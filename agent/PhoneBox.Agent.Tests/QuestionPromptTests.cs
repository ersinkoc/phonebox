using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>A question from Claude (AskUserQuestion) reaches the phone as readable text, not as a JSON dump, and
/// "Got it" leaves it to the terminal instead of answering for it.</summary>
public sealed class QuestionPromptTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void TheQuestionAndItsChoicesAreListedAsText()
    {
        var text = ClaudeHook.QuestionDetail(Json("""
            {"questions":[
              {"header":"Framework","question":"Which framework?","multiSelect":false,
               "options":[{"label":"React","description":"Component library"},{"label":"Vue","description":""}]},
              {"header":"Extras","question":"Which extras?","multiSelect":true,"options":[{"label":"Tests","description":"xUnit"}]}]}
            """));
        Assert.Equal("Framework: Which framework?\n  • React — Component library\n  • Vue\n\nExtras: Which extras?  (more than one can be picked)\n  • Tests — xUnit", text);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"questions":[]}""")]
    [InlineData("""{"questions":"nope"}""")]
    public void WithoutQuestionsItStillTellsYouToLookAtTheTerminal(string input) =>
        Assert.Contains("terminal", ClaudeHook.QuestionDetail(Json(input)));

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public void InvalidQuestionEntriesDoNotHideTheirValidNeighbors(string entry)
    {
        Assert.Contains("terminal", ClaudeHook.QuestionDetail(Json("{\"questions\":[" + entry + "]}")));
        Assert.Equal("Continue?", ClaudeHook.QuestionDetail(Json("{\"questions\":[" + entry + ",{\"question\":\"Continue?\"}," + entry + "]}")));
    }

    [Fact]
    public async Task GotItEndsTheRequestWithoutAnAnswerSoTheTerminalKeepsTheQuestion()
    {
        var store = new ApprovalStore(new PushStore(NullLogger<PushStore>.Instance, Path.Combine(Path.GetTempPath(), "phonebox-q-" + Guid.NewGuid().ToString("N") + ".json")), NullLogger<ApprovalStore>.Instance);
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var a = new Approval("q1", "Claude Code", "s", "p", "Claude has a question", "text", null, now, now + 30_000, "question");
        var asking = store.AskAsync(a, CancellationToken.None);
        Assert.Equal("question", Assert.Single(store.Snapshot()).Kind);
        Assert.True(store.Decide("q1", "skip"));
        Assert.Equal("skip", await asking);
        Assert.False(store.Decide("q1", "skip")); // already finished
        Assert.False(store.Decide("q2", "maybe"));
    }

    [Fact]
    public async Task NonObjectPayloadsEditWithoutFilePathMalformedOptionsAndSettingsHookConfigureBehaveSafely()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-ch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        PushStore? push = null;
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """{"claude":{"phoneApprovals":true,"onlyWhenAway":false,"timeoutSec":30}}""");
            var cfg = new ConfigStore(cfgPath);
            push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(dir, "push.json"));
            var approvals = new ApprovalStore(push, NullLogger<ApprovalStore>.Instance);
            var sessions = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
            Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            Assert.Null(await ClaudeHook.HandleAsync(Json("[]"), cfg, approvals, sessions, CancellationToken.None));
            Assert.Null(await ClaudeHook.HandleAsync(Json("\"ping\""), cfg, approvals, sessions, CancellationToken.None));

            var editTask = ClaudeHook.HandleAsync(
                Json("""{"tool_name":"Edit","tool_input":{"path":"src/App.cs","old_string":"oldVal","new_string":"newVal"}}"""),
                cfg, approvals, sessions, CancellationToken.None);
            var approval = Assert.Single(approvals.Snapshot());
            Assert.Contains("oldVal", approval.Detail);
            Assert.Contains("newVal", approval.Detail);
            Assert.False(approval.Detail.StartsWith("\n\n"));
            approvals.Decide(approval.Id, "skip");
            await editTask;

            var qText = ClaudeHook.QuestionDetail(Json("""
                {"questions":[{"header":"Pick","question":"Which?","options":[null, 42, {}, {"label":""}, {"label":"A","description":"ok"}]}]}
                """));
            Assert.Equal("Pick: Which?\n  • A — ok", qText);

            var settingsPath = Path.Combine(dir, "settings.json");
            File.WriteAllText(settingsPath, "  ");
            Assert.Null(ClaudeHook.Configure(true, 18765, 30, settingsPath));
            Assert.True(ClaudeHook.Installed(settingsPath));

            File.WriteAllText(settingsPath, """
                {
                  "theme": "dark",
                  "hooks": {
                    "PermissionRequest": [
                      "bad-entry",
                      { "matcher": "Bash", "hooks": [ ["not-obj"], { "command": 123 }, { "type": "command", "command": "echo user-hook" } ] }
                    ]
                  }
                }
                """);
            Assert.False(ClaudeHook.Installed(settingsPath));
            Assert.Null(ClaudeHook.Configure(true, 18765, 30, settingsPath));
            Assert.True(ClaudeHook.Installed(settingsPath));
            Assert.Null(ClaudeHook.Configure(false, 18765, 30, settingsPath));
            Assert.False(ClaudeHook.Installed(settingsPath));
            Assert.Contains("echo user-hook", File.ReadAllText(settingsPath));
        }
        finally
        {
            push?.Dispose();
            Directory.Delete(dir, true);
        }
    }
}
