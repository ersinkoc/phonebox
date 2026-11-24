using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

/// <summary>
/// Cursor integration: beforeShellExecution and beforeMCPExecution hooks in ~/.cursor/hooks.json post each command or
/// MCP tool call the Cursor agent is about to run to the agent, which shows it on the phone and returns Allow / Deny as
/// the hook's output. No answer means an empty output, and Cursor goes on as it would without the hook (its own
/// auto-run rules and approval prompt). Unlike Claude Code's PermissionRequest, these hooks run before every command,
/// including ones Cursor would run by itself, so the phone is asked only while "Ask on the phone" for Cursor is on.
///
/// A project opened over Remote - WSL runs its hooks inside the distro, from the distro's ~/.cursor/hooks.json, so the
/// hook is installed there too (with WSL watching on for Cursor): its curl reaches this agent on 127.0.0.1 when WSL
/// shares the loopback with Windows (networkingMode=mirrored); otherwise curl fails and Cursor goes on as usual.
/// </summary>
public static class CursorHook
{
    public const string Endpoint = "/api/hooks/cursor/permission";
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string HooksPath = Path.Combine(Home, ".cursor", "hooks.json");
    internal static readonly string[] Events = ["beforeShellExecution", "beforeMCPExecution"];

    /// <summary>Handles one hook call; returns the JSON for the hook to print, or null for "no decision".</summary>
    public static async Task<object?> HandleAsync(JsonElement input, ConfigStore cfg, ApprovalStore approvals,
        SessionMonitor sessions, CancellationToken aborted)
    {
        if (!cfg.Get().Cursor.PhoneApprovals || ToPermissionRequest(input) is not { } request) return null;
        // the chat's conversation_id is the folder its transcript lives in, which is the Cursor session's id
        var decision = await ClaudeHook.DecideAsync(request, cfg, approvals, sessions, aborted, "Cursor", "cursor:", allowQuestions: false);
        return Output(decision);
    }

    /// <summary>Cursor's answer for a phone decision: allow (also for Allow always, which Cursor has no rule for), deny, or null.</summary>
    internal static object? Output(string? decision) => decision switch
    {
        "allow" or "always" => new { permission = "allow" },
        "deny" => new
        {
            permission = "deny",
            user_message = "Denied from the PhoneBox panel.",
            agent_message = "The user denied this from the PhoneBox panel on their phone.",
        },
        _ => null,
    };

