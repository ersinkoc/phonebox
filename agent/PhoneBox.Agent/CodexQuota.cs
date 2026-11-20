using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>Read-only account quota RPC. No thread or inference turn is created.</summary>
internal static class CodexQuota
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static string? Find() => Find(Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());

    /// <summary>
    /// On Windows, npm writes a POSIX shell script named `codex` next to `codex.cmd`.
    /// Accept only `.exe` first and then `.cmd` on Windows so the shell script is never selected.
    /// </summary>
    internal static string? Find(string? pathEnv, bool isWindows)
    {
        if (!isWindows) return ToolProcess.Find("codex");
        var dirs = (pathEnv ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim('"', ' '))
            .Where(d => d.Length > 0)
            .ToArray();
        foreach (var name in new[] { "codex.exe", "codex.cmd" })
            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    public static Task<QuotaProvider> ReadAsync(string path, CancellationToken ct) => ReadAsync(StartInfo(path), Timeout, ct);

    internal static ProcessStartInfo StartInfo(string path) => StartInfo(path, OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture);

    internal static ProcessStartInfo StartInfo(string path, bool isWindows, Architecture arch)
    {
        // npm's Windows shim sits beside node_modules/@openai/codex. Prefer the bundled native codex.exe
        // so Node isn't needed; fall back to Node + bin/codex.js without invoking cmd.exe.
        var shim = isWindows && path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        ProcessStartInfo start;
        if (shim)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var nativeExe = FindNativeNpmBinary(directory, arch);
            if (nativeExe is not null)
            {
                start = new ProcessStartInfo(nativeExe);
            }
            else
            {
                var script = Path.Combine(directory, "node_modules", "@openai", "codex", "bin", "codex.js");
                var bundledNode = Path.Combine(directory, "node.exe");
                var node = File.Exists(bundledNode) ? bundledNode : ToolProcess.Find("node");
                if (node is null || !File.Exists(script))
                    throw new IOException("Could not locate the npm Codex binary or Node entry point");
                start = new ProcessStartInfo(node);
                start.ArgumentList.Add(script);
            }
        }
        else start = new ProcessStartInfo(path);
        start.ArgumentList.Add("app-server");
        // Stdio is the default transport, including CLI versions before --stdio was added.
        return start;
    }

    private static string? FindNativeNpmBinary(string directory, Architecture arch)
    {
        var (pkgArch, triple) = arch == Architecture.Arm64
            ? ("arm64", "aarch64-pc-windows-msvc")
            : ("x64", "x86_64-pc-windows-msvc");
        var candidates = new[]
        {
            Path.Combine(directory, "node_modules", "@openai", "codex", "vendor", triple, "codex", "codex.exe"),
            Path.Combine(directory, "node_modules", "@openai", $"codex-win32-{pkgArch}", "vendor", triple, "codex", "codex.exe"),
            Path.Combine(directory, "node_modules", "@openai", "codex", "node_modules", "@openai", $"codex-win32-{pkgArch}", "vendor", triple, "codex", "codex.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    internal static async Task<QuotaProvider> ReadAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.WorkingDirectory = AppPaths.NeutralCwd;
        using var process = Process.Start(start) ?? throw new IOException("Could not start Codex app-server");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        // Drain diagnostics without retaining potentially sensitive CLI output.
        var error = DrainAsync(process.StandardError, deadline.Token);
        try
        {
            await Send(new { id = 1, method = "initialize", @params = new
            {
                clientInfo = new { name = "phonebox", title = "PhoneBox", version = AppPaths.Version }
            } });
            await Response(1);
            await Send(new { method = "initialized" });
            await Send(new { id = 2, method = "account/rateLimits/read", @params = new
            {
                excludeResetCreditDetails = true, supportsLunaReserve = false
            } });
            return Parse(await Response(2), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Codex quota request timed out");
        }
        finally
        {
            // App-server can stay alive after stdin closes; reap it on success and every failure
            // without letting cleanup failures turn a successful read into an error.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
            try { await deadline.CancelAsync(); }
            catch { }
            try { await error; }
            catch { }
        }

        async Task Send<T>(T message)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
        }

        async Task<JsonElement> Response(int id)
        {
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; } // Some CLI versions write diagnostics to stdout.
                using (doc)
                {
                    var root = doc.RootElement;
                    // Ignore server-initiated requests/notifications even if they reuse id 1 or 2.
                    if (Prop(root, "method").ValueKind != JsonValueKind.Undefined) continue;
                    var responseId = Prop(root, "id");
                    if (responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var number) || number != id) continue;
                    var hasError = Prop(root, "error").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
                    JsonElement result = default;
                    var hasResult = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out result);
                    if (!hasError && !hasResult) continue;
                    if (hasError)
                        throw new IOException("Codex app-server rejected " + (id == 1 ? "initialize" : "account/rateLimits/read"));
                    if (result.ValueKind != JsonValueKind.Object) throw new IOException("Codex app-server returned an invalid result");
                    return result.Clone();
                }
            }
            throw new IOException("Codex app-server exited before returning quota");
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), ct) > 0) { }
    }

    internal static QuotaProvider Parse(JsonElement result, long now)
    {
        var rate = Prop(Prop(result, "rateLimitsByLimitId"), "codex");
        if (rate.ValueKind != JsonValueKind.Object) rate = Prop(result, "rateLimits");
        var limits = new List<QuotaLimit>();
        foreach (var key in new[] { "primary", "secondary" })
        {
            var window = Prop(rate, key);
            if (Number(window, "usedPercent") is not { } percent) continue;
            percent = Math.Max(0, percent);
            var minutes = Number(window, "windowDurationMins");
            var label = minutes switch
            {
                300 => "5-hour", 10080 => "Weekly", >= 1440 => $"{minutes / 1440:0.#}-day",
                > 0 => $"{minutes / 60:0.#}-hour", _ => key
            };
            var reset = Number(window, "resetsAt");
            limits.Add(new QuotaLimit(label, percent,
                reset is >= 0 and <= 253402300799 ? (long)(reset.Value * 1000) : null,
                minutes is > 0 and <= 525600 ? (long)(minutes.Value * 60) : null,
                percent >= 95 ? "critical" : percent >= 80 ? "warning" : "normal"));
        }
        if (limits.Count == 0) throw new JsonException("Codex returned no measured quota limits");
        var credits = Prop(rate, "credits");
        string? extra = Prop(credits, "unlimited").ValueKind == JsonValueKind.True ? "Credits: unlimited"
            : Prop(credits, "hasCredits").ValueKind == JsonValueKind.False ? "Credits not enabled"
            : Text(credits, "balance") is { } balance && double.TryParse(balance, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                ? "Credits: " + value.ToString("#,0.##", CultureInfo.InvariantCulture) : null;
        var reached = limits.Any(l => l.Percent >= 100)
            || Prop(rate, "rateLimitReachedType").ValueKind == JsonValueKind.String
            || Prop(rate, "spendControlReached").ValueKind == JsonValueKind.True
            || Prop(result, "ordinaryUsageAllowed").ValueKind == JsonValueKind.False;
        if (reached) extra = extra is null ? "Limit reached" : "Limit reached · " + extra;
        var plan = Text(rate, "planType")?.Replace('_', ' ').Trim();
        return new QuotaProvider("codex", "Codex", string.IsNullOrEmpty(plan) ? "Codex" : char.ToUpperInvariant(plan[0]) + plan[1..],
            limits, extra, now, null, Source: "codex-app-server");
    }

    private static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) ? value : default;
    private static string? Text(JsonElement e, string name) => Prop(e, name) is var value && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? Number(JsonElement e, string name) => Prop(e, name) is var value && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
}
