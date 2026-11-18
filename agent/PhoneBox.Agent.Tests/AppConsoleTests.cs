using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The app console sends a profile's keys only to the app it is for, and only when a widget allows it.</summary>
public sealed class AppConsoleTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-console-{Guid.NewGuid():N}.json");
    private readonly List<string> _sent = [];
    private FocusedApp? _focused = new("Photoshop", false);

    private const string Profiles = """
        [{ "id": "ps", "label": "Photoshop", "apps": "photoshop.exe, gimp",
           "keys": [{ "label": "Undo", "keys": "ctrl+z" }, { "label": "Save both", "keys": "ctrl+s, ctrl+shift+s" }],
           "dialLeft": "minus", "dialRight": "equals" }]
        """;

    public void Dispose() { try { File.Delete(_path); File.Delete(_path + ".bak"); } catch { } }

    private AppConsole Make(bool control = true, bool pageEnabled = true)
    {
        File.WriteAllText(_path, $$"""
            { "pages": [ { "id": "p", "title": "P", "enabled": {{(pageEnabled ? "true" : "false")}},
                "widgets": [ { "id": "c", "type": "app-console", "x": 0, "y": 0, "w": 6, "h": 4, "options": { "control": {{(control ? "true" : "false")}} } } ] },
                { "id": "q", "title": "Q", "enabled": true, "widgets": [
                  { "id": "c", "type": "app-console", "x": 0, "y": 0, "w": 6, "h": 4, "options": { "control": false } },
                  { "id": "s", "type": "stack", "x": 6, "y": 0, "w": 6, "h": 4, "options": { "widgets": [
                    { "id": "on", "type": "app-console", "options": { "control": true } },
                    { "id": "off", "type": "app-console", "enabled": false, "options": { "control": true } } ] } } ] } ],
              "console": { "profiles": {{Profiles}} } }
            """);
        return new AppConsole(new ConfigStore(_path), NullLogger<AppConsole>.Instance)
        {
            Supported = true,
            AgentElevated = false,
            Focused = () => _focused,
            SendCombo = combo => { _sent.Add(string.Join("+", combo)); return true; },
        };
    }

    private static JsonElement Json(object body) => JsonSerializer.SerializeToElement(body);

    [Fact]
    public void TheStateNamesTheProfileButNeverTheApp()
    {
        var (status, body) = Make().State(null, "p/c");
        Assert.Equal(200, status);
        var json = Json(body).GetRawText();
        Assert.Contains("\"Id\":\"ps\"", json);
        Assert.Contains("Undo", json);
        Assert.DoesNotContain("ctrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("photoshop.exe", json, StringComparison.OrdinalIgnoreCase);

        _focused = new("notepad", false);
        Assert.Equal(JsonValueKind.Null, Json(Make().State(null, "p/c").Body).GetProperty("profile").ValueKind);
    }

    [Fact]
    public async Task KeysGoToTheMatchingApp()
    {
        var console = Make();
        Assert.Equal((200, (string?)null), await console.Act(null, "p/c", "ps", "k1", 1, default));
        Assert.Equal(["ctrl+s", "ctrl+shift+s"], _sent);
        _focused = new("GIMP", false); // names match without case and without .exe
        Assert.Equal(200, (await console.Act(null, "p/c", "ps", "k0", 1, default)).Status);
    }

    [Fact]
    public async Task NothingIsSentWithoutAWidgetThatAllowsIt()
    {
        Assert.Equal(403, (await Make(control: false).Act(null, "p/c", "ps", "k0", 1, default)).Status);
        Assert.Equal(403, (await Make(pageEnabled: false).Act(null, "p/c", "ps", "k0", 1, default)).Status);
        Assert.Equal(404, Make(pageEnabled: false).State(null, "p/c").Status);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task EachWidgetsOwnSwitchCounts()
    {
        var console = Make();
        // page q has a console with sending off: page p's switch doesn't open it
        Assert.Equal(403, (await console.Act(null, "q/c", "ps", "k0", 1, default)).Status);
        Assert.False(Json(console.State(null, "q/c").Body).GetProperty("control").GetBoolean());
        // a Smart Stack card is named by the stack and the card; a hidden card is not there at all
        Assert.Equal(200, (await console.Act(null, "q/s:on", "ps", "k0", 1, default)).Status);
        Assert.Equal(403, (await console.Act(null, "q/s:off", "ps", "k0", 1, default)).Status);
        Assert.Equal(404, console.State(null, "q/s:off").Status);
        Assert.Equal(403, (await console.Act(null, null, "ps", "k0", 1, default)).Status);
        Assert.Equal(403, (await console.Act(null, "p/nope", "ps", "k0", 1, default)).Status);
        Assert.Single(_sent);
    }

    [Fact]
    public async Task TurningTheSwitchOffMidMacroStopsTheRest()
    {
        var console = Make();
        console.SendCombo = combo =>
        {
            _sent.Add(string.Join("+", combo));
            // the user turns "Send keys" off right after the first combo went out
            File.WriteAllText(_path, File.ReadAllText(_path).Replace("\"control\": true", "\"control\": false"));
            File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddMinutes(1));
            return true;
        };
        Assert.Equal(403, (await console.Act(null, "p/c", "ps", "k1", 1, default)).Status);
        Assert.Equal(["ctrl+s"], _sent);
    }

    [Theory]
    [InlineData("rename", 200, 2)]
    [InlineData("binding", 409, 1)]
    [InlineData("remove", 409, 1)]
    [InlineData("app", 409, 1)]
    public async Task AnInProgressMacroUsesTheCurrentProfileContract(string edit, int status, int count)
    {
        var console = Make();
        File.SetLastWriteTimeUtc(_path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        console.SendCombo = combo =>
        {
            _sent.Add(string.Join("+", combo));
            if (_sent.Count == 1)
            {
                var before = File.ReadAllText(_path);
                var after = edit switch
                {
                    "rename" => before.Replace("\"label\": \"Photoshop\"", "\"label\": \"Editor\""),
                    "binding" => before.Replace("ctrl+s, ctrl+shift+s", "F5"),
                    "remove" => before.Replace(Profiles, "[]"),
                    "app" => before.Replace("photoshop.exe, gimp", "notepad"),
                    _ => throw new ArgumentOutOfRangeException(nameof(edit)),
                };
                var store = new ConfigStore(_path);
                Assert.Null(store.Save(after));
            }
            return true;
        };
        var actual = await console.Act(null, "p/c", "ps", "k1", 1, default);
        Assert.True(actual.Status == status && _sent.Count == count,
            $"EXPECTED: status={status}, sent={count} | ACTUAL: status={actual.Status}, sent={_sent.Count}");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("focused")]
    [InlineData("foreground")]
    public async Task CancellationDuringFocusChecksDoesNotSendACombo(string cancelAt)
    {
        var console = Make();
        using var cts = new CancellationTokenSource();
        console.Focused = () =>
        {
            if (cancelAt == "focused") cts.Cancel();
            return new FocusedApp("photoshop", false, 42);
        };
        console.ForegroundWindow = () =>
        {
            if (cancelAt == "foreground") cts.Cancel();
            return 42;
        };
        var error = await Record.ExceptionAsync(() => console.Act(null, "p/c", "ps", "k0", 1, cts.Token));
        if (cancelAt == "none")
        {
            Assert.Null(error);
            Assert.Equal(["ctrl+z"], _sent);
            return;
        }
        Assert.True(error is OperationCanceledException && _sent.Count == 0,
            $"EXPECTED: cancelled=true, sent=0 | ACTUAL: cancelled={error is OperationCanceledException}, sent={_sent.Count}");
        console.Focused = () => _focused;
        Assert.Equal(200, (await console.Act(null, "p/c", "ps", "k0", 1, default)).Status);
        Assert.Equal(["ctrl+z"], _sent);
    }

    [Fact]
    public async Task AWindowChangeRightBeforeSendingStopsIt()
    {
        var console = Make();
        console.Focused = () => new FocusedApp("photoshop", false, 42);
        console.ForegroundWindow = () => 43; // another window came to the front after the checks
        Assert.Equal(409, (await console.Act(null, "p/c", "ps", "k0", 1, default)).Status);
        console.ForegroundWindow = () => 42;
        Assert.Equal(200, (await console.Act(null, "p/c", "ps", "k0", 1, default)).Status);
        Assert.Single(_sent);
    }

    [Fact]
    public async Task AnotherFocusedAppGetsNothing()
    {
        _focused = new("notepad", false);
        Assert.Equal(409, (await Make().Act(null, "p/c", "ps", "k0", 1, default)).Status);
        _focused = null;
        Assert.Equal(409, (await Make().Act(null, "p/c", "ps", "k0", 1, default)).Status);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task AFocusChangeMidMacroStopsTheRest()
    {
        var console = Make();
        var calls = 0;
        console.Focused = () => ++calls == 1 ? new FocusedApp("photoshop", false) : new FocusedApp("explorer", false);
        Assert.Equal(409, (await console.Act(null, "p/c", "ps", "k1", 1, default)).Status);
        Assert.Equal(["ctrl+s"], _sent);
    }

    [Fact]
    public async Task AnElevatedAppIsRefusedWithAReason()
    {
        _focused = new("Photoshop", true);
        var (status, error) = await Make().Act(null, "p/c", "ps", "k0", 1, default);
        Assert.Equal(409, status);
        Assert.Contains("administrator", error);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task UnknownProfilesAndSlotsNeverFallBack()
    {
        var console = Make();
        Assert.Equal(404, (await console.Act(null, "p/c", "other", "k0", 1, default)).Status);
        Assert.Equal(404, (await console.Act(null, "p/c", "ps", "k7", 1, default)).Status);
        Assert.Equal(404, (await console.Act(null, "p/c", "ps", "k01", 1, default)).Status);
        Assert.Equal(404, (await console.Act(null, "p/c", "ps", "dial-press", 1, default)).Status); // not set
        Assert.Equal(404, (await console.Act(null, "p/c", "ps", "ctrl+z", 1, default)).Status);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task DialTurnsAreClamped()
    {
        var console = Make();
        await console.Act(null, "p/c", "ps", "dial-right", 50, default);
        Assert.Equal(5, _sent.Count);
        _sent.Clear();
        await console.Act(null, "p/c", "ps", "k0", 9, default); // a key is pressed once whatever the phone asks
        Assert.Single(_sent);
    }

    [Fact]
    public async Task AnOverlongBindingFromAnOlderFileIsNotSent()
    {
        var console = Make();
        // written by hand (or before the limit): Normalize keeps it, Act refuses it before any key goes out
        File.WriteAllText(_path, File.ReadAllText(_path).Replace("\"dialLeft\": \"minus\"", $"\"dialLeft\": \"{string.Join(",", Enumerable.Repeat("a", 21))}\""));
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(400, (await console.Act(null, "p/c", "ps", "dial-left", 1, default)).Status);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task AFailedKeyPressIsReported()
    {
        var console = Make();
        console.SendCombo = _ => false;
        Assert.Equal(500, (await console.Act(null, "p/c", "ps", "k0", 1, default)).Status);
    }

    [Fact]
    public async Task UnsupportedSystemsSaySo()
    {
        var console = Make();
        console.Supported = false;
        Assert.False(Json(console.State(null, "p/c").Body).GetProperty("supported").GetBoolean());
        Assert.Equal(409, (await console.Act(null, "p/c", "ps", "k0", 1, default)).Status);
    }

    [Fact]
    public void SavingChecksTheKeysAndNeedsAnApp()
    {
        File.WriteAllText(_path, "{}");
        var store = new ConfigStore(_path);
        Assert.Contains("unknown key", store.Save("""{ "console": { "profiles": [{ "label": "X", "apps": "x", "keys": [{ "keys": "ctrl+nope" }] }] } }"""));
        Assert.Contains("no app", store.Save("""{ "console": { "profiles": [{ "label": "X", "apps": " , ", "keys": [{ "keys": "f5" }] }] } }"""));
        // too long is refused, never cut short into another valid key or app name
        Assert.Contains("longer than", store.Save($$"""{ "console": { "profiles": [{ "label": "X", "apps": "x", "keys": [{ "keys": "{{string.Join(", ", Enumerable.Repeat("ctrl+shift+f12", 20))}}" }] }] } }"""));
        Assert.Contains("longer than", store.Save($$"""{ "console": { "profiles": [{ "label": "X", "apps": "{{new string('a', 301)}}", "keys": [{ "keys": "f5" }] }] } }"""));
        Assert.Contains("more than 12 keys", store.Save($$"""{ "console": { "profiles": [{ "label": "X", "apps": "x", "keys": [{{string.Join(",", Enumerable.Repeat("""{ "keys": "f5" }""", 13))}}] }] } }"""));
        Assert.Contains("more than 20 key presses", store.Save($$"""{ "console": { "profiles": [{ "label": "X", "apps": "x", "dialLeft": "{{string.Join(",", Enumerable.Repeat("a", 21))}}" }] } }"""));
        Assert.Contains("more than 30 apps", store.Save($$"""{ "console": { "profiles": [{{string.Join(",", Enumerable.Range(0, 31).Select(i => $$"""{ "label": "A{{i}}", "apps": "a{{i}}" }"""))}}] } }"""));
        Assert.Null(store.Save("""{ "console": { "profiles": [{ "label": "Edit Pro", "apps": "edit", "keys": [{ "keys": "f5" }], "dialLeft": " " }, { "label": "Edit Pro", "apps": "e2" }] } }"""));
        var saved = store.Get().Console.Profiles;
        Assert.Equal(["edit-pro", "edit-pro-2"], saved.Select(p => p.Id));
        Assert.Equal("f5", saved[0].Keys[0].Label); // an empty label shows the keys
        Assert.Null(saved[0].DialLeft);
    }

    [Fact]
    public void TheKeysNeverReachThePhoneConfig()
    {
        Make();
        var json = JsonSerializer.Serialize(new ConfigStore(_path).Get().ToPublic());
        Assert.DoesNotContain("ctrl+z", json);
        Assert.DoesNotContain("photoshop", json, StringComparison.OrdinalIgnoreCase);
    }
}
