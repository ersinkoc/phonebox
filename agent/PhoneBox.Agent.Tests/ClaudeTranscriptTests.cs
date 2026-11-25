using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public sealed class ClaudeTranscriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-transcript-" + Guid.NewGuid());
    private const string Ts = "2026-11-19T08:00:00.000Z";

    private static string User(object content, bool sidechain = false, bool meta = false) =>
        JsonSerializer.Serialize(new { type = "user", isSidechain = sidechain, isMeta = meta, timestamp = Ts, message = new { role = "user", content } });
    private static string Assistant(params object[] blocks) =>
        JsonSerializer.Serialize(new { type = "assistant", isSidechain = false, timestamp = Ts, message = new { role = "assistant", content = blocks } });
    private static object Text(string t) => new { type = "text", text = t };
    private static object ToolUse(string id, string name, object input) => new { type = "tool_use", id, name, input };
    private static object ToolResult(string id) => new { type = "tool_result", tool_use_id = id, content = "OUTPUT" };

    [Fact]
    public void MessagesComeInOrder_ToolResultsAndRemindersAreLeftOut_AndRepliesAreMerged()
    {
        var view = ClaudeTranscript.Parse([
            User("first question\n<system-reminder>SECRET REMINDER</system-reminder>"),
            Assistant(Text("part one")),
            Assistant(ToolUse("t1", "Read", new { file_path = "/a/b/file.cs" })),
            User(new object[] { ToolResult("t1") }),
            Assistant(Text("part two")),
            User(new object[] { Text("second question") }),
            User("<local-command-stdout>noise</local-command-stdout>"),
            User("hidden by Claude Code", meta: true),
            User("a sub-agent's own chat", sidechain: true),
        ]);
        Assert.Collection(view.Messages,
            m => { Assert.Equal("user", m.Role); Assert.Equal("first question", m.Text); },
            m => { Assert.Equal("assistant", m.Role); Assert.Equal("part one\n\npart two", m.Text); },
            m => { Assert.Equal("user", m.Role); Assert.Equal("second question", m.Text); });
        Assert.DoesNotContain(view.Messages, m => m.Text.Contains("SECRET") || m.Text.Contains("OUTPUT") || m.Text.Contains("noise"));
        Assert.NotEqual(0, view.Messages[0].At);
    }

    [Fact]
    public void StepsShowOnlyNamesAndNeverArgumentsOrOutput()
    {
        var view = ClaudeTranscript.Parse([
            Assistant(ToolUse("a", "Bash", new { command = "git push --force https://user:PASSWORD@example.com/x.git" })),
            Assistant(ToolUse("b", "Edit", new { file_path = "/home/me/project/Secret Plan.md", old_string = "OLD", new_string = "NEW" })),
            Assistant(ToolUse("c", "WebFetch", new { url = "https://example.com/private/path?token=abc" })),
            Assistant(ToolUse("d", "Grep", new { pattern = "PASSWORD" })),
        ]);
        Assert.Equal(["git", "Secret Plan.md", "example.com", null], view.Steps.Select(s => s.Detail));
        Assert.DoesNotContain("PASSWORD", JsonSerializer.Serialize(view));
        Assert.DoesNotContain("OLD", JsonSerializer.Serialize(view));
    }

    [Theory]
    [InlineData("cd /x/y && docker compose up -d", "docker")]
    [InlineData("rtk grep -rn secret .", "grep")]
    [InlineData("FOO=1 BAR=2 dotnet test", "dotnet")]
    [InlineData("(docker ps; echo done)", "docker")]
    [InlineData("for f in *.cs; do rm $f; done", "rm")]
    [InlineData("/usr/bin/git status", "git")]
    [InlineData("cd /only/this", null)]
    [InlineData("echo 'hi there' | tr a b", "echo")]
    [InlineData("   ", null)]
    // a quoted ';' or '&' is part of an argument, never a new command
    [InlineData("export TOKEN='SECRET_VALUE; git status'", null)]
    [InlineData("export TOKEN='SECRET_VALUE; git status'; git status", "git")]
    [InlineData("echo \"a; b && c\"", "echo")]
    [InlineData("API_KEY='abc def' dotnet test", "dotnet")]
    [InlineData("echo 'unclosed; git push", null)]
    [InlineData("$(rm -rf /tmp/x)", null)]
    [InlineData("`whoami` --version", null)]
    [InlineData("\"git\" status", null)]
    [InlineData("VALUE=$((123456789)) git status", null)]
    [InlineData("VALUE=$(whoami) git status", null)]
    [InlineData("A=${SECRET_PART} git status", null)]
    [InlineData("time -fPRIVATE_TAG git status", null)]
    [InlineData("sudo -u root git status", null)]
    [InlineData("env --unset=PATH git status", null)]
    [InlineData("time git status", "git")]
    [InlineData("FOO=1 git status", "git")]
    [InlineData("rtk grep -rn x .", "grep")]
    [InlineData("rm $f", "rm")]
    [InlineData("VALUES=(PRIVATE_VALUE); git status", null)]
    [InlineData("VALUES+=(PRIVATE_VALUE a b) git status", null)]
    [InlineData("\"HELPER=prod\" PRIVATE_TOKEN", null)]
    [InlineData("'HELPER=prod' PRIVATE_TOKEN", null)]
    [InlineData("\\HELPER=prod PRIVATE_TOKEN", null)]
    [InlineData("HELPER=\"prod x\" git status", "git")]
    [InlineData("A=1 B='x y' dotnet test", "dotnet")]
    [InlineData("VALUE=\"$(printf \"x; 123456789 \" z)\" git status", null)]
    [InlineData("echo \"$(date)\"", null)]
    [InlineData("echo \"`date`\"", null)]
    [InlineData("echo \"${HOME}\"", null)]
    [InlineData("echo \"$HOME\"", "echo")]
    [InlineData("echo '$(not run); ${x}'", "echo")]
    [InlineData("echo \"\\$(literal)\"", "echo")]
    [InlineData("(git status)", "git")]
    [InlineData("ls\\ -la", null)] // an escaped space makes one word: not something to name
    public void ACommandIsShownAsTheProgramItRuns(string command, string? expected) =>
        Assert.Equal(expected, ClaudeTranscript.Program(command));

    [Fact]
    public void ACallWithoutAnAnswerIsRunning_UntilANewMessageEndsThatTurn()
    {
        var running = ClaudeTranscript.Parse([
            Assistant(ToolUse("a", "Read", new { file_path = "/x/a.cs" })), User(new object[] { ToolResult("a") }),
            Assistant(ToolUse("b", "Bash", new { command = "dotnet test" })),
        ]);
        Assert.Equal([false, true], running.Steps.Select(s => s.Running));

        var abandoned = ClaudeTranscript.Parse([
            Assistant(ToolUse("b", "Bash", new { command = "dotnet test" })),
            User("never mind"),
        ]);
        Assert.False(Assert.Single(abandoned.Steps).Running);
    }

    [Fact]
    public void OnlyTheLastMessagesAreKept_AndLongTextIsCut()
    {
        var lines = Enumerable.Range(0, 80).Select(i => User("message " + i)).Append(Assistant(Text(new string('x', 9000)))).ToList();
        var view = ClaudeTranscript.Parse(lines);
        Assert.Equal(ClaudeTranscript.MaxMessages, view.Messages.Count);
        Assert.Equal(ClaudeTranscript.MaxTextChars + 1, view.Messages[^1].Text.Length);
        Assert.Equal("message 79", view.Messages[^2].Text);
    }

    [Fact]
    public void BrokenLinesAreSkipped()
    {
        var view = ClaudeTranscript.Parse(["{\"type\":\"user\",\"message\":{\"content\":\"cut off", "not json at all \"user\"", User("fine")]);
        Assert.Equal("fine", Assert.Single(view.Messages).Text);
    }

    [Fact]
    public void FindFile_GivesNoFileForAProjectsFolderItMayNotList()
    {
        if (OperatingSystem.IsWindows()) return; // the folder is made unlistable with a Unix mode
        var projects = Directory.CreateDirectory(Path.Combine(_root, "locked", "projects")).FullName;
        File.SetUnixFileMode(projects, UnixFileMode.None);
        try { Assert.Null(ClaudeTranscript.FindFile("abcdef12-3456", claudeDir: Path.Combine(_root, "locked"))); }
        finally { File.SetUnixFileMode(projects, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void FindFile_RefusesIdsThatCouldLeaveTheProjectsFolder()
    {
        var dir = Path.Combine(_root, ".claude", "projects", "-some-folder");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "abcdef12-3456.jsonl"), User("hi"));
        File.WriteAllText(Path.Combine(_root, "outside.jsonl"), "x");
        Assert.NotNull(ClaudeTranscript.FindFile("abcdef12-3456", _root));
        Assert.Null(ClaudeTranscript.FindFile("../../outside", _root));
        Assert.Null(ClaudeTranscript.FindFile("..\\..\\outside", _root));
        Assert.Null(ClaudeTranscript.FindFile("short", _root));
        Assert.Null(ClaudeTranscript.FindFile("abcdef12-9999", _root));
    }

    private static AgentSession Running(string id, string tool = "claude") => new(id, tool, "x", "p", "idle", null, 1, 1);

    private string Chat(string id, string text)
    {
        var dir = Path.Combine(_root, ".claude", "projects", "-some-folder");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".jsonl"), User(text) + "\n");
        return id;
    }

    [Fact]
    public void TheChatCacheIsBounded_AndDropsASessionThatHasEnded_OrTheSettingBeingOff()
    {
        var dir = Path.Combine(_root, ".claude", "projects", "-some-folder");
        Directory.CreateDirectory(dir);
        string Id(int i) => $"aaaaaaaa-{i:D4}";
        // more chats than the cache holds, all read through Read
        for (var i = 0; i < ClaudeTranscript.MaxCachedFiles + 5; i++)
        {
            File.WriteAllText(Path.Combine(dir, Id(i) + ".jsonl"), User("text " + i) + "\n");
            ClaudeTranscript.Read(Path.Combine(dir, Id(i) + ".jsonl"));
        }
        var cached = Enumerable.Range(0, ClaudeTranscript.MaxCachedFiles + 5).Count(i => ClaudeTranscript.IsCached(Path.Combine(dir, Id(i) + ".jsonl")));
        Assert.True(cached <= ClaudeTranscript.MaxCachedFiles, $"{cached} chats cached");

        // one is read for the phone: the others, whose sessions are not running, are dropped
        var keep = Path.Combine(dir, Id(0) + ".jsonl");
        ClaudeTranscript.ForPhone(true, [Running("claude:" + Id(0))], "claude:" + Id(0), _root);
        Assert.True(ClaudeTranscript.IsCached(keep));
        Assert.Equal(1, Enumerable.Range(0, ClaudeTranscript.MaxCachedFiles + 5).Count(i => ClaudeTranscript.IsCached(Path.Combine(dir, Id(i) + ".jsonl"))));
        // the session ends, or the setting goes off: nothing stays in memory
        ClaudeTranscript.ForPhone(true, [], "claude:" + Id(0), _root);
        Assert.False(ClaudeTranscript.IsCached(keep));
        ClaudeTranscript.Read(keep);
        ClaudeTranscript.ForPhone(false, [Running("claude:" + Id(0))], "claude:" + Id(0), _root);
        Assert.False(ClaudeTranscript.IsCached(keep));
    }

    [Fact]
    public void TrimDropsCachedChatsWithoutAnyFurtherRequest()
    {
        var dir = Path.Combine(_root, ".claude", "projects", "-some-folder");
        Directory.CreateDirectory(dir);
        string Make(string id) { var p = Path.Combine(dir, id + ".jsonl"); File.WriteAllText(p, User("text for " + id) + "\n"); ClaudeTranscript.Read(p); return p; }
        var running = Make("bbbbbbbb-0001");
        var ended = Make("bbbbbbbb-0002");
        var idle = Make("bbbbbbbb-0003");
        Assert.True(ClaudeTranscript.IsCached(running) && ClaudeTranscript.IsCached(ended) && ClaudeTranscript.IsCached(idle));

        // the dialog was closed and one session ended: the next monitor pass, with no request from the phone, drops that one
        ClaudeTranscript.Trim(["bbbbbbbb-0001", "bbbbbbbb-0003"], true);
        Assert.True(ClaudeTranscript.IsCached(running));
        Assert.False(ClaudeTranscript.IsCached(ended));
        // nobody asked for a minute: the rest goes even though its session is still running
        ClaudeTranscript.Trim(["bbbbbbbb-0001", "bbbbbbbb-0003"], true, DateTime.UtcNow.AddSeconds(ClaudeTranscript.CacheIdleSeconds + 5));
        Assert.False(ClaudeTranscript.IsCached(running));
        Assert.False(ClaudeTranscript.IsCached(idle));
        // the setting goes off: everything goes
        var again = Make("bbbbbbbb-0001");
        ClaudeTranscript.Trim(["bbbbbbbb-0001"], false);
        Assert.False(ClaudeTranscript.IsCached(again));
    }

    [Fact]
    public void ForPhone_SendsNoTextWhileTheSettingIsOff()
    {
        var id = Chat("abcdef12-3456", "PRIVATE PROMPT");
        var json = JsonSerializer.Serialize(ClaudeTranscript.ForPhone(false, [Running("claude:" + id)], "claude:" + id, _root));
        Assert.Equal("{\"enabled\":false}", json);
    }

    [Fact]
    public void ForPhone_ReadsOnlyARunningClaudeSession()
    {
        var id = Chat("abcdef12-3456", "the question");
        string Ask(string asked, params AgentSession[] running) => JsonSerializer.Serialize(ClaudeTranscript.ForPhone(true, running, asked, _root));

        Assert.Contains("the question", Ask("claude:" + id, Running("claude:" + id)));
        // a chat file that exists but whose session is not running, an id of another tool, and ids that are not session ids
        Assert.DoesNotContain("the question", Ask("claude:" + id));
        Assert.DoesNotContain("the question", Ask("codex:" + id, Running("codex:" + id, "codex")));
        Assert.DoesNotContain("the question", Ask("claude:../../x", Running("claude:../../x")));
        Assert.Contains("\"messages\":[]", Ask("claude:" + id));
    }

    [Fact]
    public void Read_FollowsTheFileAsItGrows()
    {
        var path = Path.Combine(_root, "chat.jsonl");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, User("one") + "\n");
        Assert.Single(ClaudeTranscript.Read(path).Messages);
        File.AppendAllText(path, Assistant(Text("two")) + "\n");
        Assert.Equal(2, ClaudeTranscript.Read(path).Messages.Count);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
