using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The Codex reading from a session log of the test's own, never the real ~/.codex.</summary>
public sealed class CodexQuotaTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("phonebox-codex-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private QuotaMonitor Agent() => new(NullLogger<QuotaMonitor>.Instance) { CodexSessionsRoot = _root };

    private static string Reading(int percent) =>
        """{"timestamp":"2026-11-16T10:00:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":"""
        + percent + ""","window_minutes":300,"resets_at":4102444800},"secondary":{"used_percent":3,"window_minutes":10080,"resets_at":4102444800}}}}""";

    private string Log(params string[] lines)
    {
        var dir = Path.Combine(_root, "2026", "11", "16");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "rollout-2026-11-16T10-00-00-test.jsonl");
        File.WriteAllText(path, string.Join("\n", lines));
        return path;
    }

    private static QuotaProvider Codex(QuotaMonitor agent) => Assert.Single(agent.Snapshot(), p => p.Id == "codex");

    [Fact]
    public void ACompleteLastReadingIsUsed()
    {
        Log("{\"type\":\"session_meta\"}", Reading(12) + "\n");
        var agent = Agent();
        agent.CodexTick();
        Assert.Null(Codex(agent).Error);
        Assert.Equal(12, Codex(agent).Limits[0].Percent);
    }

    [Fact]
    public void ALastLineCutOffMidWriteFallsBackToTheReadingBeforeIt()
    {
        // Codex was stopped, or PhoneBox read the file, while the newest reading was half written
        var torn = Reading(55);
        Log("{\"type\":\"session_meta\"}", Reading(12), torn[..(torn.Length / 2 + 40)]);
        var agent = Agent();
        agent.CodexTick();
        Assert.Null(Codex(agent).Error);
        Assert.Equal(12, Codex(agent).Limits[0].Percent);
    }

    [Fact]
    public async Task LiveQuotaOverridesLocalQuotaAndRetainsSessionDetails()
    {
        Log(Reading(12), """{"type":"turn_context","payload":{"model":"test-model","effort":"medium"}}""");
        var agent = Agent();
        agent.CodexTick();
        var live = new QuotaProvider("codex", "Codex", "Plus", [new("5-hour", 82, null, 18000, "warning")], null, 1000, null, Source: "codex-app-server");
        await agent.RefreshCodexOnceAsync(() => "fake", (_, _) => Task.FromResult(live), default);
        var result = agent.CodexSnapshot(1001)!;
        Assert.Equal(82, result.Limits[0].Percent);
        Assert.Equal("test-model", result.Codex!.Model);
        var external = live with { Source = "codexbar", Limits = [new("5-hour", 90, null, 18000, "warning")] };
        Assert.Equal(90, agent.CodexSnapshot(1001, external)!.Limits[0].Percent);
        Assert.Equal("test-model", agent.CodexSnapshot(1001, external)!.Codex!.Model);
        Assert.Equal(82, agent.CodexSnapshot(1001, external with { Error = "failed" })!.Limits[0].Percent);
        Assert.Equal(82, agent.CodexSnapshot(1001, external with { UpdatedAt = -600000 })!.Limits[0].Percent);
        await agent.RefreshCodexOnceAsync(() => "fake", (_, _) => throw new IOException("offline"), default);
        Assert.Equal(12, agent.CodexSnapshot(1002)!.Limits[0].Percent);
        Assert.Contains("local session snapshot", agent.CodexSnapshot(1002)!.Error);
        Assert.Equal("test-model", agent.CodexSnapshot(1002)!.Codex!.Model);
        Assert.Null(agent.CodexSnapshot(1002, external)!.Error);
        await agent.RefreshCodexOnceAsync(() => null, (_, _) => throw new Exception("must not run"), default);
        Assert.Equal(12, agent.CodexSnapshot(1003)!.Limits[0].Percent);
        Assert.Contains("not installed", agent.CodexSnapshot(1003)!.Error);
    }

    [Fact]
    public void ALockedLogKeepsTheLastReading()
    {
        if (!OperatingSystem.IsWindows()) return; // FileShare.None only locks other readers on Windows
        var path = Log("{\"type\":\"session_meta\"}", Reading(12) + "\n");
        var agent = Agent();
        agent.CodexTick();
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            agent.CodexTick();
        Assert.Equal(12, Codex(agent).Limits[0].Percent);
    }
}
