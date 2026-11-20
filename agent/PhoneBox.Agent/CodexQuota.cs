using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>Read-only account quota RPC. No thread or inference turn is created.</summary>
internal static class CodexQuota
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static string? Find() => ToolProcess.Find("codex")
        ?? (OperatingSystem.IsWindows() ? ToolProcess.Find("codex.cmd") : null);

    public static Task<QuotaProvider> ReadAsync(string path, CancellationToken ct) => ReadAsync(StartInfo(path), Timeout, ct);

    internal static ProcessStartInfo StartInfo(string path)
    {
        // npm installs a .cmd shim on Windows; execute it explicitly without shelling Unix paths.
        var shim = OperatingSystem.IsWindows() && path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(shim ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : path);
        if (shim)
        {
            start.Environment["PHONEBOX_CODEX_CLI"] = path;
            start.Arguments = "/d /v:off /s /c \"\"%PHONEBOX_CODEX_CLI%\" app-server\"";
        }
        else start.ArgumentList.Add("app-server");
        // Stdio is the default transport, including CLI versions before --stdio was added.
        return start;
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
                clientInfo = new { name = "phonebox", title = "PhoneBox", version = AppPaths.Version },
                capabilities = new { experimentalApi = true }
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
            // App-server can stay alive after stdin closes; reap it on success and every failure.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await deadline.CancelAsync();
            try { await error; } catch (OperationCanceledException) { }
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
                    var responseId = Prop(root, "id");
                    if (responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var number) || number != id) continue;
                    if (Prop(root, "error").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                        throw new IOException("Codex app-server rejected " + (id == 1 ? "initialize" : "account/rateLimits/read"));
                    var result = Prop(root, "result");
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
        var plan = Text(rate, "planType");
        return new QuotaProvider("codex", "Codex", string.IsNullOrEmpty(plan) ? "Codex" : char.ToUpperInvariant(plan[0]) + plan[1..],
            limits, extra, now, null, Source: "codex-app-server");
    }

    private static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) ? value : default;
    private static string? Text(JsonElement e, string name) => Prop(e, name) is var value && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? Number(JsonElement e, string name) => Prop(e, name) is var value && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
}
