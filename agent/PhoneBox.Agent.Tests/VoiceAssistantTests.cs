using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>VoiceAssistant executes natural spoken commands and macros only when the dictate widget's assistant option is on.</summary>
public sealed class VoiceAssistantTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-voice-{Guid.NewGuid():N}.json");
    private readonly List<(string File, string? Args)> _launched = [];

    public void Dispose() { try { File.Delete(_path); } catch { } }

    private VoiceAssistant Make(bool assistant = true, bool control = true, bool confirm = false)
    {
        File.WriteAllText(_path, $$"""
            {
              "pages": [
                {
                  "id": "p",
                  "title": "P",
                  "widgets": [
                    { "id": "d", "type": "dictate", "x": 0, "y": 0, "w": 2, "h": 2, "options": { "control": {{(control ? "true" : "false")}}, "assistant": {{(assistant ? "true" : "false")}}, "confirm": {{(confirm ? "true" : "false")}} } }
                  ]
                }
              ],
              "shortcuts": [
                { "id": "chrome", "label": "Chrome", "cmd": "chrome.exe" },
                { "id": "taskmgr", "label": "Task Manager", "cmd": "taskmgr.exe" },
                { "id": "close-chrome", "label": "Close Chrome", "cmd": "taskkill.exe", "args": "/IM chrome.exe /F /T" }
              ],
              "voiceMacros": [
                { "phrase": "work mode", "shortcutId": "chrome" }
              ]
            }
            """);
        var cfg = new ConfigStore(_path);
        var procs = new ProcessMonitor(NullLogger<ProcessMonitor>.Instance, new SessionMonitor(NullLogger<SessionMonitor>.Instance));
        var push = new PushStore(NullLogger<PushStore>.Instance);
        var audio = new AudioControl(() => new AudioSnapshot(50, false, true));
        return new VoiceAssistant(cfg, procs, push, audio, NullLogger<VoiceAssistant>.Instance)
        {
            Launch = (file, args) => { _launched.Add((file, args)); return null; },
        };
    }

    [Fact]
    public async Task OffByDefaultDoesNotHandleCommands()
    {
        var (handled, _, _, _) = await Make(assistant: false).TryExecuteAsync(null, "p/d", "open chrome and search elon musk", default);
        Assert.False(handled);
        Assert.Empty(_launched);
    }

    [Fact]
    public async Task OpenChromeAndSearchLaunchesGoogleQueryInChrome()
    {
        var (handled, status, msg, confirmReq) = await Make().TryExecuteAsync(null, "p/d", "open chrome and search elon musk", default);
        Assert.True(handled);
        Assert.False(confirmReq);
        Assert.Equal(200, status);
        Assert.Equal("Searching elon musk", msg);
        var call = Assert.Single(_launched);
        Assert.Equal("chrome.exe", call.File);
        Assert.Equal("\"https://www.google.com/search?q=elon%20musk\"", call.Args);
    }

    [Fact]
    public async Task GreetingPrefixIsStrippedBeforeOpeningShortcut()
    {
        var (handled, status, msg, _) = await Make().TryExecuteAsync(null, "p/d", "hey bro open task manager", default);
        Assert.True(handled);
        Assert.Equal(200, status);
        Assert.Equal("Opened Task Manager", msg);
        var call = Assert.Single(_launched);
        Assert.Equal("taskmgr.exe", call.File);
    }

    [Fact]
    public async Task YoutubeSearchBuildsYoutubeUrl()
    {
        var (handled, status, _, _) = await Make().TryExecuteAsync(null, "p/d", "youtube lofi beats", default);
        Assert.True(handled);
        Assert.Equal(200, status);
        var call = Assert.Single(_launched);
        Assert.Contains("youtube.com/results?search_query=lofi%20beats", call.Args);
    }

    [Fact]
    public async Task CloseCommandRequestsConfirmationWhenGuardIsOn()
    {
        var va = Make(confirm: true);
        var (handled, status, msg, confirmReq) = await va.TryExecuteAsync(null, "p/d", "close chrome", default, confirmed: false);
        Assert.True(handled);
        Assert.True(confirmReq);
        Assert.Equal(200, status);
        Assert.Equal("Close chrome", msg);
        Assert.Empty(_launched);

        var (handled2, status2, _, confirmReq2) = await va.TryExecuteAsync(null, "p/d", "close chrome", default, confirmed: true);
        Assert.True(handled2);
        Assert.False(confirmReq2);
        Assert.Equal(200, status2);
        var call = Assert.Single(_launched);
        Assert.Equal("taskkill.exe", call.File);
    }

    [Fact]
    public async Task CustomVoiceMacroRunsConfiguredShortcut()
    {
        var (handled, status, msg, _) = await Make().TryExecuteAsync(null, "p/d", "hey work mode", default);
        Assert.True(handled);
        Assert.Equal(200, status);
        Assert.Equal("Ran work mode", msg);
    }

    [Fact]
    public async Task NormalSpeechFallsBackToDictation()
    {
        var (handled, _, _, _) = await Make().TryExecuteAsync(null, "p/d", "hello team please review this", default);
        Assert.False(handled);
        Assert.Empty(_launched);
    }
}
