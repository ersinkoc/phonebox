using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class CodexLiveQuotaTests
{
    private const string Rate = """{"primary":{"usedPercent":82,"windowDurationMins":300,"resetsAt":4102444800},"secondary":{"usedPercent":7,"windowDurationMins":10080,"resetsAt":4102444900},"planType":"plus","credits":{"hasCredits":true,"balance":"12.5"}}""";
    private static QuotaProvider Parse(string json, long now = 1000)
    {
        using var doc = JsonDocument.Parse(json);
        return CodexQuota.Parse(doc.RootElement, now);
    }

    [Theory]
    [InlineData("rateLimits")]
    [InlineData("rateLimitsByLimitId")]
    public void NormalizesBothResponseShapes(string shape)
    {
        var json = shape == "rateLimits" ? "{\"rateLimits\":" + Rate + "}"
            : "{\"rateLimitsByLimitId\":{\"codex\":" + Rate + "},\"rateLimits\":{}}";
        var quota = Parse(json);
        Assert.Equal("codex-app-server", quota.Source);
        Assert.Equal("Plus", quota.Plan);
        Assert.Equal(1000, quota.UpdatedAt);
        Assert.Equal("Credits: 12.5", quota.Extra);
        Assert.Collection(quota.Limits, primary =>
        {
            Assert.Equal("5-hour", primary.Label);
            Assert.Equal(82, primary.Percent);
            Assert.Equal("warning", primary.Severity);
            Assert.Equal(4102444800000, primary.ResetsAt);
            Assert.Equal(18000, primary.WindowSec);
        }, secondary => Assert.Equal("Weekly", secondary.Label));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"rateLimits\":{\"primary\":{}}}")]
    [InlineData("{\"rateLimitsByLimitId\":{\"other\":{\"primary\":{\"usedPercent\":20}}}}")]
    public void MissingMeasurementsAreUnavailable(string json) => Assert.Throws<JsonException>(() => Parse(json));

    [Fact]
    public void BackendBlockedStatusIsPreservedEvenBelowOneHundredPercent()
    {
        var result = Parse("{\"ordinaryUsageAllowed\":false,\"rateLimits\":" + Rate + "}");
        Assert.StartsWith("Limit reached", result.Extra);
        Assert.Equal("critical", Parse("{\"rateLimits\":{" + "\"primary\":{\"usedPercent\":101}}}").Limits[0].Severity);
    }

    [Fact]
    public async Task PassingResetTimeDoesNotClearBackendBlockedStatus()
    {
        var agent = new QuotaMonitor(NullLogger<QuotaMonitor>.Instance);
        var live = Parse("""{"ordinaryUsageAllowed":false,"rateLimits":{"primary":{"usedPercent":3,"resetsAt":1}}}""",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await agent.RefreshCodexOnceAsync(() => "fake", (_, _) => Task.FromResult(live), default);
        Assert.Equal("Limit reached", Assert.Single(agent.Snapshot()).Extra);
    }

    [Fact]
    public void WindowsNpmEntryPointUsesNodeArgumentListWithoutShell()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("pb codex & shim ").FullName;
        try
        {
            var script = Path.Combine(root, "node_modules", "@openai", "codex", "bin", "codex.js");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.WriteAllText(script, "// fake entry point; not executed");
            var node = Path.Combine(root, "node.exe");
            File.WriteAllText(node, "fake node; not executed");
            var start = CodexQuota.StartInfo(Path.Combine(root, "codex.cmd"));
            Assert.Equal(node, start.FileName);
            Assert.Equal(new[] { script, "app-server" }, start.ArgumentList);
            Assert.Empty(start.Arguments);
            File.Delete(script);
            Assert.Throws<IOException>(() => CodexQuota.StartInfo(Path.Combine(root, "codex.cmd")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UncheckingCodexStopsLiveQueriesAndClearsLiveCache()
    {
        var root = Directory.CreateTempSubdirectory("pb-codex-disabled-").FullName;
        try
        {
            var path = Path.Combine(root, "config.json");
            File.WriteAllText(path, """{"codexBar":{"providers":["codex"]}}""");
            var store = new ConfigStore(path);
            var agent = new QuotaMonitor(store, NullLogger<QuotaMonitor>.Instance);
            await agent.RefreshCodexOnceAsync(() => "fake", (_, _) => Task.FromResult(Parse("{\"rateLimits\":" + Rate + "}")), default);
            Assert.NotNull(agent.CodexSnapshot(1000));
            store.Get().CodexBar.Providers = [];
            await agent.RefreshCodexOnceAsync(() => throw new Exception("must not discover CLI"),
                (_, _) => throw new Exception("must not query CLI"), default);
            Assert.Null(agent.CodexSnapshot(1000));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LiveReadingWorksWithoutSessionsAndExpires()
    {
        var agent = new QuotaMonitor(NullLogger<QuotaMonitor>.Instance);
        await agent.RefreshCodexOnceAsync(() => "fake", (_, _) => Task.FromResult(Parse("{\"rateLimits\":" + Rate + "}")), default);
        Assert.Equal(82, agent.CodexSnapshot(1000)!.Limits[0].Percent);
        Assert.Equal("codex-app-server", agent.CodexSnapshot(121000)!.Source);
        Assert.Contains("stale", agent.CodexSnapshot(121001)!.Error);
    }

    [Fact]
    public async Task FailureDoesNotShowPreviousLiveReadingAndNextPollRecovers()
    {
        var agent = new QuotaMonitor(NullLogger<QuotaMonitor>.Instance);
        Task<QuotaProvider> Read(string _, CancellationToken ct) => Task.FromResult(Parse("{\"rateLimits\":" + Rate + "}"));
        await agent.RefreshCodexOnceAsync(() => "fake", Read, default);
        await agent.RefreshCodexOnceAsync(() => "fake", (_, _) => throw new IOException("signed out"), default);
        Assert.Empty(agent.CodexSnapshot(1001)!.Limits);
        Assert.NotNull(agent.CodexSnapshot(1001)!.Error);
        await agent.RefreshCodexOnceAsync(() => "fake", Read, default);
        Assert.Null(agent.CodexSnapshot(1002)!.Error);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.RefreshCodexOnceAsync(() => "fake",
            (_, ct) => Task.FromCanceled<QuotaProvider>(ct), cancelled.Token));
    }

    internal static ProcessStartInfo Shell(string windows, string unix)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe") : "/bin/sh");
        foreach (var arg in OperatingSystem.IsWindows() ? new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", windows } : new[] { "-c", unix }) start.ArgumentList.Add(arg);
        return start;
    }

    [Fact]
    public async Task ProtocolWaitsForInitializeAndIgnoresNotificationsAndUnrelatedResponses()
    {
        var transcript = Path.Combine(Path.GetTempPath(), "pb-codex-rpc-" + Guid.NewGuid());
        var response = "{\"id\":2,\"result\":{\"rateLimits\":" + Rate + "}}";
        try
        {
            var start = Shell(
                $"$a=[Console]::ReadLine(); [IO.File]::WriteAllText('{transcript}', $a + \"`n\"); [Console]::WriteLine('{{\"id\":1,\"result\":{{}}}}'); $b=[Console]::ReadLine(); $c=[Console]::ReadLine(); [IO.File]::AppendAllText('{transcript}', $b + \"`n\" + $c); [Console]::WriteLine('{{\"method\":\"notification\"}}'); [Console]::WriteLine('{{\"id\":99,\"result\":{{}}}}'); [Console]::WriteLine('{response}'); Start-Sleep 60",
                $"read -r a; printf '%s\\n' \"$a\" > '{transcript}'; printf '%s\\n' '{{\"id\":1,\"result\":{{}}}}'; read -r b; read -r c; printf '%s\\n' \"$b\" \"$c\" >> '{transcript}'; printf '%s\\n' 'diagnostic' '{{\"method\":\"notification\"}}' '{{\"id\":99,\"result\":{{}}}}' '{response}'; exec sleep 60");
            var result = await CodexQuota.ReadAsync(start, TimeSpan.FromSeconds(10), default);
            Assert.Equal(82, result.Limits[0].Percent);
            var requests = File.ReadAllLines(transcript).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.Equal(3, requests.Length);
                Assert.Equal("initialize", requests[0].RootElement.GetProperty("method").GetString());
                Assert.Equal("initialized", requests[1].RootElement.GetProperty("method").GetString());
                Assert.Equal("account/rateLimits/read", requests[2].RootElement.GetProperty("method").GetString());
                Assert.True(requests[2].RootElement.GetProperty("params").GetProperty("excludeResetCreditDetails").GetBoolean());
                Assert.False(requests[2].RootElement.GetProperty("params").GetProperty("supportsLunaReserve").GetBoolean());
            }
            finally { foreach (var request in requests) request.Dispose(); }
        }
        finally { File.Delete(transcript); }
    }

    [Theory]
    [InlineData("{\"id\":1,\"error\":{\"message\":\"unsupported\"}}")]
    [InlineData("{\"id\":1,\"result\":null}")]
    [InlineData("")]
    public async Task RejectedHandshakeAndPrematureExitFail(string response)
    {
        var start = Shell($"[Console]::ReadLine() | Out-Null; [Console]::WriteLine('{response}')",
            $"read -r a; printf '%s\\n' '{response}'");
        await Assert.ThrowsAsync<IOException>(() => CodexQuota.ReadAsync(start, TimeSpan.FromSeconds(10), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndCancellationReapProcess(bool cancel)
    {
        var marker = Path.Combine(Path.GetTempPath(), "pb-codex-pid-" + Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        var start = Shell($"[IO.File]::WriteAllText('{marker}', [string]$PID); Start-Sleep 60",
            $"echo $$ > '{marker}'; exec sleep 60");
        int pid = 0;
        try
        {
            var pending = CodexQuota.ReadAsync(start, TimeSpan.FromSeconds(5), cancellation.Token);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (pid == 0)
            {
                if (File.Exists(marker))
                    try { int.TryParse((await File.ReadAllTextAsync(marker, wait.Token)).Trim(), out pid); }
                    catch (IOException) { }
                if (pid == 0) await Task.Delay(20, wait.Token);
            }
            if (cancel) cancellation.Cancel();
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            else await Assert.ThrowsAsync<TimeoutException>(() => pending);
            Assert.False(IsAlive(pid));
        }
        finally
        {
            if (pid > 0 && IsAlive(pid))
                try { using var child = Process.GetProcessById(pid); child.Kill(entireProcessTree: true); } catch (ArgumentException) { }
            File.Delete(marker);
        }
    }

    private static bool IsAlive(int pid)
    {
        try { using var child = Process.GetProcessById(pid); return !child.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
