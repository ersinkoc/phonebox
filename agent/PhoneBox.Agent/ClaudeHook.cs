using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

/// <summary>
/// Claude Code integration: a PermissionRequest hook in ~/.claude/settings.json posts each permission prompt to the
/// agent, which shows it on the phone and returns Allow / Deny as the hook's output. No answer means no output, and
/// Claude Code asks in the terminal as usual.
/// </summary>
public static class ClaudeHook
{
    public const string Endpoint = "/api/hooks/claude/permission";
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string SettingsPath = Path.Combine(Home, ".claude", "settings.json");

    /// <summary>Handles one hook call; returns the JSON for the hook to print, or null for "no decision".</summary>
    public static async Task<object?> HandleAsync(JsonElement input, ConfigStore cfg, ApprovalStore approvals, SessionMonitor sessions, CancellationToken aborted)
        => await HandlePermissionAsync(input, cfg, approvals, sessions, aborted, "Claude Code", "claude:", allowQuestions: true);

    /// <summary>Shared PermissionRequest protocol used by Claude Code, Codex and compatible agents.</summary>
    internal static async Task<object?> HandlePermissionAsync(JsonElement input, ConfigStore cfg, ApprovalStore approvals,
        SessionMonitor sessions, CancellationToken aborted, string source, string? sessionPrefix, bool allowQuestions)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        var c = cfg.Get().Claude;
        if (!c.PhoneApprovals) return null;
        if (c.OnlyWhenAway && Idle.Seconds() < 60) return null;

        var tool = Str(input, "tool_name") ?? "a tool";
        var ti = input.TryGetProperty("tool_input", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
        var cwd = (Str(input, "cwd") ?? "").Trim();
        var sessionId = Str(input, "session_id") ?? "";
        var session = sessionPrefix is null ? null : sessions.Snapshot().FirstOrDefault(s => s.Id == sessionPrefix + sessionId);
        var project = session?.Name ?? ProjectName(cwd);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // AskUserQuestion is a question, not a permission: Allow would only open the same question in the terminal.
        var question = allowQuestions && tool == "AskUserQuestion";
        var shownCwd = cwd.Length <= 1000 ? cwd : "…" + cwd[^999..];
        var a = new Approval(ApprovalStore.NewId(), source, sessionId, project, shownCwd, ToolTitle(tool), question ? QuestionDetail(ti) : Detail(tool, ti),
            ti.ValueKind == JsonValueKind.Object ? Str(ti, "description") : null, now, now + Math.Clamp(c.TimeoutSec, 10, 3600) * 1000L,
            question ? "question" : "permission");

        var decision = await approvals.AskAsync(a, aborted);
        if (decision is null or "skip") return null; // no decision: Claude Code asks in the terminal as usual
        return new
        {
            hookSpecificOutput = new
            {
                hookEventName = "PermissionRequest",
                decision = decision == "allow"
                    ? (object)new { behavior = "allow" }
                    : new { behavior = "deny", message = "Denied from the PhoneBox panel." },
            },
        };
    }

    /// <summary>
    /// What the panel shows as the project: the workspace name WorkBuddy remembers for a directory (its sessions are
    /// invisible to <see cref="SessionMonitor"/>), otherwise the folder name as before.
    /// </summary>
    internal static string ProjectName(string cwd, string? homeDir = null) =>
        WorkBuddyNames.For(cwd, homeDir) ?? Path.GetFileName(cwd.TrimEnd('\\', '/'));

    private static string ToolTitle(string tool) => tool switch
    {
        "Bash" => "Run a command",
        "Edit" or "MultiEdit" => "Edit a file",
        "Write" => "Write a file",
        "NotebookEdit" => "Edit a notebook",
        "WebFetch" => "Fetch a web page",
        "WebSearch" => "Search the web",
        "Read" => "Read a file",
        "AskUserQuestion" => "Claude has a question",
        _ when tool.StartsWith("mcp__") => "Use " + tool[5..].Replace("__", " › "),
        _ => "Use " + tool,
    };

