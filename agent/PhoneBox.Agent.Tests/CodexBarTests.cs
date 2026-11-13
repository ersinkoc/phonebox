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

    /// <summary>
    /// Verifies that cost overview slices 90-day daily records to the stated window, avoids cross-period totals fallback,
    /// narrows mixed windows, and keeps currencies segregated.
    /// </summary>
    [Fact]
    public void ParsesCostOverviewWithConsistentWindowAndCurrency()
    {
        var sliced90dAndEur = CodexBar.ParseCost("""
            [
              {
                "provider": "claude",
                "historyDays": 90,
                "currencyCode": "EUR",
                "updatedAt": "2026-04-20T12:00:00Z",
                "totals": { "totalCost": 135.0, "totalTokens": 5900000 },
                "daily": [
                  {
                    "date": "2026-02-01",
                    "totalCost": 100.0,
                    "totalTokens": 5000000,
                    "modelBreakdowns": [
                      { "modelName": "claude-old", "cost": 100.0, "totalTokens": 5000000 }
                    ]
                  },
                  {
                    "date": "2026-04-18",
                    "totalCost": 20.0,
                    "totalTokens": 600000,
                    "modelBreakdowns": [
                      { "modelName": "claude-sonnet-4-5", "cost": 15.0, "totalTokens": 500000 },
                      { "modelName": "claude-opus-4-6", "cost": 5.0, "totalTokens": 100000 }
                    ]
                  },
                  {
                    "date": "2026-04-19",
                    "totalCost": 15.0,
                    "totalTokens": 300000,
                    "modelBreakdowns": [
                      { "modelName": "claude-sonnet-4-5", "cost": 15.0, "totalTokens": 300000 }
                    ]
                  }
                ]
              },
              {
                "provider": "codex",
                "historyDays": 90,
                "totals": { "totalCost": 50.0, "totalTokens": 500000 }
              },
              {
                "provider": "alibabatokenplan",
                "historyDays": 30,
                "currencyCode": "eur",
                "last30DaysCostUSD": 10.0,
                "last30DaysTokens": 200000
              },
              {
                "provider": "cursor",
                "historyDays": 30,
                "currencyCode": "USD",
                "last30DaysCostUSD": 99.0,
                "last30DaysTokens": 400000,
                "updatedAt": "2026-04-20T12:00:00Z",
                "daily": [
                  {
                    "date": "2026-04-19",
                    "totalCost": 99.0,
                    "totalTokens": 400000,
                    "modelBreakdowns": [
                      { "modelName": "gpt-5", "cost": 99.0, "totalTokens": 400000 }
                    ]
                  }
                ]
              }
            ]
            """, referenceDate: new DateOnly(2026, 4, 20));

        Assert.NotNull(sliced90dAndEur);
        Assert.Equal(30, sliced90dAndEur.WindowDays);
        Assert.Equal("EUR", sliced90dAndEur.CurrencyCode);
        Assert.Equal(3, sliced90dAndEur.ProviderCount);
        Assert.DoesNotContain(sliced90dAndEur.Subscriptions, s => s.Id == "codex");
        Assert.Equal(45.0, sliced90dAndEur.TotalCost, 3);
        Assert.Equal(1_100_000, sliced90dAndEur.TotalTokens);
        Assert.Equal("Alibaba Token Plan", sliced90dAndEur.Subscriptions.First(s => s.Id == "alibabatokenplan").Name);
        Assert.DoesNotContain(sliced90dAndEur.TopModels, m => m.ModelName == "claude-old");
        Assert.DoesNotContain(sliced90dAndEur.TopModels, m => m.ModelName == "gpt-5");
        Assert.Equal(2, sliced90dAndEur.TopModels.Count);
        Assert.Equal("claude-sonnet-4-5", sliced90dAndEur.TopModels[0].ModelName);
        Assert.Equal(30.0, sliced90dAndEur.TopModels[0].Cost, 3);
        Assert.Equal("EUR", sliced90dAndEur.TopModels[0].CurrencyCode);

        var sevenDay = CodexBar.ParseCost("""
            [
              {
                "provider": "codex",
                "historyDays": 7,
                "last30DaysCostUSD": 12.5,
                "last30DaysTokens": 250000
              },
              {
                "provider": "claude",
                "historyDays": 30,
                "updatedAt": "2026-04-20T12:00:00Z",
                "last30DaysCostUSD": 80.0,
                "daily": [
                  { "date": "2026-04-01", "totalCost": 70.0, "totalTokens": 700000 },
                  { "date": "2026-04-18", "totalCost": 10.0, "totalTokens": 100000 }
                ]
              }
            ]
            """, referenceDate: new DateOnly(2026, 4, 20));

        Assert.NotNull(sevenDay);
        Assert.Equal(7, sevenDay.WindowDays);
        Assert.Equal("USD", sevenDay.CurrencyCode);
        Assert.Equal(22.5, sevenDay.TotalCost, 3);
        Assert.Equal(350_000, sevenDay.TotalTokens);
    }

    /// <summary>
    /// Verifies that cost polling is skipped unless an enabled page (or enabled Smart Stack child) sets showOverview.
    /// </summary>
    [Fact]
    public async Task OverviewSkipsPollingUnlessShowOverviewEnabledIncludingStackChildren()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-cost-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            var stampBump = 0;
            void WriteCfg(string json)
            {
                File.WriteAllText(cfgPath, json);
                File.SetLastWriteTimeUtc(cfgPath, DateTime.UtcNow.AddSeconds(++stampBump));
            }

            WriteCfg("""{"pages":[{"id":"ai","enabled":true,"widgets":[{"id":"q1","type":"codexbar","options":{}}]}]}""");
            var cfg = new ConfigStore(cfgPath);
            var monitor = new QuotaMonitor(cfg, NullLogger<QuotaMonitor>.Instance);
            var calls = 0;

            Task<QuotaOverview?> FakeRead(string path, CancellationToken ct)
            {
                calls++;
                return Task.FromResult<QuotaOverview?>(new QuotaOverview(100, 1.5, "USD", 30, 1, 1000,
                    [new QuotaSubscriptionCost("claude", "Claude", 100, 1.5)], []));
            }

            Assert.False(cfg.Get().ShowQuotaOverview());
            Assert.False(await monitor.RefreshOverviewOnceAsync(() => "codexbar", FakeRead, 1000, CancellationToken.None));
            Assert.Equal(0, calls);

            WriteCfg("""
                {"pages":[
                  {"id":"home","enabled":true,"widgets":[]},
                  {"id":"ai","enabled":false,"widgets":[{"id":"q1","type":"codexbar","options":{"showOverview":true}}]}
                ]}
                """);
            Assert.False(cfg.Get().ShowQuotaOverview());
            Assert.False(await monitor.RefreshOverviewOnceAsync(() => "codexbar", FakeRead, 1000, CancellationToken.None));
            Assert.Equal(0, calls);

            WriteCfg("""
                {"pages":[{"id":"ai","enabled":true,"widgets":[
                  {"id":"s1","type":"stack","options":{"widgets":[{"id":"c1","type":"codexbar","enabled":true,"options":{"showOverview":true}}]}}
                ]}]}
                """);
            Assert.True(cfg.Get().ShowQuotaOverview());
            Assert.True(await monitor.RefreshOverviewOnceAsync(() => "codexbar", FakeRead, 1000, CancellationToken.None));
            Assert.Equal(1, calls);
            Assert.NotNull(monitor.Overview(2000));

            WriteCfg("""{"pages":[{"id":"ai","enabled":true,"widgets":[{"id":"q1","type":"codexbar","options":{"showOverview":true}}]}]}""");
            Assert.True(cfg.Get().ShowQuotaOverview());
            Assert.True(await monitor.RefreshOverviewOnceAsync(() => "codexbar", FakeRead, 3000, CancellationToken.None));
            Assert.Equal(2, calls);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Verifies that a cached cost overview stays valid up to 1 hour on refresh errors and expires cleanly afterwards.
    /// </summary>
    [Fact]
    public async Task OverviewExpiresAfterOneHourWithoutSuccessfulRefresh()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-cost-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {"pages":[{"id":"ai","enabled":true,"widgets":[{"id":"q1","type":"codexbar","options":{"showOverview":true}}]}]}
                """);
            var cfg = new ConfigStore(cfgPath);
            var monitor = new QuotaMonitor(cfg, NullLogger<QuotaMonitor>.Instance);
            const long t0 = 1_000_000;

            Assert.True(await monitor.RefreshOverviewOnceAsync(
                () => "codexbar",
                (_, _) => Task.FromResult<QuotaOverview?>(new QuotaOverview(500, 5.0, "USD", 30, 1, t0,
                    [new QuotaSubscriptionCost("claude", "Claude", 500, 5.0)], [])),
                t0,
                CancellationToken.None));

            var at59Min = t0 + 59 * 60_000L;
            Assert.False(await monitor.RefreshOverviewOnceAsync(() => null, (_, _) => Task.FromResult<QuotaOverview?>(null), at59Min, CancellationToken.None));
            Assert.NotNull(monitor.Overview(at59Min));

            var at61Min = t0 + 61 * 60_000L;
            Assert.Null(monitor.Overview(at61Min));
            Assert.False(await monitor.RefreshOverviewOnceAsync(
                () => "codexbar",
                (_, _) => throw new IOException("timeout"),
                at61Min,
                CancellationToken.None));
            Assert.Null(monitor.Overview(t0));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
