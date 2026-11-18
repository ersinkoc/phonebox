using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Dictation types the phone's text into the app in front, only for a widget that allows it, and never as commands.</summary>
public sealed class DictationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-dictate-{Guid.NewGuid():N}.json");
    private readonly List<string> _typed = [];
    private readonly List<string> _keys = [];
    private FocusedApp? _focused = new("notepad", false);

    public void Dispose() { try { File.Delete(_path); } catch { } }

    private Dictation Make(bool control = true, bool enter = false)
    {
        File.WriteAllText(_path, $$"""
            { "pages": [ { "id": "p", "title": "P", "widgets": [
                { "id": "d", "type": "dictate", "x": 0, "y": 0, "w": 4, "h": 3, "options": { "control": {{(control ? "true" : "false")}}, "enter": {{(enter ? "true" : "false")}} } },
                { "id": "off", "type": "dictate", "x": 4, "y": 0, "w": 4, "h": 3, "options": { "control": false } } ] } ] }
            """);
        return new Dictation(new ConfigStore(_path), NullLogger<Dictation>.Instance)
        {
            TypeText = (text, _) => { _typed.Add(text); return true; },
            SendCombo = combo => { _keys.Add(string.Join("+", combo)); return true; },
            Focused = () => _focused,
            CheckElevation = true,
            AgentElevated = false,
        };
    }

    [Fact]
    public async Task TheTextIsTypedWithASpaceAfterIt()
    {
        Assert.Equal((200, (string?)null), await Make().TypeAsync(null, "p/d", "hello world", default));
        Assert.Equal(["hello world "], _typed);
        Assert.Empty(_keys);
    }

    [Fact]
    public async Task LineBreaksNeverBecomeEnter()
    {
        // a newline would be Enter: it sends a chat message or runs a command
        await Make().TypeAsync(null, "p/d", "rm -rf x\nreally\r\n\tnow\u2028done", default);
        Assert.Equal(["rm -rf x really now done "], _typed);
        Assert.Empty(_keys);
    }

    [Fact]
    public async Task EnterOnlyWithTheWidgetsOption()
    {
        await Make(enter: true).TypeAsync(null, "p/d", "ship it", default);
        Assert.Equal(["ship it"], _typed);
        Assert.Equal(["enter"], _keys);
    }

    [Fact]
    public async Task NothingIsTypedWithoutTheWidgetsOwnSwitch()
    {
        var d = Make();
        Assert.Equal(403, (await d.TypeAsync(null, "p/off", "x", default)).Status);
        Assert.Equal(403, (await d.TypeAsync(null, null, "x", default)).Status);
        Assert.Equal(403, (await d.TypeAsync(null, "p/missing", "x", default)).Status);
        Assert.Equal(403, (await Make(control: false).TypeAsync(null, "p/d", "x", default)).Status);
        Assert.Empty(_typed);
    }

    [Fact]
    public async Task EmptyTextAndElevatedAppsAreRefused()
    {
        var d = Make();
        Assert.Equal(400, (await d.TypeAsync(null, "p/d", " \n\t ", default)).Status);
        _focused = new("regedit", true);
        var (status, error) = await d.TypeAsync(null, "p/d", "x", default);
        Assert.Equal(409, status);
        Assert.Contains("administrator", error);
        Assert.Empty(_typed);
    }

    [Fact]
    public async Task ACancelledRequestNeverPressesEnter()
    {
        using var cts = new CancellationTokenSource();
        var d = Make(enter: true);
        d.TypeText = (text, _) => { _typed.Add(text); cts.Cancel(); return true; }; // the phone gave up while typing
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => d.TypeAsync(null, "p/d", "rm -rf build", cts.Token));
        Assert.Empty(_keys);
    }

    [Fact]
    public async Task TheSwitchIsReadAgainAfterWaitingForTheTextBefore()
    {
        var d = Make();
        var first = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        d.TypeText = (text, _) => { _typed.Add(text); first.TrySetResult(); release.Task.Wait(); return true; };
        var running = Task.Run(() => d.TypeAsync(null, "p/d", "one", default));
        await first.Task;
        var waiting = d.TypeAsync(null, "p/d", "two", default);
        // turned off while the second text waits for the first
        File.WriteAllText(_path, File.ReadAllText(_path).Replace("\"control\": true", "\"control\": false"));
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddMinutes(1));
        release.SetResult();
        await running;
        Assert.Equal(403, (await waiting).Status);
        Assert.Equal(["one "], _typed);
    }

    [Fact]
    public async Task LongTextIsCut()
    {
        await Make().TypeAsync(null, "p/d", new string('a', Dictation.MaxChars + 500), default);
        Assert.Equal(Dictation.MaxChars + 1, _typed.Single().Length); // plus the trailing space
        Assert.Equal(413, (await Make().TypeAsync(null, "p/d", new string('a', Dictation.MaxChars * 3), default)).Status);
    }

    [Fact]
    public async Task ARefusedTypingIsReported()
    {
        var d = Make();
        d.TypeText = (_, _) => false;
        Assert.Equal(500, (await d.TypeAsync(null, "p/d", "x", default)).Status);
    }
}
