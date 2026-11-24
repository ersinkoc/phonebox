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
/// shares the loopback with Windows (networkingMode=mirrored); otherwise curl fails and Cursor goes on as usual. The
/// files it went to are remembered (<see cref="WslInstallsPath"/>), so the distros are looked at only with WSL watching
/// on or after an install there, and Remove still finds those hooks once WSL watching is turned off.
/// </summary>
public static class CursorHook
{
    public const string Endpoint = "/api/hooks/cursor/permission";
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string HooksPath = Path.Combine(Home, ".cursor", "hooks.json");
    internal static readonly string[] Events = ["beforeShellExecution", "beforeMCPExecution"];
    /// <summary>The hooks.json files inside WSL distros the hook was installed to (a JSON list of paths).</summary>
    internal static readonly string WslInstallsPath = Path.Combine(AppPaths.ConfigDir, "cursor-hook-wsl.json");

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

    /// <summary>
    /// Where the hook is, for the settings page: the Windows (or native) profile, and the running distros' files that
    /// have it. WSL is looked at only with <paramref name="wsl"/> (Also watch Cursor in WSL) on, or when the hook was
    /// installed in a distro before; otherwise loading the page runs no wsl.exe and reads no \\wsl.localhost share.
    /// </summary>
    public static object Status(bool wsl) => Status(wsl, HooksPath, () => WslHomes.Running(), WslInstallsPath);

    internal static object Status(bool wsl, string native, Func<IReadOnlyList<WslHomes.Entry>> running, string installsPath)
    {
        // "installed" = any PhoneBox entry is there, also a partial one (one event kept by hand): it still asks the
        // phone, so the page must offer to remove it. A distro's file is listed only if it has one.
        var paths = new[] { native }.Concat(WslTargets(wsl, running, Remembered(installsPath)).Running)
            .Select(p => new { path = p, installed = HasAny(p), complete = Installed(p) })
            .Where(p => p.path == native || p.installed).ToList();
        return new { installed = paths.Any(p => p.installed), paths };
    }

    /// <summary>
    /// Installs the hook in the native profile and, with <paramref name="wsl"/> on, in the running distros where Cursor's
    /// server has been used. Removing takes it from the native profile and from every distro it was installed in (also
    /// with <paramref name="wsl"/> off now), and with <paramref name="wsl"/> on from the other running distros too.
    /// Returns the first error, or null.
    /// </summary>
    public static string? ConfigureAll(bool install, int port, int timeoutSec, bool wsl) =>
        ConfigureAll(install, port, timeoutSec, wsl, (HooksPath, OperatingSystem.IsWindows()), () => WslHomes.Running(), WslInstallsPath);

    internal static string? ConfigureAll(bool install, int port, int timeoutSec, bool wsl, (string Path, bool Windows) native,
        Func<IReadOnlyList<WslHomes.Entry>> running, string installsPath)
    {
        var remembered = Remembered(installsPath);
        var (inWsl, stopped) = install && !wsl ? (new List<string>(), new List<string>()) : WslTargets(wsl, running, remembered);
        var done = new List<string>();
        var error = ConfigureAll(install, port, timeoutSec, [native, .. inWsl.Select(p => (p, false))], done.Add);
        // a stopped distro is not started to remove the hook: it stays remembered, and Remove takes it off once the
        // distro runs (the page lists it again then)
        if (!install && stopped.Count > 0) error ??= $"{stopped[0]}: not removed, its WSL distro is not running";
        // only a distro file that was written changes the record: one that failed (a share not answering) keeps its place
        var written = inWsl.Where(p => done.Contains(p, PathComparer)).ToList();
        var next = (install ? remembered.Concat(written) : remembered.Where(p => !written.Contains(p, PathComparer)))
            .Distinct(PathComparer).ToList();
        if (!next.SequenceEqual(remembered, PathComparer))
        {
            try
            {
                if (next.Count == 0) File.Delete(installsPath);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(installsPath)!);
                    AppPaths.WriteAllTextPrivate(installsPath, JsonSerializer.Serialize(next));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error ??= $"{installsPath}: {ex.Message}"; }
        }
        return error;
    }

    /// <summary>
    /// The distros' hooks.json files to look at, in running distros only (opening a stopped one's share would start it):
    /// with <paramref name="wsl"/> each one whose home has a ~/.cursor folder (Cursor's server has been used there), and
    /// always the ones the hook was installed to. Without either WSL is not asked at all. Stopped: the remembered files
    /// whose distro is not running.
    /// </summary>
    internal static (List<string> Running, List<string> Stopped) WslTargets(bool wsl, Func<IReadOnlyList<WslHomes.Entry>> running,
        IReadOnlyList<string> remembered)
    {
        if (!wsl && remembered.Count == 0) return ([], []);
        var targets = new List<string>();
        var stopped = remembered.ToList();
        foreach (var home in running())
        {
            if (wsl)
            {
                var cursor = Path.Combine(home.Home, ".cursor");
                try { if (Directory.Exists(cursor)) targets.Add(Path.Combine(cursor, "hooks.json")); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            var root = home.Root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (var p in remembered.Where(p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            {
                targets.Add(p);
                stopped.Remove(p);
            }
        }
        return (targets.Distinct(PathComparer).ToList(), stopped);
    }

    // \\wsl.localhost paths: Windows compares them without case
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>The distros' hooks.json files the hook was installed to, as remembered; empty when there is no record.</summary>
    internal static IReadOnlyList<string> Remembered(string installsPath)
    {
        try
        {
            return File.Exists(installsPath) && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(installsPath)) is { } list
                ? list.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(PathComparer).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    /// <summary>Every target is tried, also after one failed: one unreadable hooks.json must not leave the others
    /// installed when removing, or not installed when installing.</summary>
    internal static string? ConfigureAll(bool install, int port, int timeoutSec, IReadOnlyList<(string Path, bool Windows)> targets,
        Action<string>? done = null)
    {
        string? error = null;
        foreach (var (path, windows) in targets)
        {
            var e = Configure(install, port, timeoutSec, path, windows);
            if (e is not null) error ??= $"{path}: {e}";
            else done?.Invoke(path);
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
    /// A PhoneBox hook: a command posting to 127.0.0.1:&lt;any port&gt;<see cref="Endpoint"/>, as for Claude Code's
    /// (<see cref="ClaudeHook.IsOurs"/>). The endpoint, not the command text, identifies it, so a hook written by an
    /// earlier release with another command is still found (no second hook on reinstall, and Remove takes it off).
    /// </summary>
    internal static bool IsOurs(JsonNode? hook) => ClaudeHook.IsOurs(hook, Endpoint);

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