    private static string Detail(string tool, JsonElement ti)
    {
        if (ti.ValueKind != JsonValueKind.Object) return "";
        string? s = tool switch
        {
            "Bash" => Str(ti, "command"),
            "Edit" or "MultiEdit" or "Read" or "NotebookEdit" => Str(ti, "file_path") ?? Str(ti, "notebook_path"),
            "Write" => Str(ti, "file_path") is { } f ? f + "\n\n" + Head(Str(ti, "content") ?? "", 12) : null,
            "WebFetch" => Str(ti, "url"),
            "WebSearch" => Str(ti, "query"),
            _ => null,
        };
        if (tool is "Edit" && s is not null && Str(ti, "new_string") is { } ns) s += "\n\n" + Head(ns, 12);
        s ??= JsonSerializer.Serialize(ti, new JsonSerializerOptions { WriteIndented = true });
        return s.Length > 3000 ? s[..3000] + "…" : s;
    }

    /// <summary>The questions and their choices as plain text: "Header: Question" and one line per choice.</summary>
    internal static string QuestionDetail(JsonElement ti)
    {
        var sb = new System.Text.StringBuilder();
        if (ti.ValueKind == JsonValueKind.Object && ti.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
            foreach (var q in qs.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.Object) continue;
                if (sb.Length > 0) sb.Append("\n\n");
                var header = Str(q, "header");
                sb.Append(header is { Length: > 0 } ? header + ": " : "").Append(Str(q, "question") ?? "");
                if (q.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True) sb.Append("  (more than one can be picked)");
                if (q.TryGetProperty("options", out var os) && os.ValueKind == JsonValueKind.Array)
                    foreach (var o in os.EnumerateArray())
                    {
                        if (o.ValueKind != JsonValueKind.Object || Str(o, "label") is not { Length: > 0 } label) continue;
                        var d = Str(o, "description");
                        sb.Append("\n  • ").Append(label).Append(d is { Length: > 0 } ? " — " + d : "");
                    }
            }
        var s = sb.ToString();
        if (s.Length == 0) s = "Claude is asking something; answer it in the terminal on your PC.";
        return s.Length > 3000 ? s[..3000] + "…" : s;
    }

