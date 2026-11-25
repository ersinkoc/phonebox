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
    public void PlanNamesReplaceUnderscoresWithSpaces()
    {
        var result = Parse("""{"rateLimits":{"planType":"self_serve_plus","primary":{"usedPercent":10}}}""");
        Assert.Equal("Self serve plus", result.Plan);
    }

    [Fact]
    public void WindowsDiscoveryIgnoresPosixShimAndPrefersExeOverCmd()
    {
        var dir1 = Directory.CreateTempSubdirectory("pb-codex-find-1-").FullName;
        var dir2 = Directory.CreateTempSubdirectory("pb-codex-find-2-").FullName;
        try
        {
            // Bare POSIX `codex` shim next to `codex.cmd`: must pick `codex.cmd`, never bare `codex`.
            File.WriteAllText(Path.Combine(dir1, "codex"), "#!/bin/sh\n");
            var cmd = Path.Combine(dir1, "codex.cmd");
            File.WriteAllText(cmd, "@echo off\r\n");
            var pathEnv = dir1 + Path.PathSeparator + dir2;
            Assert.Equal(cmd, CodexQuota.Find(pathEnv, isWindows: true));

            // When a native `codex.exe` exists later on PATH, prefer `.exe` over `.cmd`.
            var exe = Path.Combine(dir2, "codex.exe");
            File.WriteAllText(exe, "fake exe");
            Assert.Equal(exe, CodexQuota.Find(pathEnv, isWindows: true));

            // With only bare POSIX `codex` on Windows, return null.
            File.Delete(cmd);
            File.Delete(exe);
            Assert.Null(CodexQuota.Find(pathEnv, isWindows: true));
        }
        finally
        {
            Directory.Delete(dir1, recursive: true);
            Directory.Delete(dir2, recursive: true);
        }
    }

    [Fact]
    public void WindowsNpmEntryPointPrefersNativeVendorBinaryThenFallsBackToNode()
    {
        var root = Directory.CreateTempSubdirectory("pb codex & shim ").FullName;
        try
        {
            var script = Path.Combine(root, "node_modules", "@openai", "codex", "bin", "codex.js");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.WriteAllText(script, "// fake entry point; not executed");
            var node = Path.Combine(root, "node.exe");
            File.WriteAllText(node, "fake node; not executed");
            var cmd = Path.Combine(root, "codex.cmd");

            // Fallback to Node + codex.js when native vendor binary is absent
            var fallback = CodexQuota.StartInfo(cmd, isWindows: true, System.Runtime.InteropServices.Architecture.X64);
            Assert.Equal(node, fallback.FileName);
            Assert.Equal(new[] { script, "app-server" }, fallback.ArgumentList);
            Assert.Empty(fallback.Arguments);

            // Prefer bundled native codex.exe when present (no Node needed)
            var native = Path.Combine(root, "node_modules", "@openai", "codex", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(native)!);
            File.WriteAllText(native, "fake native binary");
            var direct = CodexQuota.StartInfo(cmd, isWindows: true, System.Runtime.InteropServices.Architecture.X64);
            Assert.Equal(native, direct.FileName);
            Assert.Equal(new[] { "app-server" }, direct.ArgumentList);

            // Missing both native binary and script (e.g. pnpm shim) throws IOException cleanly
            File.Delete(native);
            File.Delete(script);
            Assert.Throws<IOException>(() => CodexQuota.StartInfo(cmd, isWindows: true, System.Runtime.InteropServices.Architecture.X64));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void StoreCliFallbackRunsOnlyWhenWindowsPathHasNoCli()
    {
        var root = Directory.CreateTempSubdirectory("pb-codex-store-").FullName;
        try
        {
            Assert.Equal("store-codex", CodexQuota.Find(root, true, () => "store-codex"));
            var cli = Path.Combine(root, "codex.exe");
            File.WriteAllText(cli, "");
            Assert.Equal(cli, CodexQuota.Find(root, true, () => throw new InvalidOperationException()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LivePollingIsOptInWorksWithoutNativeWidgetsAndYieldsToFreshCodexBar()
    {
        var root = Directory.CreateTempSubdirectory("pb-codex-gating-").FullName;
        try
        {
            var path = Path.Combine(root, "config.json");
            // Default config: codexLiveQuota is false -> must not poll and must not surface an error
            File.WriteAllText(path, """{"codexBar":{"providers":["codex"]},"pages":[{"id":"p1","enabled":true,"widgets":[{"type":"quotas"}]}]}""");
            var store = new ConfigStore(path);
            var agent = new QuotaMonitor(store, NullLogger<QuotaMonitor>.Instance);
            Assert.False(store.Get().CodexBar.CodexLiveQuota);
            Assert.Equal(QuotaMonitor.CodexLiveOutcome.Skipped,
                await agent.RefreshCodexOnceAsync(() => throw new Exception("must not discover CLI"),
                    (_, _) => throw new Exception("must not query CLI"), 1000, default));
            Assert.Null(agent.CodexSnapshot(1000));

            // Opted in with only a custom API consumer -> polls and succeeds
            store.Get().CodexBar.CodexLiveQuota = true;
            store.Get().Pages[0].Widgets![0].Type = "html";
            Assert.Equal(QuotaMonitor.CodexLiveOutcome.Ok,
                await agent.RefreshCodexOnceAsync(() => "fake", (_, _) => Task.FromResult(Parse("{\"rateLimits\":" + Rate + "}")), 1000, default));
            Assert.NotNull(agent.CodexSnapshot(1000));

            // Fresh CodexBar data wins -> skips live CLI poll
            var freshExternal = new QuotaProvider("codex", "Codex", "Plus",
                [new QuotaLimit("5-hour", 20, null, 18000, "normal")], null, 1000, null, Source: "codexbar");
            agent.SetStateForTest(external: [freshExternal]);
            Assert.Equal(QuotaMonitor.CodexLiveOutcome.Skipped,
                await agent.RefreshCodexOnceAsync(() => throw new Exception("must skip while CodexBar is fresh"),
                    (_, _) => throw new Exception("must skip while CodexBar is fresh"), 1000, default));

            // Unchecking Codex stops polling and clears live cache
            agent.SetStateForTest(external: []);
            store.Get().CodexBar.Providers = [];
            Assert.Equal(QuotaMonitor.CodexLiveOutcome.Skipped,
                await agent.RefreshCodexOnceAsync(() => throw new Exception("must not discover CLI"),
                    (_, _) => throw new Exception("must not query CLI"), 1000, default));
            Assert.Null(agent.CodexSnapshot(1000));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FailureBackoffGrowsExponentiallyUpToThirtyMinutes()
    {
        Assert.Equal(120_000, QuotaMonitor.CodexLiveBackoffMs(1));
        Assert.Equal(240_000, QuotaMonitor.CodexLiveBackoffMs(2));
        Assert.Equal(480_000, QuotaMonitor.CodexLiveBackoffMs(3));
        Assert.Equal(960_000, QuotaMonitor.CodexLiveBackoffMs(4));
        Assert.Equal(1_800_000, QuotaMonitor.CodexLiveBackoffMs(5));
        Assert.Equal(1_800_000, QuotaMonitor.CodexLiveBackoffMs(10));
    }

    [Fact]
    public async Task LiveReadingWorksWithoutSessionsAndExpires()
    {
        var agent = new QuotaMonitor(NullLogger<QuotaMonitor>.Instance);
        await agent.RefreshCodexOnceAsync(() => "fake", (_, _) => Task.FromResult(Parse("{\"rateLimits\":" + Rate + "}")), default);
        Assert.Equal(82, agent.CodexSnapshot(1000)!.Limits[0].Percent);
        Assert.Equal("codex-app-server", agent.CodexSnapshot(361000)!.Source);
        Assert.Contains("stale", agent.CodexSnapshot(361001)!.Error);
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
                $"$a=[Console]::ReadLine(); [IO.File]::WriteAllText('{transcript}', $a + \"`n\"); [Console]::WriteLine('{{\"id\":1,\"method\":\"server/ping\"}}'); [Console]::WriteLine('{{\"id\":1,\"result\":{{}}}}'); $b=[Console]::ReadLine(); $c=[Console]::ReadLine(); [IO.File]::AppendAllText('{transcript}', $b + \"`n\" + $c); [Console]::WriteLine('{{\"method\":\"notification\"}}'); [Console]::WriteLine('{{\"id\":2,\"method\":\"server/request\"}}'); [Console]::WriteLine('{{\"id\":99,\"result\":{{}}}}'); [Console]::WriteLine('{response}'); Start-Sleep 60",
                $"read -r a; printf '%s\\n' \"$a\" > '{transcript}'; printf '%s\\n' '{{\"id\":1,\"method\":\"server/ping\"}}' '{{\"id\":1,\"result\":{{}}}}'; read -r b; read -r c; printf '%s\\n' \"$b\" \"$c\" >> '{transcript}'; printf '%s\\n' 'diagnostic' '{{\"method\":\"notification\"}}' '{{\"id\":2,\"method\":\"server/request\"}}' '{{\"id\":99,\"result\":{{}}}}' '{response}'; exec sleep 60");
            var result = await CodexQuota.ReadAsync(start, TimeSpan.FromSeconds(10), default);
            Assert.Equal(82, result.Limits[0].Percent);
            var requests = File.ReadAllLines(transcript).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.Equal(3, requests.Length);
                Assert.Equal("initialize", requests[0].RootElement.GetProperty("method").GetString());
                Assert.False(requests[0].RootElement.GetProperty("params").TryGetProperty("capabilities", out _));
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
            var pending = CodexQuota.ReadAsync(start, TimeSpan.FromSeconds(cancel ? 30 : 8), cancellation.Token);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