    /// <summary>
    /// A Cursor hook input as the PermissionRequest shape the approval flow reads: a shell command becomes "Bash" with
    /// its command, an MCP call "mcp__&lt;server&gt;__&lt;tool&gt;" with its arguments. Null for any other event.
    /// </summary>
    internal static JsonElement? ToPermissionRequest(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        var cwd = Str(input, "cwd") is { Length: > 0 } c ? c
            // Cursor can send an empty cwd: the workspace folder is the next best thing for the project name
            : input.TryGetProperty("workspace_roots", out var roots) && roots.ValueKind == JsonValueKind.Array
              && roots.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.String } first ? first.GetString() ?? "" : "";
        var request = new JsonObject
        {
            ["cwd"] = cwd,
            ["session_id"] = Str(input, "conversation_id") ?? "",
        };
        switch (Str(input, "hook_event_name"))
        {
            case "beforeShellExecution":
                if (Str(input, "command") is not { Length: > 0 } command) return null;
                request["tool_name"] = "Bash";
                request["tool_input"] = new JsonObject { ["command"] = command };
                break;
            case "beforeMCPExecution":
                var server = Str(input, "mcp_server_name") ?? "mcp";
                var tool = Str(input, "tool_name") ?? "tool";
                request["tool_name"] = $"mcp__{server}__{tool}";
                request["tool_input"] = ToolInput(input);
                break;
            default:
                return null;
        }
        return JsonSerializer.SerializeToElement(request);
    }

    // tool_input is a JSON string in beforeMCPExecution; an object when it parses as one, else shown as it came
    private static JsonObject ToolInput(JsonElement input)
    {
        if (!input.TryGetProperty("tool_input", out var ti)) return [];
        if (ti.ValueKind == JsonValueKind.Object) return JsonNode.Parse(ti.GetRawText()) as JsonObject ?? [];
        if (ti.ValueKind != JsonValueKind.String) return [];
        var text = ti.GetString() ?? "";
        try { if (JsonNode.Parse(text) is JsonObject obj) return obj; }
        catch (JsonException) { }
        return new JsonObject { ["input"] = text };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------------------------------------------------------------- install / remove the hooks in ~/.cursor/hooks.json

    /// <summary>
    /// The hook's command. Cursor on Windows runs it in PowerShell and pipes the request in, so it posts with
    /// Invoke-WebRequest (any PowerShell from 5.1, no curl quoting). Its stdout must be the answer or nothing: Cursor
    /// blocks the command on anything it cannot parse, so the progress stream (which Windows PowerShell writes out as
    /// CLIXML when its output is redirected) is turned off, a failure prints nothing, and the exit code is always 0 —
    /// all of which leave the decision to Cursor. Inside WSL (Linux, macOS) it is the Claude Code hook's curl, which
    /// also always exits 0.
    /// </summary>
    internal static string HookCommand(int port, bool windows) => windows
        // stdin read as UTF-8 (Cursor sends UTF-8 JSON; [Console]::In would decode it with the console's OEM code page)
        ? "$ProgressPreference = 'SilentlyContinue'; " +
          "$b = (New-Object IO.StreamReader([Console]::OpenStandardInput(), (New-Object Text.UTF8Encoding $false))).ReadToEnd(); " +
          "try { $r = Invoke-WebRequest -UseBasicParsing -Method Post " +
          $"-Uri 'http://127.0.0.1:{port}{Endpoint}' -ContentType 'application/json; charset=utf-8' " +
          "-Body ([Text.Encoding]::UTF8.GetBytes($b)) -TimeoutSec 3600; [Console]::Out.Write($r.Content) } catch { }; exit 0"
        // `|| true`: with the agent unreachable (WSL without mirrored networking) curl exits 7; Cursor would let the
        // command run anyway, but log the hook as failed every time
        : $"curl -s --noproxy 127.0.0.1 --max-time 3600 -X POST http://127.0.0.1:{port}{Endpoint} -H \"Content-Type: application/json\" --data-binary \"@-\" || true";

    /// <summary>Where the hook goes: the Windows (or native) profile, and with <paramref name="wsl"/> the running
    /// distros' homes in which Cursor's server has been used (a ~/.cursor folder).</summary>
    public static IReadOnlyList<(string Path, bool Windows)> Targets(bool wsl)
    {
        var targets = new List<(string, bool)> { (HooksPath, OperatingSystem.IsWindows()) };
        if (wsl)
            foreach (var home in WslHomes.Running())
            {
                var cursor = Path.Combine(home.Home, ".cursor");
                try { if (Directory.Exists(cursor)) targets.Add((Path.Combine(cursor, "hooks.json"), false)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        return targets;
    }

    /// <summary>Where the hook is: the WSL distros are always looked at, so a hook installed there before WSL watching
    /// was turned off still shows (and is removed). A distro without the hook is listed only if it has one.</summary>
    public static object Status()
    {
        // "installed" = any PhoneBox entry is there, also a partial one (one event kept by hand): it still asks the
        // phone, so the page must offer to remove it
        var paths = Targets(wsl: true).Where(t => t.Path == HooksPath || HasAny(t.Path))
            .Select(t => new { path = t.Path, installed = HasAny(t.Path), complete = Installed(t.Path) }).ToList();
        return new { installed = paths.Any(p => p.installed), paths };
    }

    /// <summary>Installs the hook where <see cref="Targets"/> names (the distros only with WSL watching on) or removes
    /// it from every place it may be; the first error, or null.</summary>
    public static string? ConfigureAll(bool install, int port, int timeoutSec, bool wsl) =>
        ConfigureAll(install, port, timeoutSec, Targets(wsl: !install || wsl));

    /// <summary>Every target is tried, also after one failed: one unreadable hooks.json must not leave the others
    /// installed when removing, or not installed when installing.</summary>
    internal static string? ConfigureAll(bool install, int port, int timeoutSec, IReadOnlyList<(string Path, bool Windows)> targets)
    {
        string? error = null;
        foreach (var (path, windows) in targets)
        {
            var e = Configure(install, port, timeoutSec, path, windows);
            if (e is not null) error ??= $"{path}: {e}";
        }
        return error;
    }

    /// <summary>Both events carry the PhoneBox hook.</summary>
    internal static bool Installed(string path) => Count(path) == Events.Length;

    /// <summary>At least one event carries the PhoneBox hook.</summary>
    internal static bool HasAny(string path) => Count(path) > 0;

    // how many of the events carry the PhoneBox hook
    private static int Count(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return root is JsonObject obj && obj["hooks"] is JsonObject hooks
                ? Events.Count(ev => hooks[ev] is JsonArray list && list.Any(IsOurs))
                : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// A hook PhoneBox wrote: a command posting to its endpoint (any port) in one of the two forms <see cref="HookCommand"/>
    /// writes. A hook of the user's own that posts to the same endpoint is not ours and is never removed.
    /// </summary>
    internal static bool IsOurs(JsonNode? hook)
    {
        if (!ClaudeHook.IsOurs(hook, Endpoint) || hook!["command"]?.GetValue<string>() is not { } cmd) return false;
        var port = System.Text.RegularExpressions.Regex.Match(cmd, @"127\.0\.0\.1:(\d{1,5})" + System.Text.RegularExpressions.Regex.Escape(Endpoint));
        return port.Success && int.TryParse(port.Groups[1].Value, out var p)
            && (cmd == HookCommand(p, windows: false) || cmd == HookCommand(p, windows: true));
    }

    /// <summary>Adds (or, with install = false, removes) the PhoneBox hooks, keeping everything else and a backup.</summary>
    internal static string? Configure(bool install, int port, int timeoutSec, string path, bool windows)
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
                           ?? throw new InvalidDataException("hooks.json is not a JSON object");
                    // owner-only like ClaudeHook's: other hooks' commands may carry secrets
                    AppPaths.WriteAllTextPrivate(path + ".phonebox.bak", text);
                }
            }
            else if (!install) return null; // nothing to remove
            else root = new JsonObject();

            // a value of another shape is the user's to fix: replacing or dropping it would lose what they wrote
            if (root["hooks"] is { } hooksNode && hooksNode is not JsonObject)
                throw new InvalidDataException("\"hooks\" in hooks.json is not a JSON object");
            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            foreach (var ev in Events)
            {
                if (hooks[ev] is { } listNode && listNode is not JsonArray)
                    throw new InvalidDataException($"\"hooks.{ev}\" in hooks.json is not a list");
                var list = hooks[ev] as JsonArray ?? new JsonArray();
                foreach (var h in list.Where(IsOurs).ToList()) list.Remove(h);
                if (install)
                    list.Add(new JsonObject
                    {
                        ["command"] = HookCommand(port, windows),
                        // the phone's time to answer plus a margin, so Cursor does not give up on a pending request
                        ["timeout"] = Math.Clamp(timeoutSec, 10, 3600) + 15,
                    });
                if (list.Count > 0) { if (!ReferenceEquals(hooks[ev], list)) hooks[ev] = list; }
                else hooks.Remove(ev);
            }
            if (hooks.Count > 0) { if (!ReferenceEquals(root["hooks"], hooks)) root["hooks"] = hooks; }
            else root.Remove("hooks");
            // Cursor ignores a file without a version
            if (root["hooks"] is not null && root["version"] is null) root["version"] = 1;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            AppPaths.WriteAllTextPrivate(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
