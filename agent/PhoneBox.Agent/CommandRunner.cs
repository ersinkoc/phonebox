using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace PhoneBox.Agent;

public sealed record CommandResult(string Output, long At, bool Ok, bool Running, double DurationMs);

/// <summary>
/// Runs the PowerShell commands of "command" widgets. Commands live only in config.json on this PC; the phone
/// asks for a widget by page and widget id and gets the cached output. A command runs at most once per its
/// interval, and only while a phone is actually looking at it.
/// </summary>
public sealed class CommandRunner(ConfigStore cfg, ILogger<CommandRunner> log)
{
    private const int MaxOutput = 4000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<string, CommandResult> _results = new();
    private readonly ConcurrentDictionary<string, byte> _running = new();

    /// <returns>The cached result (possibly stale or empty while the first run is in progress), or null if there is no such widget.</returns>
    public CommandResult? Get(string pageId, string widgetId, bool force = false)
    {
        var w = FindWidget(pageId, widgetId);
        if (w is null) return null;
        var command = ResolveCommand(w);
        var interval = Math.Clamp(w.Int("interval") ?? 30, 5, 86400);
        var prefix = $"{pageId}/{widgetId}:";
        var key = $"{prefix}{command.GetHashCode()}";
        // Drop the results of commands this widget no longer has. Against the settings as they are now, not the ones
        // this request read: a request that read an older command must not evict the result of the current one.
        var liveKey = FindWidget(pageId, widgetId) is { } current ? $"{prefix}{ResolveCommand(current).GetHashCode()}" : null;
        foreach (var k in _results.Keys)
            if (k.StartsWith(prefix, StringComparison.Ordinal) && k != key && k != liveKey)
                _results.TryRemove(k, out _);

        var cached = _results.GetValueOrDefault(key);
        var now = Now();
        var stale = cached is null || now - cached.At < 0 || now - cached.At >= interval * 1000L;
        if ((stale || force) && command.Trim() != "" && _running.TryAdd(key, 0))
            _ = StartWorker(async () =>
            {
                try
                {
                    // Checked again when the worker starts: the page may have been hidden, or the command changed,
                    // while this run waited to be scheduled. Checked once more for its result (below).
                    if (!StillCurrent(pageId, widgetId, command)) return;
                    var res = await RunAsync(command);
                    if (StillCurrent(pageId, widgetId, command))
                        _results[key] = res;
                }
                finally { _running.TryRemove(key, out _); }
            });
        var running = _running.ContainsKey(key);
        return cached is null
            ? new CommandResult(command.Trim() == "" ? "(no command)" : "", 0, true, running, 0)
            : cached with { Running = running };
    }

    /// <summary>Starts a command run in the background; tests replace it to hold a run between its request and its start.</summary>
    internal Func<Func<Task>, Task> StartWorker { get; init; } = work => Task.Run(work);

    private bool StillCurrent(string pageId, string widgetId, string command) =>
        FindWidget(pageId, widgetId) is { } live && ResolveCommand(live) == command;

    private WidgetConfig? FindWidget(string pageId, string widgetId) =>
        cfg.Get().Pages.FirstOrDefault(p => p.Enabled && p.Id == pageId)?.Widgets?.FirstOrDefault(x => x.Id == widgetId && x.Type == "command");

    private static string ResolveCommand(WidgetConfig w) =>
        (!OperatingSystem.IsWindows() && w.Str("commandUnix") is { } unix && !string.IsNullOrWhiteSpace(unix) ? unix : null) ?? w.Str("command") ?? "";

    private async Task<CommandResult> RunAsync(string command)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var shell = OperatingSystem.IsWindows() ? "powershell.exe" : OperatingSystem.IsMacOS() ? "/bin/zsh" : File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var psi = new ProcessStartInfo(shell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Directory.Exists(home) ? home : AppPaths.NeutralCwd,
            };
            string[] args = OperatingSystem.IsWindows()
                ? ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                   "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + command]
                : ["-lc", command]; // login shell: the user's PATH (docker, brew…) is available
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = new Capture();
            var stderr = new Capture();
            var pumps = Task.WhenAll(stdout.Pump(p.StandardOutput.BaseStream), stderr.Pump(p.StandardError.BaseStream));
            using var cts = new CancellationTokenSource(Timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new CommandResult($"timed out after {Timeout.TotalSeconds:0} s", Now(), false, false, sw.Elapsed.TotalMilliseconds);
            }
            // The shell is done. A process it left running in the background (start /b, cmd &) inherits the output pipe
            // and may keep it open for ever: wait a moment for the rest of the output, not for that process.
            await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(1)));
            var output = stdout.Text().TrimEnd();
            var err = stderr.Text().Trim();
            var ok = p.ExitCode == 0;
            if (output == "" && err != "") output = err;
            output = TruncateOutput(output, MaxOutput);
            return new CommandResult(output, Now(), ok, false, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            log.LogWarning("command widget failed: {Msg}", ex.Message);
            return new CommandResult(ex.Message, Now(), false, false, sw.Elapsed.TotalMilliseconds);
        }
    }

    internal static string TruncateOutput(string output, int maxChars = MaxOutput)
    {
        if (output.Length <= maxChars) return output;
        var cut = maxChars > 0 && char.IsHighSurrogate(output[maxChars - 1]) ? maxChars - 1 : maxChars;
        return output[..cut] + "…";
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Collects a stream's bytes (up to a cap, the rest is drained and dropped) and can be read at any time.</summary>
    private sealed class Capture
    {
        private const int Cap = 64 * 1024;
        private readonly MemoryStream _buf = new();

        public async Task Pump(Stream s)
        {
            var chunk = new byte[8192];
            try
            {
                int n;
                while ((n = await s.ReadAsync(chunk)) > 0)
                    lock (_buf) if (_buf.Length < Cap) _buf.Write(chunk, 0, (int)Math.Min(n, Cap - _buf.Length));
            }
            catch (Exception) { /* the process object was disposed while a background process still held the pipe */ }
        }

        public string Text()
        {
            lock (_buf)
            {
                var bytes = _buf.GetBuffer();
                var len = (int)_buf.Length;
                if (len == Cap) len = TrimIncompleteUtf8Tail(bytes.AsSpan(0, len));
                return Encoding.UTF8.GetString(bytes, 0, len);
            }
        }

        private static int TrimIncompleteUtf8Tail(ReadOnlySpan<byte> span)
        {
            var cont = 0;
            while (cont < 3 && cont < span.Length && (span[span.Length - 1 - cont] & 0xC0) == 0x80) cont++;
            if (cont >= span.Length) return span.Length;
            var leadIdx = span.Length - 1 - cont;
            var lead = span[leadIdx];
            if (lead < 0x80) return span.Length;
            var needed = (lead & 0xE0) == 0xC0 ? 2 : (lead & 0xF0) == 0xE0 ? 3 : (lead & 0xF8) == 0xF0 ? 4 : 1;
            return cont + 1 < needed ? leadIdx : span.Length;
        }
    }
}
