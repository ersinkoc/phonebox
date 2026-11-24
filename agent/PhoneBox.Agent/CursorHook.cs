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
    /// The hook's command. Cursor on Windows runs it in PowerShell and pipes the request in (to stdin, or as pipeline
    /// input: both are read), so it posts with Invoke-WebRequest (any PowerShell from 5.1, no curl quoting). Inside WSL (Linux, macOS) it is the Claude Code
    /// hook's curl.
    /// <para>
    /// Its stdout must be the answer or nothing. Cursor's hooks reference (https://cursor.com/docs/agent/hooks): for
    /// beforeShellExecution and beforeMCPExecution "invalid JSON or a response that doesn't match the hook's schema
    /// blocks the action", while "no output" is a hook failure, which fails open by default ("hook failures (crash,
    /// timeout, non-zero exit code, no output) block the action instead of allowing it through" only with failClosed,
    /// which PhoneBox never sets). The schema's permission values are allow, deny and ask, none of which means "no
    /// opinion" (ask would prompt even for a command Cursor runs by itself), so when there is no decision (the agent
    /// answers with an empty body, or cannot be reached) the hook prints nothing and Cursor goes on as it would without
    /// it. Hence the progress stream (which Windows PowerShell writes out as CLIXML when its output is redirected) is
    /// turned off, a failure prints nothing, and the exit code is always 0.
    /// </para>
    /// </summary>
    internal static string HookCommand(int port, bool windows) => windows
        // The request: Cursor's reference says "On Windows, the desktop app runs hook commands through PowerShell and
        // pipes the JSON into the command", and "a PowerShell script reads it from `$input`" while "other programs read
        // it from stdin". So the process's stdin is read first, as UTF-8 (Cursor sends UTF-8 JSON; PowerShell decodes
        // stdin with the console's OEM code page), and the pipeline input when stdin has nothing. The pipeline input is
        // fetched with Get-Variable: a literal $input in the command makes PowerShell read stdin into it before the
        // command runs, already decoded with the OEM code page.
        ? "$ProgressPreference = 'SilentlyContinue'; " +
          "try { $b = ''; if ([Console]::IsInputRedirected) { " +
          "$b = (New-Object IO.StreamReader([Console]::OpenStandardInput(), (New-Object Text.UTF8Encoding $false))).ReadToEnd() }; " +
          "if (-not $b.Trim()) { $e = Get-Variable -Name input -ValueOnly -ErrorAction SilentlyContinue; " +
          "if ($e) { $b = @(foreach ($l in $e) { $l }) -join ' ' } }; " +
          "$r = Invoke-WebRequest -UseBasicParsing -Method Post " +
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
        // phone, so the page must offer to remove it. A running distro's file is listed only if it has one.
        var (inWsl, stopped) = WslTargets(wsl, running, Remembered(installsPath));
        var paths = new[] { native }.Concat(inWsl)
            .Select(p =>
            {
                var n = Count(p); // one read for both flags
                return new { path = p, installed = n > 0, complete = n == Events.Length, running = true };
            })
            .Where(p => p.path == native || p.installed)
            // a stopped distro's share is not opened (that would start it): its recorded hook counts as installed, so
            // the page offers Remove, which says the distro must run first
            .Concat(stopped.Select(p => new { path = p, installed = true, complete = true, running = false }))
            .ToList();
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
        // one Install or Remove at a time: the record is read, the files written and the record updated as one step
        lock (ConfigureGate)
        {
            // an unreadable record stops here, before any hook is written or the record replaced
            if (ReadRecord(installsPath, out var remembered) is { } recordError) return recordError;
            var (inWsl, stopped) = install && !wsl ? (new List<string>(), new List<string>()) : WslTargets(wsl, running, remembered);
            string? error = null;
            if (install && inWsl.Count > 0)
            {
                // recorded before it is written, so no distro can get a hook that Remove does not know about; an entry
                // whose write then fails does no harm (the page lists it only with the hook there, Remove tries it again)
                error = Record(installsPath, remembered, remembered.Concat(inWsl));
                if (error is not null) inWsl.Clear(); // not recorded: not written either
            }
            var done = new List<string>();
            var written = ConfigureAll(install, port, timeoutSec, [native, .. inWsl.Select(p => (p, false))], done.Add,
                // a distro's file counts as removed only while its home can be read: a share that does not answer may still have it
                reachable: p => inWsl.Contains(p, PathComparer) ? Path.GetDirectoryName(Path.GetDirectoryName(p)) : null);
            error ??= written;
            // a stopped distro is not started to remove the hook: it stays recorded, and Remove takes it off once the
            // distro runs
            if (!install && stopped.Count > 0) error ??= $"{stopped[0]}: not removed, its WSL distro is not running";
            if (!install)
            {
                // only the files the hook was removed from leave the record; one that failed is tried again next time
                var e = Record(installsPath, remembered, remembered.Where(p => !done.Contains(p, PathComparer)));
                error ??= e;
            }
            return error;
        }
    }

    private static readonly Lock ConfigureGate = new();

    // writes the record when it changed (removes the file when it is empty); an error, or null
    private static string? Record(string installsPath, IReadOnlyList<string> remembered, IEnumerable<string> next)
    {
        var list = next.Distinct(PathComparer).ToList();
        if (list.SequenceEqual(remembered, PathComparer)) return null;
        try
        {
            if (list.Count == 0) File.Delete(installsPath);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(installsPath)!);
                AppPaths.WriteAllTextPrivate(installsPath, JsonSerializer.Serialize(list));
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"{installsPath}: {ex.Message}"; }
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

    /// <summary>The distros' hooks.json files the hook was installed to, as remembered; empty when there is no record
    /// or it cannot be read (for the settings page, which only shows it; Install and Remove use <see cref="ReadRecord"/>).</summary>
    internal static IReadOnlyList<string> Remembered(string installsPath) => ReadRecord(installsPath, out var list) is null ? list : [];

    /// <summary>
    /// Reads the record: null with <paramref name="list"/> filled (empty when there is no record file), or an error
    /// when the file is there but cannot be read or is not a list of paths. Install and Remove stop on that error
    /// instead of replacing the record, which would forget the distros it names.
    /// </summary>
    internal static string? ReadRecord(string installsPath, out IReadOnlyList<string> list)
    {
        list = [];
        string text;
        try { text = File.ReadAllText(installsPath); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"{installsPath}: {ex.Message}"; }
        try
        {
            var read = JsonSerializer.Deserialize<List<string?>>(text) ?? throw new JsonException("null instead of a list");
            list = read.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct(PathComparer).ToList();
            return null;
        }
        catch (JsonException ex)
        {
            return $"{installsPath}: not a list of the WSL hooks.json files PhoneBox installed to ({ex.Message}); fix or delete it";
        }
    }

    /// <summary>Every target is tried, also after one failed: one unreadable hooks.json must not leave the others
    /// installed when removing, or not installed when installing.</summary>
    internal static string? ConfigureAll(bool install, int port, int timeoutSec, IReadOnlyList<(string Path, bool Windows)> targets,
        Action<string>? done = null, Func<string, string?>? reachable = null)
    {
        string? error = null;
        foreach (var (path, windows) in targets)
        {
            var e = Configure(install, port, timeoutSec, path, windows, reachable?.Invoke(path));
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

    /// <summary>
    /// Adds (or, with install = false, removes) the PhoneBox hooks, keeping everything else and a backup. With
    /// <paramref name="reachable"/> (a distro's home), a missing file counts as nothing to remove only while that folder
    /// can be read: a share that stopped answering could still hold the hook.
    /// </summary>
    internal static string? Configure(bool install, int port, int timeoutSec, string path, bool windows, string? reachable = null)
    {
        try
        {
            JsonObject root;
            // read directly: File.Exists also answers false when the share or the file cannot be reached
            string? text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { text = null; }
            if (text is not null)
            {
                if (string.IsNullOrWhiteSpace(text)) root = new JsonObject();
                else
                {
                    root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                           ?? throw new InvalidDataException("hooks.json is not a JSON object");
                    // owner-only like ClaudeHook's: other hooks' commands may carry secrets
                    AppPaths.WriteAllTextPrivate(path + ".phonebox.bak", text);
                }
            }
            else if (!install)
                // nothing to remove, unless the folder that should hold it cannot be read either
                return reachable is null || Directory.Exists(reachable) ? null : $"not removed, {reachable} cannot be reached";
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
