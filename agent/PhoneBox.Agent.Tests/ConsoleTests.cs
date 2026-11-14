using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class ConsoleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-console-" + Guid.NewGuid().ToString("N"));

    public ConsoleTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeWindowPlatform : IWindowPlatform
    {
        public ConsoleWindowInfo? Foreground { get; set; }
        public List<ConsoleWindowInfo> Windows { get; set; } = [];
        public int GetForegroundCalls { get; private set; }
        public int FocusCalls { get; private set; }
        public string? LastFocusApp { get; private set; }
        public string? LastFocusTitle { get; private set; }
        public bool FocusResult { get; set; } = true;

        public ConsoleWindowInfo? GetForeground(bool readTitle)
        {
            GetForegroundCalls++;
            if (Foreground is null) return null;
            return readTitle ? Foreground : Foreground with { Title = "" };
        }

        public IReadOnlyList<ConsoleWindowInfo> ListWindows() => Windows;

        public bool FocusWindow(string processMatch, string? titleMatch)
        {
            FocusCalls++;
            LastFocusApp = processMatch;
            LastFocusTitle = titleMatch;
            return FocusResult;
        }
    }

    private sealed class FakeDeckNative : IDeckNative
    {
        public readonly List<string> Writes = [];
        public List<DeckSession> Sessions = [];
        public List<DeckMonitor> MonitorList = [];
        public int OutputVol = 40;
        public int MicVol = 70;

        public DeckAudio Audio() => new(true, null,
            [
                new("speaker", "Speaker", "output", true, OutputVol, false, 0, 2),
                new("mic", "Mic", "input", true, MicVol, false, 0, 1),
            ],
            Sessions.ToArray());

        public bool Device(string id, int? volume, bool? muted, int? channel = null)
        {
            Writes.Add($"device:{id}:{volume}");
            if (id == "mic" && volume is { } mv) MicVol = mv;
            if (id == "speaker" && volume is { } ov) OutputVol = ov;
            return true;
        }

        public bool Session(string id, int? volume, bool? muted)
        {
            Writes.Add($"session:{id}:{volume}");
            Sessions = Sessions.Select(s => s.Id == id
                ? s with { Volume = volume ?? s.Volume, Muted = muted ?? s.Muted }
                : s).ToList();
            return Sessions.Any(s => s.Id == id);
        }

        public bool DefaultDevice(string id, int? role = null) => true;
        public IReadOnlyList<DeckMonitor> Monitors() => MonitorList;

        public bool Brightness(string id, int value)
        {
            Writes.Add($"brightness:{id}:{value}");
            MonitorList = MonitorList.Select(m => m.Id == id ? m with { Brightness = value } : m).ToList();
            return MonitorList.Any(m => m.Id == id);
        }

        public IReadOnlyList<DeckItem> PowerPlans() => [];
        public Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeckMedia>>([]);
        public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct) => Task.FromResult(false);
        public void Play(string id, string file, string? device, int volume) { }
        public void StopSound(string? id) { }
        public IReadOnlyList<DeckItem> MidiPorts() => [];
        public void Midi(int port, string message, int value) { }
        public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
        public void Dispose() { }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly FakeWindowPlatform Platform = new();
        public readonly FakeDeckNative Native = new();
        public readonly AudioControl Audio = new();
        public readonly MediaMonitor Media = new(NullLogger<MediaMonitor>.Instance);
        public readonly ObsMonitor Obs;
        public readonly PushStore Push;
        public readonly ControlDeck Deck;
        public readonly ConsoleService Service;
        public readonly ConfigStore Config;
        public readonly List<string> SentKeys = [];
        public readonly List<string> TypedTexts = [];
        public long Now = 1_000;

        public Fixture(string dir, string json = "{}")
        {
            var cfgPath = Path.Combine(dir, $"cfg-{Guid.NewGuid():N}.json");
            File.WriteAllText(cfgPath, json);
            Config = new ConfigStore(cfgPath);
            Push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(dir, $"data-{Guid.NewGuid():N}.json"));
            Obs = new ObsMonitor(Config, NullLogger<ObsMonitor>.Instance);
            Deck = new ControlDeck(Config, Native, NullLogger<ControlDeck>.Instance, () => Now, Push, Audio);
            Service = new ConsoleService(
                Config,
                NullLogger<ConsoleService>.Instance,
                Audio,
                Media,
                Obs,
                Deck,
                Push,
                Platform,
                () => Now,
                keys => SentKeys.Add(keys),
                text => TypedTexts.Add(text));
        }

        public void Dispose() => Deck.Dispose();
    }

    [Fact]
    public void NormalizeAppliesDefaultsPreservesExplicitEmptyAndClampsBounds()
    {
        var defaults = AppConsole.Normalize(null);
        Assert.NotNull(defaults.Profiles);
        Assert.Contains(defaults.Profiles, p => p.Id == "desktop" && p.IsDefault);
        Assert.Contains(defaults.Profiles, p => p.Id == "vscode");
        Assert.Contains(defaults.Profiles, p => p.Id == "browser");

        var emptyPath = Path.Combine(_dir, "empty.json");
        File.WriteAllText(emptyPath, """{"console":{"profiles":[]}}""");
        var emptyCfg = new ConfigStore(emptyPath).Get();
        Assert.Empty(emptyCfg.Console.Profiles!);

        var dirty = new AppConsoleConfig
        {
            Profiles =
            [
                new ConsoleProfile
                {
                    Id = "",
                    Name = "İçerik Düzeni",
                    Color = "javascript:alert(1)",
                    Columns = 99,
                    Rows = 0,
                    IsDefault = true,
                    SubPages =
                    [
                        new ConsoleSubPage
                        {
                            Id = "",
                            Name = "Birinci",
                            Slots =
                            [
                                new ConsoleSlot { Kind = "powershell", Label = "Run", Keys = "Ctrl+B", Hint = "01234567890123456789EXTRA" },
                                new ConsoleSlot { Kind = "empty", Label = "X", Keys = "Ctrl+X", Target = "secret", Hint = "nope", Toggle = true, StateKey = "k" },
                            ],
                        },
                    ],
                    Axes =
                    [
                        new ConsoleAxis { Index = 99, Kind = "bad-kind", Sensitivity = "ultra", Name = "Dial" },
                        new ConsoleAxis { Index = 8, Kind = "mic-gain", Name = "Dup" },
                    ],
                },
                new ConsoleProfile { Id = "", Name = "Second", IsDefault = true, Columns = 1, Rows = 99 },
            ],
        };

        var normalized = AppConsole.Normalize(dirty);
        var first = normalized.Profiles![0];
        var second = normalized.Profiles[1];

        Assert.Equal("icerik-duzeni", first.Id);
        Assert.Equal("#38bdf8", first.Color);
        Assert.Equal(6, first.Columns);
        Assert.Equal(2, first.Rows);
        Assert.True(first.IsDefault);
        Assert.False(second.IsDefault);
        Assert.Equal(2, second.Columns);
        Assert.Equal(4, second.Rows);

        var s0 = first.SubPages![0].Slots![0];
        var s1 = first.SubPages[0].Slots![1];
        Assert.Equal("keys", s0.Kind);
        Assert.Equal(20, s0.Hint!.Length);
        Assert.Equal("empty", s1.Kind);
        Assert.Null(s1.Keys);
        Assert.Null(s1.Target);
        Assert.Null(s1.Hint);
        Assert.False(s1.Toggle);

        Assert.Single(first.Axes!);
        Assert.Equal(8, first.Axes![0].Index);
        Assert.Equal("keys", first.Axes[0].Kind);
        Assert.Equal("normal", first.Axes[0].Sensitivity);
    }

    [Fact]
    public void ToPublicNeverExposesCommandsTargetsOrMatchers()
    {
        var cfg = AppConsole.Normalize(new AppConsoleConfig
        {
            Profiles =
            [
                new ConsoleProfile
                {
                    Id = "obs",
                    Name = "OBS",
                    AppMatch = "obs64",
                    TitleMatch = "Recording",
                    SubPages =
                    [
                        new ConsoleSubPage
                        {
                            Id = "main",
                            Name = "Main",
                            Slots =
                            [
                                new ConsoleSlot { Kind = "keys", Label = "Rec", Keys = "Ctrl+Shift+R", Hint = "Ctrl+Shift+R", StateKey = "obs:rec", Toggle = true },
                                new ConsoleSlot { Kind = "text", Label = "Snippet", Text = "secret token", Hint = "should hide" },
                                new ConsoleSlot { Kind = "macro", Label = "Macro", Steps = ["keys:Ctrl+A", "wait:50"] },
                                new ConsoleSlot { Kind = "shortcut", Label = "Run", Target = "secret-cmd" },
                            ],
                        },
                    ],
                    Axes =
                    [
                        new ConsoleAxis { Index = 1, Kind = "keys", PrevKeys = "Ctrl+[", NextKeys = "Ctrl+]", TapAction = "Ctrl+0", AppTarget = "Spotify.exe" },
                    ],
                },
            ],
        });

        var json = JsonSerializer.Serialize(AppConsole.ToPublic(cfg), ConfigStore.WriteOpts);
        using var doc = JsonDocument.Parse(json);

        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "keys", "text", "steps", "target", "appMatch", "titleMatch", "prevKeys", "nextKeys", "tapAction", "appTarget",
        };
        var foundProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPropertyNames(doc.RootElement, foundProps);

        foreach (var secretKey in forbidden)
            Assert.DoesNotContain(secretKey, foundProps);

        Assert.DoesNotContain("secret token", json);
        Assert.DoesNotContain("secret-cmd", json);
        Assert.DoesNotContain("should hide", json);

        var slot0 = doc.RootElement.GetProperty("profiles")[0].GetProperty("subPages")[0].GetProperty("slots")[0];
        Assert.Equal("Ctrl+Shift+R", slot0.GetProperty("hint").GetString());
        var slot1 = doc.RootElement.GetProperty("profiles")[0].GetProperty("subPages")[0].GetProperty("slots")[1];
        Assert.False(slot1.TryGetProperty("hint", out var h) && h.ValueKind != JsonValueKind.Null);
        var axis0 = doc.RootElement.GetProperty("profiles")[0].GetProperty("axes")[0];
        Assert.True(axis0.GetProperty("hasTap").GetBoolean());
    }

    [Fact]
    public void MatchProfilePrefersAppAndTitleThenTitleThenAppAndStripsExe()
    {
        var profiles = new List<ConsoleProfile>
        {
            new() { Id = "desktop", Name = "Desktop", IsDefault = true },
            new() { Id = "obs", Name = "OBS", AppMatch = "obs64" },
            new() { Id = "obs-rec", Name = "OBS Rec", AppMatch = "obs64", TitleMatch = "Recording" },
            new() { Id = "yt", Name = "YT", TitleMatch = "YouTube Studio" },
            new() { Id = "disabled", Name = "Disabled", AppMatch = "notepad", Enabled = false },
        };

        Assert.Equal("obs", ConsoleService.MatchProfile(profiles, new("obs64.exe", "OBS 30 - Profile"))?.Id);
        Assert.Equal("obs-rec", ConsoleService.MatchProfile(profiles, new("obs64.exe", "OBS 30 - Recording"))?.Id);
        Assert.Equal("yt", ConsoleService.MatchProfile(profiles, new("chrome.exe", "Channel - YouTube Studio"))?.Id);
        Assert.Null(ConsoleService.MatchProfile(profiles, new("notepad.exe", "notes.txt")));
    }

    [Fact]
    public void StepPollAppliesAntiFlickerDebounceBeforeDroppingMatch()
    {
        using var f = new Fixture(_dir);
        f.Platform.Foreground = new("Code.exe", "main.ts - Visual Studio Code");
        f.Service.StepPoll();
        Assert.Equal("vscode", f.Service.Snapshot().MatchedProfileId);
        Assert.Equal("vscode", f.Service.Snapshot().EffectiveProfileId);

        f.Platform.Foreground = new("explorer.exe", "Desktop");
        f.Now = 1_100;
        f.Service.StepPoll();
        Assert.Equal("vscode", f.Service.Snapshot().MatchedProfileId);

        f.Now = 1_549;
        f.Service.StepPoll();
        Assert.Equal("vscode", f.Service.Snapshot().MatchedProfileId);

        f.Now = 1_550;
        f.Service.StepPoll();
        Assert.Null(f.Service.Snapshot().MatchedProfileId);
        Assert.Equal("desktop", f.Service.Snapshot().EffectiveProfileId);
    }

    [Fact]
    public async Task FocusBeforeKeysFocusesBackgroundAppAndErrorsWhenClosed()
    {
        var cfgJson = JsonSerializer.Serialize(new
        {
            @console = new
            {
                autoFollow = true,
                focusBeforeKeys = true,
                profiles = new object[]
                {
                    new
                    {
                        id = "obs",
                        name = "OBS Studio",
                        appMatch = "obs64",
                        subPages = new object[]
                        {
                            new
                            {
                                id = "main",
                                name = "Main",
                                slots = new object[]
                                {
                                    new { kind = "keys", label = "Save", keys = "Ctrl+B", toggle = true },
                                    new { kind = "text", label = "Type", text = "hello" },
                                    new { kind = "shortcut", label = "Missing", target = "missing-id" },
                                },
                            },
                        },
                    },
                },
            },
        });
        using var f = new Fixture(_dir, cfgJson);
        f.Platform.Foreground = new("Finder", "");
        f.Platform.FocusResult = false;

        var missing = await f.Service.ActAsync(new("slot", ProfileId: "obs", SubPageId: "main", Slot: 0), default);
        Assert.False(missing.Ok);
        Assert.Equal("Start OBS Studio on the computer first", missing.Error);
        Assert.Equal(1, f.Platform.FocusCalls);
        Assert.Empty(f.SentKeys);

        f.Platform.FocusResult = true;
        var okKeys = await f.Service.ActAsync(new("slot", ProfileId: "obs", SubPageId: "main", Slot: 0), default);
        Assert.True(okKeys.Ok);
        Assert.Equal(2, f.Platform.FocusCalls);
        Assert.Equal("obs64", f.Platform.LastFocusApp);
        Assert.Equal(["Ctrl+B"], f.SentKeys);
        Assert.True(okKeys.State!.SlotStates["obs:main:0"]);

        var okText = await f.Service.ActAsync(new("slot", ProfileId: "obs", SubPageId: "main", Slot: 1), default);
        Assert.True(okText.Ok);
        Assert.Equal(3, f.Platform.FocusCalls);
        Assert.Equal(["hello"], f.TypedTexts);

        var errShortcut = await f.Service.ActAsync(new("slot", ProfileId: "obs", SubPageId: "main", Slot: 2), default);
        Assert.False(errShortcut.Ok);
        Assert.Equal(3, f.Platform.FocusCalls);
    }

    [Fact]
    public async Task AxesAndPinUpdateTargetAudioMonitorsKeysAndSnapshot()
    {
        var cfgJson = JsonSerializer.Serialize(new
        {
            @console = new
            {
                autoFollow = true,
                focusBeforeKeys = true,
                profiles = new object[]
                {
                    new { id = "desktop", name = "Desktop", isDefault = true },
                    new
                    {
                        id = "studio",
                        name = "Studio",
                        appMatch = "studio",
                        axes = new object[]
                        {
                            new { index = 1, name = "Spotify", kind = "app-volume", appTarget = "Spotify", sensitivity = "normal" },
                            new { index = 2, name = "Mic", kind = "mic-gain", appTarget = "mic", sensitivity = "fine" },
                            new { index = 3, name = "Display", kind = "brightness", appTarget = "mon1", sensitivity = "coarse" },
                            new { index = 4, name = "Timeline", kind = "keys", prevKeys = "Left", nextKeys = "Right", tapAction = "Space", sensitivity = "normal" },
                        },
                    },
                },
            },
        });
        using var f = new Fixture(_dir, cfgJson);
        f.Native.Sessions = [new("sp-1", "Spotify", 42, "speaker", 50, false, 0)];
        f.Native.MonitorList = [new("mon1", "Primary", 60, 1920, 1080, 60)];
        for (var i = 0; i < 100 && f.Deck.CurrentMonitors().Count == 0; i++) await Task.Delay(10);
        f.Platform.Foreground = new("studio.exe", "Studio");
        f.Service.StepPoll();

        var resVol = await f.Service.ActAsync(new("axis", ProfileId: "studio", Axis: 1, Delta: 2), default);
        Assert.True(resVol.Ok);
        Assert.Equal(56, f.Native.Sessions[0].Volume);

        var resMic = await f.Service.ActAsync(new("axis", ProfileId: "studio", Axis: 2, Delta: -1), default);
        Assert.True(resMic.Ok);
        Assert.Equal(69, f.Native.MicVol);

        var resBright = await f.Service.ActAsync(new("axis", ProfileId: "studio", Axis: 3, Delta: 1), default);
        Assert.True(resBright.Ok);
        Assert.Equal(66, f.Native.MonitorList[0].Brightness);

        var resKeys = await f.Service.ActAsync(new("axis", ProfileId: "studio", Axis: 4, Delta: 9), default);
        Assert.True(resKeys.Ok);
        Assert.Equal(5, f.SentKeys.Count);
        Assert.All(f.SentKeys, k => Assert.Equal("Right", k));

        var resTap = await f.Service.ActAsync(new("axis-tap", ProfileId: "studio", Axis: 4), default);
        Assert.True(resTap.Ok);
        Assert.Equal("Space", f.SentKeys.Last());

        var badPin = await f.Service.ActAsync(new("pin", Pinned: "does-not-exist"), default);
        Assert.False(badPin.Ok);

        var pinDesktop = await f.Service.ActAsync(new("pin", Pinned: "desktop"), default);
        Assert.True(pinDesktop.Ok);
        Assert.Equal("desktop", pinDesktop.State!.PinnedProfileId);
        Assert.Equal("desktop", pinDesktop.State.EffectiveProfileId);

        var unpin = await f.Service.ActAsync(new("pin", Pinned: ""), default);
        Assert.True(unpin.Ok);
        Assert.Null(unpin.State!.PinnedProfileId);
        Assert.Equal("studio", unpin.State.EffectiveProfileId);
    }

    [Fact]
    public void LanAllowlistExposesOnlyStateAndActNeverWindows()
    {
        Assert.True(Lan.AllowedOverLan("/api/console/state"));
        Assert.True(Lan.AllowedOverLan("/api/console/act"));
        Assert.False(Lan.AllowedOverLan("/api/console/windows"));

        Assert.False(Lan.IsWebhook("/api/console/state"));
        Assert.False(Lan.IsWebhook("/api/console/act"));
        Assert.False(Lan.IsWebhook("/api/console/windows"));
    }

    private static void CollectPropertyNames(JsonElement element, HashSet<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    names.Add(prop.Name);
                    CollectPropertyNames(prop.Value, names);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectPropertyNames(item, names);
                break;
        }
    }
}
