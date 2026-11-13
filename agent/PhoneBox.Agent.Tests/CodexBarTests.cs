using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class CodexBarTests
{
    [Fact]
    public void ParsesPrimarySecondaryAndSupplementalWindowsWithoutPlanMetadata()
    {
        var p = Assert.Single(CodexBar.Parse("""
            [{"provider":"claude","usage":{"primary":{"usedPercent":0,"windowMinutes":300},
            "secondary":{"usedPercent":80,"windowMinutes":10080},
            "extraRateWindows":[{"title":"Model pool","window":{"usedPercent":25}}]}}]
            """));
        Assert.Equal(3, p.Limits.Count);
        Assert.Equal(0, p.Limits[0].Percent);
        Assert.Equal("Weekly", p.Limits[1].Label);
        Assert.Equal("Model pool", p.Limits[2].Label);
        Assert.Null(p.Error);
    }

    [Fact]
    public void SyntheticAndMalformedWindowsAreSkippedWithoutDroppingLaterProviders()
    {
        var p = CodexBar.Parse("""
            [{"provider":"claude","usage":{"primary":{"usedPercent":0,"isSyntheticPlaceholder":true},"secondary":{"usedPercent":"unknown"}}},
            {"provider":"antigravity","usage":{"secondary":{"usedPercent":0}}}]
            """);
        Assert.Empty(p[0].Limits);
        Assert.NotNull(p[0].Error);
        Assert.Equal("Secondary limit", Assert.Single(p[1].Limits).Label);
    }

    [Fact]
    public void UsesLocalFallbackOnProviderErrorAndKeepsItFresh()
    {
        var external = Assert.Single(CodexBar.Parse("""[{"provider":"codex","error":{"message":"Signed out"}}]"""));
        var first = new QuotaProvider("codex", "Codex", "Plan", [new("Weekly", 10, null, null, "normal")], null, 1, null);
        var next = first with { Limits = [new("Weekly", 40, null, null, "normal")], UpdatedAt = 2 };
        Assert.Equal(10, CodexBar.Select(first, external, 2)!.Limits[0].Percent);
        Assert.Equal(40, CodexBar.Select(next, external, 3)!.Limits[0].Percent);
        Assert.Equal("Signed out", CodexBar.Select(null, external, 3)!.Error);
        var healthy = next with { Source = "codexbar", UpdatedAt = 3 };
        Assert.Equal("codexbar", CodexBar.Select(first, healthy, 4)!.Source);
        Assert.Equal("local", CodexBar.Select(first, healthy, 600004)!.Source);
    }

    [Fact]
    public void InvalidRootIsAnError() => Assert.Throws<System.Text.Json.JsonException>(() => CodexBar.Parse("{}"));

    [Fact]
    public void AFailedProviderDoesNotDiscardOtherProvidersFromANonzeroExit()
    {
        // CodexBar 0.84.0 merges provider outputs but returns failure if any provider fails.
        var providers = CodexBar.Parse("""
            [{"provider":"claude","error":{"message":"Not signed in"}},
             {"provider":"codex","usage":{"primary":{"usedPercent":32,"windowMinutes":300}}}]
            """, exitCode: 1);
        Assert.Equal(2, providers.Count);
        Assert.Equal("Not signed in", providers[0].Error);
        Assert.Null(providers[1].Error);
        Assert.Equal(32, Assert.Single(providers[1].Limits).Percent);
    }

    [Fact]
    public void FatalCliErrorsRemainFailures() => Assert.Throws<IOException>(() => CodexBar.Parse(
        """[{"provider":"cli","error":{"message":"Invalid arguments"}}]""", exitCode: 1));

    [Fact]
    public void TertiaryWindowsAreRetainedAlongsidePrimaryAndSecondary()
    {
        var provider = Assert.Single(CodexBar.Parse("""
            [{"provider":"claude","rateWindowLabels":{"tertiary":"Opus"},"usage":{
            "primary":{"usedPercent":10},"secondary":{"usedPercent":20},"tertiary":{"usedPercent":30}}}]
            """));
        Assert.Equal(3, provider.Limits.Count);
        Assert.Equal("Opus", provider.Limits[2].Label);
        Assert.Equal(30, provider.Limits[2].Percent);
    }

    /// <summary>
    /// Verifies that documented CLI pace metadata, reset descriptions, and expanded provider names are parsed.
    /// </summary>
    [Fact]
    public void ParsesResetDescriptionPaceAndAdditionalProviders()
    {
        var providers = CodexBar.Parse("""
            [{"provider":"cursor","pace":{"primary":{"stage":"ahead","deltaPercent":12.5,"expectedUsedPercent":30,"willLastToReset":true}},
              "usage":{"loginMethod":"Pro","primary":{
                "usedPercent":42.5,
                "windowMinutes":10080,
                "resetDescription":"Resets Sunday 12:00"
              },
              "extraRateWindows":[{"title":"Fuel pack: 750/1000","window":{"usedPercent":25,"resetDescription":"   "}}]}},
            {"provider":"alibabatokenplan","usage":{"primary":{"usedPercent":10}}},
            {"provider":"alibaba","usage":{"primary":{"usedPercent":20}}},
            {"provider":"custom_ai","displayName":"Ignored Name","usage":{"primary":{"usedPercent":75}}}]
            """);
        Assert.Equal(4, providers.Count);

        var cursor = providers[0];
        Assert.Equal("cursor", cursor.Id);
        Assert.Equal("Cursor", cursor.Name);
        Assert.Equal("Pro", cursor.Plan);
        Assert.Equal(2, cursor.Limits.Count);
        var limit = cursor.Limits[0];
        Assert.Equal("Weekly", limit.Label);
        Assert.Equal("Resets Sunday 12:00", limit.ResetDescription);
        Assert.Equal(12.5, limit.DeltaPercent);
        Assert.Equal(30, limit.ExpectedPercent);
        Assert.True(limit.PaceOnTrack);

        var fuelPack = cursor.Limits[1];
        Assert.Equal("Fuel pack: 750/1000", fuelPack.Label);
        Assert.Null(fuelPack.ResetDescription);
        Assert.Null(fuelPack.DeltaPercent);
        Assert.Null(fuelPack.PaceOnTrack);

        Assert.Equal("Alibaba Token Plan", providers[1].Name);
        Assert.Equal("Alibaba Coding Plan", providers[2].Name);
        Assert.Equal("Custom_ai", providers[3].Name);
    }

    /// <summary>
    /// Verifies that quota-above fires for cached limits carrying an error while ignoring omitted providers.
    /// </summary>
    [Fact]
    public void QuotaAboveRuleKeepsFiringForCachedLimitsWithErrorAndSkipsOmittedProviders()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-codexbar-rule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {"rules":[
                  {"id":"q-claude","name":"Claude High","when":"quota-above","match":"claude","value":80,"then":"push","key":"q_claude","pushValue":"hot","whileActive":true},
                  {"id":"q-cursor","name":"Cursor High","when":"quota-above","match":"cursor","value":80,"then":"push","key":"q_cursor","pushValue":"hot","whileActive":true}
                ]}
                """);
            var cfg = new ConfigStore(cfgPath);
            var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(dir, "data.json"));
            var quotas = new QuotaMonitor(NullLogger<QuotaMonitor>.Instance);
            quotas.SetStateForTest(
                claude: new QuotaProvider("claude", "Claude", "Max",
                    [new QuotaLimit("5-hour session", 92, null, 18000, "warning")],
                    null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "Could not fetch quota (HTTP 500)"),
                external: [new QuotaProvider("cursor", "Cursor", "Pro", [], null,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "CodexBar omitted this provider", Source: "codexbar")]);

            var engine = new RuleEngine(cfg, push,
                new SessionMonitor(NullLogger<SessionMonitor>.Instance),
                new MediaMonitor(NullLogger<MediaMonitor>.Instance),
                new SystemMonitor(NullLogger<SystemMonitor>.Instance),
                new PrivacyMonitor(NullLogger<PrivacyMonitor>.Instance),
                new CalendarMonitor(cfg, NullLogger<CalendarMonitor>.Instance),
                new ObsMonitor(cfg, NullLogger<ObsMonitor>.Instance),
                quotas,
                new GitHubMonitor(NullLogger<GitHubMonitor>.Instance),
                new BatteryMonitor(NullLogger<BatteryMonitor>.Instance),
                new PingMonitor(),
                new AudioControl(),
                NullLogger<RuleEngine>.Instance);

            engine.Evaluate();

            Assert.Contains("q-claude", engine.Active());
            Assert.DoesNotContain("q-cursor", engine.Active());
            Assert.True(push.Snapshot().ContainsKey("q_claude"));
            Assert.False(push.Snapshot().ContainsKey("q_cursor"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
