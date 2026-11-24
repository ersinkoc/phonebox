using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

/// <summary>Config normalization: templates, the pre-0.6.2 page migration, grid limits, ids and what the phone sees.</summary>
public class ConfigTests
{
    [Fact]
    public void NullOrMissingSteamApiKeyKeepsStoredKeyButEmptyCanClearIt()
    {
        var proposed = JsonSerializer.Deserialize<AgentConfig>("{}", ConfigStore.ReadOpts)!;
        Assert.True(ConfigStore.PreserveSteamApiKey(proposed, "stored-secret"));
        Assert.Equal("stored-secret", proposed.Steam.ApiKey);

        proposed = JsonSerializer.Deserialize<AgentConfig>("{\"steam\":{\"apiKey\":null}}", ConfigStore.ReadOpts)!;
        Assert.True(ConfigStore.PreserveSteamApiKey(proposed, "stored-secret"));
        Assert.Equal("stored-secret", proposed.Steam.ApiKey);

        proposed.Steam.ApiKey = "";
        Assert.False(ConfigStore.PreserveSteamApiKey(proposed, "stored-secret"));
        Assert.Equal("", proposed.Steam.ApiKey);
        Assert.False(ConfigStore.PreserveSteamApiKey(proposed, null));
    }

    private static AgentConfig Load(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-cfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try { return new ConfigStore(path).Get(); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EmptyConfigGetsTheDefaultPagesAndFreshTokens()
    {
        var c = Load("{}");
        Assert.NotEmpty(c.Pages);
        Assert.All(c.Pages, p => Assert.NotEmpty(p.Widgets!));
        Assert.False(string.IsNullOrEmpty(c.Lan.Token));
        Assert.False(string.IsNullOrEmpty(c.Lan.WebhookToken));
        Assert.NotEqual(c.Lan.Token, c.Lan.WebhookToken);
    }

    // The panel converts the forecast itself, so the only values the agent hands it are the two it knows how to show.
    [Theory]
    [InlineData("""{"weather":{"city":"Tallinn","lat":59.437,"lon":24.745}}""", "celsius")]
    [InlineData("""{"weather":{"unit":"fahrenheit"}}""", "fahrenheit")]
    [InlineData("""{"weather":{"unit":" Fahrenheit "}}""", "fahrenheit")]
    [InlineData("""{"weather":{"unit":"kelvin"}}""", "celsius")]
    [InlineData("""{"weather":{"unit":null}}""", "celsius")]
    [InlineData("""{"weather":{"city":"Oslo","unit":123}}""", "celsius")]
    [InlineData("""{"weather":{"city":"Oslo","unit":true}}""", "celsius")]
    [InlineData("""{"weather":{"city":"Oslo","unit":{"deep":[1]}}}""", "celsius")]
    public void WeatherUnitDefaultsToCelsiusAndReachesThePhone(string json, string expected)
    {
        var c = Load(json);
        Assert.Equal(expected, c.Weather.Unit);
        // a bad unit costs only the unit: the rest of the weather section (and the file) still loads
        if (json.Contains("Oslo")) Assert.Equal("Oslo", c.Weather.City);
        var weather = JsonSerializer.SerializeToElement(c.ToPublic(), ConfigStore.WriteOpts).GetProperty("weather");
        Assert.Equal(expected, weather.GetProperty("unit").GetString());
    }

    // The model goes into the phone's forecast URL, so only the names the settings offer get through.
    [Theory]
    [InlineData("""{"weather":{"city":"Tallinn","lat":59.437,"lon":24.745}}""", "")]
    [InlineData("""{"weather":{"model":"gfs_seamless"}}""", "gfs_seamless")]
    [InlineData("""{"weather":{"model":" GFS_Seamless "}}""", "gfs_seamless")]
    [InlineData("""{"weather":{"model":"ecmwf_ifs025"}}""", "ecmwf_ifs025")]
    [InlineData("""{"weather":{"model":"gfs_seamless&daily=x"}}""", "")]
    [InlineData("""{"weather":{"model":"accuweather"}}""", "")]
    [InlineData("""{"weather":{"model":null}}""", "")]
    [InlineData("""{"weather":{"city":"Oslo","model":42}}""", "")]
    [InlineData("""{"weather":{"city":"Oslo","model":["gfs_seamless"]}}""", "")]
    public void WeatherModelIsOneOfTheOfferedModelsOrBestMatch(string json, string expected)
    {
        var c = Load(json);
        Assert.Equal(expected, c.Weather.Model);
        if (json.Contains("Oslo")) Assert.Equal("Oslo", c.Weather.City);
        var weather = JsonSerializer.SerializeToElement(c.ToPublic(), ConfigStore.WriteOpts).GetProperty("weather");
        Assert.Equal(expected, weather.GetProperty("model").GetString());
    }

    [Fact]
    public void BurnInProtectionIsOffByDefaultAndTheDimTimeIsClamped()
    {
        var defaults = Load("{}").Display;
        Assert.False(defaults.PixelShift);
        Assert.Equal(0, defaults.IdleDimMin);
        Assert.Equal(1440, Load("""{ "display": { "pixelShift": true, "idleDimMin": 99999 } }""").Display.IdleDimMin);
        Assert.Equal(0, Load("""{ "display": { "idleDimMin": -5 } }""").Display.IdleDimMin);
        Assert.True(Load("""{ "display": { "pixelShift": true } }""").Display.PixelShift);
    }

    [Fact]
    public void UnreachableNightScreenIsOffByDefaultAndDelayIsClamped()
    {
        var defaults = Load("{}").Display;
        Assert.False(defaults.NightWhenUnreachable);
        Assert.Equal(5, defaults.UnreachableNightDelayMin);
        Assert.Equal(30, Load("""{ "display": { "nightWhenUnreachable": true, "unreachableNightDelayMin": 999 } }""").Display.UnreachableNightDelayMin);
        Assert.Equal(1, Load("""{ "display": { "nightWhenUnreachable": true, "unreachableNightDelayMin": -5 } }""").Display.UnreachableNightDelayMin);
        Assert.True(Load("""{ "display": { "nightWhenUnreachable": true } }""").Display.NightWhenUnreachable);
    }

    [Fact]
    public void DisplayFinishSoundVolumeDefaultsAndRange()
    {
        var defaults = Load("{}").Display;
        Assert.False(defaults.FinishSound);
        Assert.Equal(70, defaults.FinishSoundVolume);
        Assert.Equal(100, Load("""{ "display": { "finishSoundVolume": 999 } }""").Display.FinishSoundVolume);
        Assert.Equal(10, Load("""{ "display": { "finishSoundVolume": -5 } }""").Display.FinishSoundVolume);
        Assert.Equal(10, Load("""{ "display": { "finishSoundVolume": 0 } }""").Display.FinishSoundVolume);
        Assert.Equal(50, Load("""{ "display": { "finishSoundVolume": 50 } }""").Display.FinishSoundVolume);
    }

    [Fact]
    public void TemplatesExpandIntoWidgets()
    {
        var c = Load("""{ "pages": [ { "template": "music" }, { "template": "music" } ] }""");
        Assert.Equal(["music", "music-2"], c.Pages.Select(p => p.Id));
        Assert.Contains(c.Pages[0].Widgets!, w => w.Type == "media");
        Assert.Null(c.Pages[0].Template);
    }

    [Theory]
    [InlineData("Desk / Live", "desk-live")]
    [InlineData("Desk + Live", "desk-live-2")]
    [InlineData("kitchen", "kitchen")]
    public void PageReferencesFollowIdNormalizationOnSaveAndReload(string reference, string expectedId)
    {
        var directory = Directory.CreateTempSubdirectory("phonebox-page-references-");
        try
        {
            var path = Path.Combine(directory.FullName, "config.json");
            var json = JsonSerializer.Serialize(new
            {
                pages = new[] { new { id = "Desk / Live" }, new { id = "Desk + Live" }, new { id = "kitchen" } },
                display = new { homePage = reference },
                phones = new[] { new { id = "desk", pages = new[] { reference } } },
                rules = new[] { new { when = "time", then = "goto", page = reference } },
            });
            var store = new ConfigStore(path);
            Assert.Null(store.Save(json));

            foreach (var config in new[] { store.Get(), new ConfigStore(path).Get() })
            {
                Assert.Equal(["desk-live", "desk-live-2", "kitchen"], config.Pages.Select(p => p.Id));
                var actual = (Home: config.Display.HomePage, Phone: string.Join(",", config.PagesFor("desk").Select(p => p.Id)),
                    Rule: Assert.Single(config.Rules).Page);
                Assert.Equal((expectedId, expectedId, expectedId), actual);
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void ANewTemplateCannotTakeAnExistingPagesId()
    {
        var config = Load("""
            {
              "pages": [
                { "template": "music" },
                { "id": "music", "title": "Existing music page" }
              ]
            }
            """);

        Assert.Equal(["music-2", "music"], config.Pages.Select(p => p.Id));
    }

    [Fact]
    public void ANewWidgetCannotTakeAnExistingWidgetsPersistenceKey()
    {
        var config = Load("""
            {
              "pages": [{
                "id": "desk",
                "widgets": [
                  { "type": "timer" },
                  { "type": "timer", "id": "timer" }
                ]
              }]
            }
            """);

        Assert.Equal(["timer-2", "timer"], Assert.Single(config.Pages).Widgets!.Select(w => w.Id));
    }

    [Fact]
    public void TurkishCapitalDottedIKeepsItsLetterInIds()
    {
        // ToLowerInvariant leaves İ (U+0130) as it is; the id must not lose the letter ("zmir")
        var c = Load("""
        { "pages": [ { "title": "İzmir", "icon": "x", "widgets": [ { "type": "clock" } ] },
                     { "title": "İş Akışı", "icon": "x", "widgets": [ { "type": "clock" } ] } ],
          "shortcuts": [ { "label": "İndir", "cmd": "notepad" } ],
          "rules": [ { "name": "İkindi", "when": "always", "then": "notify" } ] }
        """);
        Assert.Equal(["izmir", "is-akisi"], c.Pages.Select(p => p.Id));
        Assert.Equal("indir", c.Shortcuts.Single().Id);
        Assert.Equal("ikindi", c.Rules.Single().Id);
    }

    [Fact]
    public void ANewShortcutNeverReusesAJustDeletedOnesId()
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-config-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ConfigStore(path);
            Assert.Null(store.Save("""{ "shortcuts": [ { "label": "Terminal", "cmd": "cmd.exe" } ] }"""));
            Assert.Equal("terminal", store.Get().Shortcuts.Single().Id);

            Assert.Null(store.Save("""{ "shortcuts": [ { "label": "Terminal", "cmd": "wt.exe" } ] }"""));
            Assert.Equal("terminal-2", store.Get().Shortcuts.Single().Id);
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void AStaleIdRejectedForANewShortcutDoesNotStealAKeptShortcutsId()
    {
        // A new shortcut's rejected (stale) candidate id must not occupy "used", or processing it before a kept
        // shortcut that still owns that exact id would bump the kept one to an unrelated id instead.
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-config-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ConfigStore(path);
            Assert.Null(store.Save("""{ "shortcuts": [ { "label": "Terminal", "cmd": "a.exe" }, { "label": "Terminal", "cmd": "b.exe" } ] }"""));
            Assert.Equal(["terminal", "terminal-2"], store.Get().Shortcuts.Select(s => s.Id));

            // "terminal" is deleted; a brand-new "Terminal" (no id, listed first) must not end up claiming
            // "terminal-2" ahead of the shortcut that explicitly keeps it.
            Assert.Null(store.Save("""{ "shortcuts": [ { "label": "Terminal", "cmd": "c.exe" }, { "id": "terminal-2", "label": "Terminal", "cmd": "b.exe" } ] }"""));
            Assert.Equal(["terminal-3", "terminal-2"], store.Get().Shortcuts.Select(s => s.Id));
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void ABlankIdShortcutListedFirstDoesNotStealAnExplicitIdListedAfterIt()
    {
        // Explicit ids are reserved in a first pass before any blank-id shortcut is assigned one, so list order
        // cannot let a brand-new shortcut take an id another shortcut in the same save explicitly asked to keep.
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-config-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ConfigStore(path);
            Assert.Null(store.Save("""{ "shortcuts": [ { "label": "Terminal", "cmd": "a.exe" } ] }"""));
            Assert.Equal("terminal", store.Get().Shortcuts.Single().Id);

            Assert.Null(store.Save("""
                { "shortcuts": [ { "label": "Terminal", "cmd": "new.exe" }, { "id": "terminal-2", "label": "Other", "cmd": "x.exe" } ] }
                """));
            Assert.Equal(["terminal-3", "terminal-2"], store.Get().Shortcuts.Select(s => s.Id));
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void LegacyHomePageKeepsItsTilesAndOptions()
    {
        // 0.6.1: a fixed "home" page with options
        var c = Load("""{ "pages": [ { "type": "home", "title": "Start", "options": { "tiles": ["cpu", "ram"], "media": false, "weather": false } } ] }""");
        var p = c.Pages.Single();
        Assert.Equal("Start", p.Title);
        Assert.DoesNotContain(p.Widgets!, w => w.Type == "media");
        var stats = p.Widgets!.Where(w => w.Type == "stat").ToList();
        Assert.Equal(["cpu", "ram"], stats.Select(w => w.Str("metric")));
        Assert.Equal(12, stats.Sum(w => w.W));                         // the tiles share the full width
        Assert.False(p.Widgets!.Single(w => w.Type == "clock").Bool("weather"));
        Assert.Equal(12, p.Widgets!.Single(w => w.Type == "clock").W); // no media: the clock takes the row
    }

    [Fact]
    public void LegacyProcessPageKeepsTheKillSetting()
    {
        var c = Load("""{ "pages": [ { "type": "procs", "options": { "allowKill": false } } ] }""");
        Assert.False(c.Pages.Single().Widgets!.Single(w => w.Type == "processes").Bool("allowKill"));
    }

    [Fact]
    public void WidgetsStayOnTheGridAndGetUniqueIds()
    {
        var c = Load("""
        { "pages": [ { "id": "p", "title": "P", "widgets": [
            { "type": "clock", "x": 10, "y": 5, "w": 20, "h": 9 },
            { "type": "note", "id": "n", "x": -3, "y": 0, "w": 2, "h": 2 },
            { "type": "note", "id": "n", "x": 0, "y": 0, "w": 2, "h": 2 },
            { "type": "no-such-widget", "x": 0, "y": 0, "w": 2, "h": 2 } ] } ] }
        """);
        var ws = c.Pages.Single().Widgets!;
        Assert.Equal(3, ws.Count);                                      // the unknown widget is dropped
        var clock = ws[0];
        Assert.True(clock.X + clock.W <= 12 && clock.Y + clock.H <= 6);
        Assert.Equal(0, ws[1].X);
        Assert.Equal(["clock", "n", "n-2"], ws.Select(w => w.Id));
    }

    [Fact]
    public void AtLeastOnePageStaysEnabled()
    {
        var c = Load("""{ "pages": [ { "template": "clock", "enabled": false }, { "template": "music", "enabled": false } ] }""");
        Assert.True(c.Pages[0].Enabled);
    }

    [Fact]
    public void ThePhoneNeverSeesCommands()
    {
        var c = Load("""
        { "pages": [ { "id": "p", "title": "P", "widgets": [ { "type": "command", "x": 0, "y": 0, "w": 3, "h": 2, "options": { "command": "secret-script.ps1", "commandUnix": "secret-unix.sh", "future": "secret-x", "title": "T" } } ] } ],
          "shortcuts": [ { "id": "s", "label": "S", "cmd": "secret.exe", "args": "--token abc" } ] }
        """);
        var json = JsonSerializer.Serialize(c.ToPublic());
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("--token", json);
        Assert.Contains("\"T\"", json);
    }

    [Fact]
    public void ThePhoneLearnsWhichShortcutPicturesAreUploads()
    {
        // only an uploaded picture may fill a Big button; a program's own icon (also with an iconRev) may not
        var c = Load("""{ "shortcuts": [ { "id": "up", "label": "Up", "cmd": "a.exe" }, { "id": "own", "label": "Own", "cmd": "b.exe" } ] }""");
        var pub = JsonSerializer.SerializeToElement(c.ToPublic(null, s => (7, s.Id == "up")));
        var byId = pub.GetProperty("shortcuts").EnumerateArray().ToDictionary(s => s.GetProperty("Id").GetString()!);
        Assert.True(byId["up"].GetProperty("iconUpload").GetBoolean());
        Assert.False(byId["own"].GetProperty("iconUpload").GetBoolean());
        Assert.Equal(7, byId["own"].GetProperty("iconRev").GetInt64());
        // without the lookup (tests, older callers) nothing counts as an upload
        Assert.All(JsonSerializer.SerializeToElement(c.ToPublic()).GetProperty("shortcuts").EnumerateArray(),
            s => Assert.False(s.GetProperty("iconUpload").GetBoolean()));
    }

    [Fact]
    public void RulesAreCleanedUp()
    {
        var c = Load("""{ "rules": [ { "when": "explode", "then": "goto" }, { "name": "Away", "when": "idle", "value": 99999, "then": "notify", "level": "weird" }, { "name": "Away", "when": "idle", "then": "goto" } ] }""");
        Assert.Equal(["away", "away-2"], c.Rules.Select(r => r.Id));
        Assert.Equal(1440, c.Rules[0].Value);
        Assert.Equal("info", c.Rules[0].Level);
    }
}

public class LanTests
{
    [Theory]
    [InlineData("/api/state", true)]
    [InlineData("/api/stream", true)]
    [InlineData("/api/run/terminal", true)]
    [InlineData("/api/apps", true)]
    [InlineData("/api/apps/open", true)]
    [InlineData("/api/feeds", true)]
    [InlineData("/api/feeds/open", true)]
    [InlineData("/api/photos", true)]
    [InlineData("/api/photos/0123456789abcdef", true)]
    [InlineData("/api/steam", true)]
    [InlineData("/api/steam/avatar", true)]
    [InlineData("/api/steam/other", false)]
    [InlineData("/api/steam/library", true)]
    [InlineData("/api/steam/cover", true)]
    [InlineData("/api/steam/launch", true)]
    [InlineData("/api/steam/library/x", false)]
    [InlineData("/api/steam/games", false)]
    [InlineData("/api/lyrics", true)]
    [InlineData("/api/lyrics/search", false)]
    [InlineData("/api/push/temperature", true)]
    [InlineData("/api/settings", false)]
    [InlineData("/api/pair", false)]
    [InlineData("/api/pair/claim", true)]
    [InlineData("/api/pair/start", false)]
    [InlineData("/api/pair/cancel", false)]
    [InlineData("/api/pair/status", false)]
    [InlineData("/api/pair/claim/x", false)]
    [InlineData("/api/shortcuts/test", false)]
    [InlineData("/api/shortcuts/icon", true)]
    [InlineData("/api/settings/shortcut-icon", false)]
    [InlineData("/api/claude/hook", false)]
    [InlineData("/api/codex/hook", false)]
    [InlineData("/api/obs-test", false)]
    [InlineData("/api/ha/test", false)]
    [InlineData("/api/phones", false)]
    [InlineData("/api/rules/test", false)]
    [InlineData("/panel/main.js", false)]
    [InlineData("/", false)]
    public void OnlyThePhoneApiIsReachableOverWifi(string path, bool allowed) => Assert.Equal(allowed, Lan.AllowedOverLan(new PathString(path)));

    [Theory]
    [InlineData("/api/push/x", true)]
    [InlineData("/api/notify", true)]
    [InlineData("/api/goto/2", true)]
    [InlineData("/api/refresh", true)]
    [InlineData("/api/state", false)]
    [InlineData("/api/run/x", false)]
    public void TheWebhookKeyOpensOnlyWebhooks(string path, bool webhook) => Assert.Equal(webhook, Lan.IsWebhook(new PathString(path)));

    [Fact]
    public void TokensAreComparedExactly()
    {
        Assert.True(Lan.Matches("Bearer abc123", "abc123"));
        Assert.False(Lan.Matches("Bearer abc12", "abc123"));
        Assert.False(Lan.Matches("abc123", "abc123"));
        Assert.False(Lan.Matches(null, "abc123"));
        Assert.True(Lan.MatchesQuery("abc123", "abc123"));
        Assert.False(Lan.MatchesQuery("", "abc123"));
        Assert.False(Lan.MatchesQuery(null, "abc123"));
    }

    [Theory]
    // A Hyper-V external switch owns the physical NIC's address and gateway: skipping it emptied
    // the pairing QR's host list (#175). Host-only and NAT virtual adapters have no gateway.
    [InlineData("Hyper-V Virtual Ethernet Adapter vEthernet (WAN)", true, false)]
    [InlineData("Hyper-V Virtual Ethernet Adapter vEthernet (Default Switch)", false, true)]
    [InlineData("WSL adapter vEthernet (WSL)", false, true)]
    [InlineData("VMware Virtual Ethernet Adapter VMnet8", false, true)]
    [InlineData("Bluetooth Device (Personal Area Network)", false, true)]
    [InlineData("Intel(R) Ethernet Controller I219-LM Ethernet", false, false)]
    [InlineData("Intel(R) Ethernet Controller I219-LM Ethernet", true, false)]
    public void VirtualAdaptersAreSkippedUnlessTheyCarryTheDefaultRoute(string desc, bool hasGateway, bool skip) =>
        Assert.Equal(skip, Lan.ShouldSkipAdapter(desc, hasGateway));
}

public class PushTests
{
    [Theory]
    [InlineData("temperature", true)]
    [InlineData("build.main-ci_2", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("../etc", false)]
    public void Keys(string key, bool valid) => Assert.Equal(valid, PushStore.ValidKey(key));

    [Fact]
    public void ValuesAreTyped()
    {
        Assert.Equal(JsonValueKind.Number, PushStore.ParseValue("21.5").ValueKind);
        Assert.Equal(JsonValueKind.True, PushStore.ParseValue("true").ValueKind);
        Assert.Equal(JsonValueKind.String, PushStore.ParseValue("passed").ValueKind);
        Assert.Equal(JsonValueKind.String, PushStore.ParseValue("NaN").ValueKind);
    }
}

/// <summary>Audit F10: a settings tab loaded earlier must not silently undo a change saved since (a stale tab once turned the night screen back on).</summary>
public class ConfigConcurrencyTests
{
    private static (ConfigStore Store, string Path) NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        return (new ConfigStore(path), path);
    }

    private static string With(ConfigStore s, Action<AgentConfig> edit)
    {
        var c = System.Text.Json.JsonSerializer.Deserialize<AgentConfig>(System.Text.Json.JsonSerializer.Serialize(s.Get(), ConfigStore.WriteOpts), ConfigStore.WriteOpts)!;
        edit(c);
        return System.Text.Json.JsonSerializer.Serialize(c, ConfigStore.WriteOpts);
    }

    [Fact]
    public void AStaleSaveIsRefusedAndTheNewerChangeStays()
    {
        var (s, path) = NewStore();
        try
        {
            var loadedByTabA = s.Version;
            var tabA = With(s, c => c.Night.Brightness = 0.03);
            Thread.Sleep(20);
            Assert.Null(s.Save(With(s, c => c.Night.Start = "01:11"), s.Version));   // tab B, up to date
            Assert.Equal(ConfigStore.Conflict, s.Save(tabA, loadedByTabA));           // tab A, stale
            Assert.Equal("01:11", s.Get().Night.Start);
            Assert.Equal(0.02, s.Get().Night.Brightness);
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void EveryWriteMovesTheVersionForward()
    {
        // The version is the file's write time, which Windows keeps in coarse steps: two quick writes could share one
        // version, and a stale save got through. A write time ahead of the clock stands in for "the same step" here, on
        // any machine: a write must still move the version past it.
        var (s, path) = NewStore();
        try
        {
            var ahead = DateTime.UtcNow.AddMinutes(5);
            File.SetLastWriteTimeUtc(path, ahead);
            var loaded = s.Version;
            Assert.Null(s.Save(With(s, c => c.Night.Start = "01:11"), loaded));
            Assert.True(s.Version > loaded);
            Assert.Equal(ConfigStore.Conflict, s.Save(With(s, c => c.Night.End = "06:06"), loaded)); // the stale one

            File.SetLastWriteTimeUtc(path, ahead);
            loaded = s.Version;
            s.Touch();
            Assert.True(s.Version > loaded);
            Assert.True(s.VersionConflict(loaded.ToString()));
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void TouchAdvancesVersionSoAnotherTabsConflictCheckNoticesIt()
    {
        var (s, path) = NewStore();
        try
        {
            var loaded = s.Version;
            Assert.False(s.VersionConflict(loaded.ToString()));
            Assert.False(s.VersionConflict(null)); // no header sent: never a conflict
            Thread.Sleep(20);
            s.Touch();
            Assert.True(s.VersionConflict(loaded.ToString()));
            Assert.False(s.VersionConflict(s.Version.ToString()));
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void GeneratedKeysPersistForANewConfigWithoutReplacingUnreadableFiles()
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-config-");
        try
        {
            var path = Path.Combine(dir.FullName, "config.json");
            var store = new ConfigStore(path);
            var generated = store.Get().Lan;
            store.PersistGenerated();
            Assert.True(File.Exists(path));
            var restarted = new ConfigStore(path).Get().Lan;
            Assert.Equal(generated.Token, restarted.Token);
            Assert.Equal(generated.WebhookToken, restarted.WebhookToken);

            const string invalid = "{ incomplete";
            File.WriteAllText(path, invalid);
            new ConfigStore(path).PersistGenerated();
            Assert.Equal(invalid, File.ReadAllText(path));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void DeletingSettingsInvalidatesTheVersionHeldByOpenTabs()
    {
        var (store, path) = NewStore();
        try
        {
            var version = store.Version;
            var stale = With(store, c => c.Night.Start = "01:11");
            File.Delete(path);

            Assert.Equal(ConfigStore.Conflict, store.Save(stale, version));
            Assert.False(File.Exists(path));
            Assert.Null(store.Save(stale, store.Version));
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnUpToDateSaveAndASaveWithoutAVersionGoThrough()
    {
        var (s, path) = NewStore();
        try
        {
            Assert.Null(s.Save(With(s, c => c.Night.Start = "01:11"), s.Version));
            Thread.Sleep(20);
            Assert.Null(s.Save(With(s, c => c.Night.Start = "02:22"), null));        // scripts and backups send no version
            Assert.Equal("02:22", s.Get().Night.Start);
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void DisplayLanguageDefaultsAndExposesToPublic()
    {
        var (s, path) = NewStore();
        try
        {
            Assert.Equal("auto", s.Get().Display.Language);

            Assert.Null(s.Save("""{"display":{"language":"tr"}}"""));
            Assert.Equal("tr", s.Get().Display.Language);
            var pub = JsonSerializer.Serialize(s.Get().ToPublic(), ConfigStore.WriteOpts);
            using var doc = JsonDocument.Parse(pub);
            Assert.Equal("tr", doc.RootElement.GetProperty("display").GetProperty("language").GetString());
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Theory]
    [InlineData("\"TR\"", "tr")]       // any case
    [InlineData("\" de \"", "de")]     // stray spaces
    [InlineData("\"vi\"", "vi")]       // added after the list was first written (#177)
    [InlineData("\"pt-BR\"", "pt-BR")] // distinct from pt; casing must survive the round trip, i18n.js looks it up by exact code
    [InlineData("\"pt-br\"", "pt-BR")] // any case still normalizes to the canonical pt-BR, not a lowercase copy
    [InlineData("\"zh-hant\"", "zh-Hant")]
    [InlineData("\"AR\"", "ar")]
    [InlineData("\"xx\"", "auto")]     // a language PhoneBox does not have
    [InlineData("null", "auto")]
    [InlineData("\"\"", "auto")]
    public void ADisplayLanguageItDoesNotKnowFollowsThePhone(string json, string expected)
    {
        var (s, path) = NewStore();
        try
        {
            Assert.Null(s.Save("{\"display\":{\"language\":" + json + "}}"));
            Assert.Equal(expected, s.Get().Display.Language);
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void EmbeddedI18nResourceExists()
    {
        using var s = AppPaths.Resource("www/i18n.js");
        Assert.NotNull(s);
    }

    [Fact]
    public void EveryLanguageThePanelHasCanBeSaved()
    {
        // a language added to i18n.js but not to DisplayConfig.Languages is saved as "auto", so picking it does nothing
        using var s = AppPaths.Resource("www/i18n.js");
        Assert.NotNull(s);
        var js = new StreamReader(s!).ReadToEnd();
        var codes = System.Text.RegularExpressions.Regex.Matches(js, @"\{\s*code:\s*'([A-Za-z]{2,3}(?:-[A-Za-z]{2,4})?)'").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Contains("en", codes);
        Assert.Equal(codes.Order(), DisplayConfig.Languages.Where(l => l != "auto").Order());
    }
}

public class Hour12ConfigTests
{
    [Theory]
    [InlineData("{}", "false", JsonValueKind.False)]
    [InlineData("""{"display":{"hour12":false}}""", "false", JsonValueKind.False)]
    [InlineData("""{"display":{"hour12":true}}""", "true", JsonValueKind.True)]
    [InlineData("""{"display":{"hour12":"true"}}""", "true", JsonValueKind.True)]
    [InlineData("""{"display":{"hour12":"12h"}}""", "true", JsonValueKind.True)]
    [InlineData("""{"display":{"hour12":"false"}}""", "false", JsonValueKind.False)]
    [InlineData("""{"display":{"hour12":"auto"}}""", "auto", JsonValueKind.String)]
    [InlineData("""{"display":{"hour12":" AUTO "}}""", "auto", JsonValueKind.String)]
    [InlineData("""{"display":{"hour12":"bogus"}}""", "false", JsonValueKind.False)]
    [InlineData("""{"display":{"hour12":null}}""", "false", JsonValueKind.False)]
    [InlineData("""{"display":{"hour12":123}}""", "false", JsonValueKind.False)]
    [InlineData("""{"display":{"hour12":["bad"]}}""", "false", JsonValueKind.False)]
    public void DisplayHour12NormalizesAndPreservesWireTypes(string inputJson, string expectedNormalized, JsonValueKind expectedWrittenKind)
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-h12-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{}");
            var store = new ConfigStore(path);
            Assert.Null(store.Save(inputJson));
            Assert.Equal(expectedNormalized, store.Get().Display.Hour12);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var prop = doc.RootElement.GetProperty("display").GetProperty("hour12");
            Assert.Equal(expectedWrittenKind, prop.ValueKind);
            if (expectedWrittenKind == JsonValueKind.String) Assert.Equal("auto", prop.GetString());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }
}

public class PageBarConfigTests
{
    [Theory]
    [InlineData("{}", "dots", "bottom")]
    [InlineData("""{"display":{"pageBar":"bar","pageBarSide":"left"}}""", "bar", "left")]
    [InlineData("""{"display":{"pageBar":"auto","pageBarSide":"right"}}""", "auto", "right")]
    [InlineData("""{"display":{"pageBar":"hidden"}}""", "hidden", "bottom")]
    [InlineData("""{"display":{"pageBar":"sideways","pageBarSide":"top"}}""", "dots", "bottom")]
    [InlineData("""{"display":{"pageBar":null,"pageBarSide":null}}""", "dots", "bottom")]
    public void APageBarSettingThePanelDoesNotKnowIsSavedAsTheDefault(string inputJson, string bar, string side)
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-pagebar-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{}");
            var store = new ConfigStore(path);
            Assert.Null(store.Save(inputJson));
            Assert.Equal((bar, side), (store.Get().Display.PageBar, store.Get().Display.PageBarSide));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }
}