    private static string Head(string text, int lines)
    {
        var l = text.Split('\n');
        return string.Join('\n', l.Take(lines)) + (l.Length > lines ? $"\n… ({l.Length - lines} more lines)" : "");
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------------------------------------------------------------- install / remove the hook in ~/.claude/settings.json

    // --noproxy: a proxy in the user's curl settings (or *_proxy variables) must never receive the request, which
    // carries the command and paths the agent asks about
    private static string HookCommand(int port, string endpoint) =>
        $"{(OperatingSystem.IsWindows() ? "curl.exe" : "curl")} -s --noproxy 127.0.0.1 --max-time 3600 -X POST http://127.0.0.1:{port}{endpoint} -H \"Content-Type: application/json\" --data-binary \"@-\"";

    private static bool IsOurs(JsonNode? hook, string endpoint)
    {
        if (hook is not JsonObject obj || obj["command"] is not JsonValue v || !v.TryGetValue<string>(out var cmd))
            return false;
        const string hostPrefix = "127.0.0.1:";
        for (var pos = 0; (pos = cmd.IndexOf(hostPrefix, pos, StringComparison.Ordinal)) >= 0; pos += hostPrefix.Length)
        {
            var i = pos + hostPrefix.Length;
            var digitsStart = i;
            while (i < cmd.Length && char.IsAsciiDigit(cmd[i])) i++;
            // the endpoint must end there: /api/hooks/codex/permission-audit is someone else's hook
            if (i > digitsStart && cmd.AsSpan(i).StartsWith(endpoint.AsSpan(), StringComparison.Ordinal)
                && (i + endpoint.Length == cmd.Length || !IsPathChar(cmd[i + endpoint.Length])))
                return true;
        }
        return false;
    }

    private static bool IsPathChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '~' or '%';

    public static bool Installed() => Installed(SettingsPath);

    internal static bool Installed(string path)
        => Installed(path, Endpoint);

    internal static bool Installed(string path, string endpoint)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return root is JsonObject obj
                && obj["hooks"] is JsonObject hooks
                && hooks["PermissionRequest"] is JsonArray groups
                && groups.Any(g => g is JsonObject go && go["hooks"] is JsonArray hs && hs.Any(h => IsOurs(h, endpoint)));
        }
        catch { return false; }
    }

    /// <summary>Adds (or, with install = false, removes) the PhoneBox hook, keeping everything else and a backup.</summary>
    public static string? Configure(bool install, int port, int timeoutSec) =>
        Configure(install, port, timeoutSec, SettingsPath);

    internal static string? Configure(bool install, int port, int timeoutSec, string path)
        => Configure(install, port, timeoutSec, path, Endpoint);

    internal static string? Configure(bool install, int port, int timeoutSec, string path, string endpoint)
    {
        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text)) root = new JsonObject();
                else
                {
                    root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                           ?? throw new InvalidDataException("settings.json is not a JSON object");
                    // owner-only like the replacement below: the file can hold API keys (its "env" section)
                    AppPaths.WriteAllTextPrivate(path + ".phonebox.bak", text);
                }
            }
            else root = new JsonObject();

            // a value of another shape is the user's to fix: replacing or dropping it would lose what they wrote
            if (root["hooks"] is { } hooksNode && hooksNode is not JsonObject)
                throw new InvalidDataException("\"hooks\" in settings.json is not a JSON object");
            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            if (hooks["PermissionRequest"] is { } groupsNode && groupsNode is not JsonArray)
                throw new InvalidDataException("\"hooks.PermissionRequest\" in settings.json is not a list");
            var groups = hooks["PermissionRequest"] as JsonArray ?? new JsonArray();
            // drop our old entries (and groups that become empty)
            foreach (var g in groups.ToList())
            {
                if (g is not JsonObject go || go["hooks"] is not JsonArray hs) continue;
                foreach (var h in hs.Where(h => IsOurs(h, endpoint)).ToList()) hs.Remove(h);
                if (hs.Count == 0) groups.Remove(g);
            }
            if (install)
                groups.Add(new JsonObject
                {
                    ["matcher"] = "*",
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = HookCommand(port, endpoint),
                        ["timeout"] = Math.Clamp(timeoutSec, 10, 3600) + 15,
                    }),
                });
            if (groups.Count > 0)
            {
                if (!ReferenceEquals(hooks["PermissionRequest"], groups)) hooks["PermissionRequest"] = groups;
            }
            else hooks.Remove("PermissionRequest");
            if (hooks.Count > 0)
            {
                if (!ReferenceEquals(root["hooks"], hooks)) root["hooks"] = hooks;
            }
            else root.Remove("hooks");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            // created owner-only: moving a 0644 file over the original would widen a private settings file
            AppPaths.WriteAllTextPrivate(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}

/// <summary>How long the PC's user has not touched the keyboard or mouse.</summary>
public static class Idle
{
    public static double Seconds()
    {
#if WINDOWS
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? (Environment.TickCount - (int)info.dwTime) / 1000.0 : 0;
#else
        if (OperatingSystem.IsMacOS())
        {
            // HIDIdleTime is in nanoseconds
            var io = Sh.Run("ioreg", "-c", "IOHIDSystem", "-d", "4") ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(io, @"""HIDIdleTime"" = (\d+)");
            return m.Success ? long.Parse(m.Groups[1].Value) / 1e9 : 0;
        }
        // X11 only (xprintidle); on Wayland this reports 0, i.e. "at the PC"
        return double.TryParse(Sh.Run("xprintidle")?.Trim(), out var ms) ? ms / 1000 : 0;
#endif
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
