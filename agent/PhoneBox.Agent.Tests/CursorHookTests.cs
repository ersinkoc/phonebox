using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

/// <summary>Cursor's beforeShellExecution / beforeMCPExecution hooks, with inputs shaped like the ones Cursor 3.30 sends.</summary>
[Collection("Approval request expiry")] // shares the static PhoneLastSeen with the other approval tests
public sealed class CursorHookTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-cursor-hook-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    // recorded from Cursor 3.30.14 over Remote - WSL: note the empty cwd
    private const string Shell = """
        {"conversation_id":"35762ab5-5f14-4aa8-b5f7-b97fad7c592e","generation_id":"fd675877","model":"default",
         "command":"npm test","cwd":"","sandbox":true,"tool_use_id":"d60783ae","session_id":"35762ab5-5f14-4aa8-b5f7-b97fad7c592e",
         "hook_event_name":"beforeShellExecution","cursor_version":"3.30.14","workspace_roots":["/home/dev/eshop"]}
        """;

    private ConfigStore Config(string json)
    {
        var path = Path.Combine(_dir, $"config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return new ConfigStore(path);
    }

    [Fact]
    public void AShellCommandBecomesABashRequestInTheWorkspaceFolder()
    {
        var r = CursorHook.ToPermissionRequest(Json(Shell))!.Value;
        Assert.Equal("Bash", r.GetProperty("tool_name").GetString());
        Assert.Equal("npm test", r.GetProperty("tool_input").GetProperty("command").GetString());
        Assert.Equal("/home/dev/eshop", r.GetProperty("cwd").GetString());
        Assert.Equal("35762ab5-5f14-4aa8-b5f7-b97fad7c592e", r.GetProperty("session_id").GetString());
    }

    [Fact]
    public void AnMcpCallBecomesAnMcpToolWithItsArguments()
    {
        var r = CursorHook.ToPermissionRequest(Json("""
            {"hook_event_name":"beforeMCPExecution","conversation_id":"c1","cwd":"/w/app","mcp_server_name":"github",
             "tool_name":"create_issue","tool_input":"{\"title\":\"Bug\",\"body\":\"x\"}"}
            """))!.Value;
        Assert.Equal("mcp__github__create_issue", r.GetProperty("tool_name").GetString());
        Assert.Equal("Bug", r.GetProperty("tool_input").GetProperty("title").GetString());
        Assert.Equal("/w/app", r.GetProperty("cwd").GetString());
    }

    [Fact]
    public void AnMcpInputThatIsNotJsonIsShownAsItCame()
    {
        var r = CursorHook.ToPermissionRequest(Json("""
            {"hook_event_name":"beforeMCPExecution","mcp_server_name":"s","tool_name":"t","tool_input":"plain words"}
            """))!.Value;
        Assert.Equal("plain words", r.GetProperty("tool_input").GetProperty("input").GetString());
    }

    [Theory]
    [InlineData("""{"hook_event_name":"beforeReadFile","file_path":"/a"}""")]
    [InlineData("""{"hook_event_name":"beforeShellExecution","command":""}""")]
    [InlineData("""[1,2]""")]
    public void OtherEventsAndEmptyCommandsAreNotAsked(string input) => Assert.Null(CursorHook.ToPermissionRequest(Json(input)));

    [Fact]
    public void DecisionsUseCursorsPermissionField()
    {
        Assert.Equal("""{"permission":"allow"}""", JsonSerializer.Serialize(CursorHook.Output("allow")));
        Assert.Equal("""{"permission":"allow"}""", JsonSerializer.Serialize(CursorHook.Output("always")));
        Assert.Contains("\"permission\":\"deny\"", JsonSerializer.Serialize(CursorHook.Output("deny")));
        // no answer: nothing printed, and Cursor goes on as it would without the hook
        Assert.Null(CursorHook.Output(null));
    }

    [Fact]
    public async Task TheCommandIsShownAsCursorAndThePhoneDecides()
    {
        var cfg = Config("""{"claude":{"phoneApprovals":true,"timeoutSec":30},"cursor":{"phoneApprovals":true}}""");
        using var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "push.json"));
        var approvals = new ApprovalStore(push, NullLogger<ApprovalStore>.Instance);
        var sessions = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var pending = CursorHook.HandleAsync(Json(Shell), cfg, approvals, sessions, CancellationToken.None);
        var approval = Assert.Single(approvals.Snapshot(), a => a.Source == "Cursor");
        Assert.Equal("eshop", approval.Project);
        Assert.Equal("Run a command", approval.Tool);
        Assert.Equal("npm test", approval.Detail);
        Assert.True(approvals.Decide(approval.Id, "deny"));
        Assert.Contains("\"permission\":\"deny\"", JsonSerializer.Serialize(await pending));
    }

    [Fact]
    public async Task CursorPromptsDoNotDependOnClaudeCodesSwitch()
    {
        // Claude Code's Ask on the phone off (and Only when away on): Cursor's own switch alone decides
        var cfg = Config("""{"claude":{"phoneApprovals":false,"onlyWhenAway":true,"timeoutSec":30},"cursor":{"phoneApprovals":true}}""");
        using var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "push.json"));
        var approvals = new ApprovalStore(push, NullLogger<ApprovalStore>.Instance);
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var pending = CursorHook.HandleAsync(Json(Shell), cfg, approvals, new SessionMonitor(NullLogger<SessionMonitor>.Instance), CancellationToken.None);
        var approval = Assert.Single(approvals.Snapshot(), a => a.Source == "Cursor");
        Assert.True(approvals.Decide(approval.Id, "allow"));
        Assert.Contains("\"permission\":\"allow\"", JsonSerializer.Serialize(await pending));
    }

    [Fact]
    public void EveryTargetIsTriedAfterOneFails()
    {
        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, """{"version":1,"hooks":"not an object"}""");
        var good = Path.Combine(_dir, "good.json");
        IReadOnlyList<(string, bool)> targets = [(bad, false), (good, false)];

        Assert.Contains("bad.json", CursorHook.ConfigureAll(true, 18765, 30, targets));
        Assert.True(CursorHook.Installed(good)); // installed although the first one failed
        Assert.Contains("bad.json", CursorHook.ConfigureAll(false, 18765, 30, targets));
        Assert.False(CursorHook.Installed(good)); // and removed again
    }

    // the WSL command #470 wrote before its review added `|| true`: a hook from an earlier version
    private const string OldWslCommand =
        "curl -s --noproxy 127.0.0.1 --max-time 3600 -X POST http://127.0.0.1:8765/api/hooks/cursor/permission -H \"Content-Type: application/json\" --data-binary \"@-\"";

    [Theory]
    [InlineData(OldWslCommand)]
    [InlineData("curl.exe -s -X POST http://127.0.0.1:18765/api/hooks/cursor/permission --data-binary @-")]
    [InlineData("pwsh -c \"irm -Method Post http://127.0.0.1:1/api/hooks/cursor/permission -Body $input\"")]
    public void AHookIsOursByItsEndpointWhateverTheCommandAround(string command)
    {
        Assert.True(CursorHook.IsOurs(new System.Text.Json.Nodes.JsonObject { ["command"] = command }));

        // reinstalling replaces it instead of adding a second hook (two cards per command), and Remove takes it off
        var path = Path.Combine(_dir, $"old-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            version = 1,
            hooks = new { beforeShellExecution = new[] { new { command } }, beforeMCPExecution = new[] { new { command } } },
        }));
        Assert.True(CursorHook.Installed(path));
        Assert.Null(CursorHook.Configure(true, 18765, 30, path, false));
        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            foreach (var ev in CursorHook.Events)
            {
                var list = doc.RootElement.GetProperty("hooks").GetProperty(ev);
                Assert.Equal(1, list.GetArrayLength());
                Assert.Equal(CursorHook.HookCommand(18765, windows: false), list[0].GetProperty("command").GetString());
            }
        Assert.Null(CursorHook.Configure(false, 18765, 30, path, false));
        Assert.False(CursorHook.HasAny(path));
    }

    [Theory]
    [InlineData("curl -s http://127.0.0.1:18765/api/hooks/cursor/permission-audit -d @-")] // the path must end there
    [InlineData("curl -s http://127.0.0.1:18765/api/hooks/cursor/permissions -d @-")]
    [InlineData("curl -s http://192.168.1.20:18765/api/hooks/cursor/permission -d @-")] // not this computer's agent
    [InlineData("curl -s http://127.0.0.1:18765/api/hooks/claude/permission -d @-")]
    [InlineData("./hooks/audit.sh /api/hooks/cursor/permission")]
    public void SomeoneElsesSimilarHookIsLeftAlone(string command)
    {
        Assert.False(CursorHook.IsOurs(new System.Text.Json.Nodes.JsonObject { ["command"] = command }));

        var path = Path.Combine(_dir, $"other-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { version = 1, hooks = new { beforeShellExecution = new[] { new { command } } } }));
        Assert.False(CursorHook.HasAny(path)); // not reported as PhoneBox's
        Assert.Null(CursorHook.Configure(true, 18765, 30, path, false));
        Assert.Null(CursorHook.Configure(false, 18765, 30, path, false));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(command, doc.RootElement.GetProperty("hooks").GetProperty("beforeShellExecution")[0].GetProperty("command").GetString());
    }

    // ---------------------------------------------------------------- WSL targets: looked at only when needed

    private sealed class FakeWsl
    {
        public int Calls;
        public bool Running = true;
        public List<WslHomes.Entry> Distros { get; } = [];
        public IReadOnlyList<WslHomes.Entry> List() { Calls++; return Running ? Distros : []; }
    }

    // a distro share under the test folder, with a ~/.cursor folder like one where Cursor's server has been used
    private (FakeWsl Wsl, string Native, string InDistro, string State) WslSetup()
    {
        var root = Path.Combine(_dir, "share", "Ubuntu");
        var home = Path.Combine(root, "home", "dev");
        Directory.CreateDirectory(Path.Combine(home, ".cursor"));
        var wsl = new FakeWsl();
        wsl.Distros.Add(new WslHomes.Entry("Ubuntu", root, home));
        return (wsl, Path.Combine(_dir, "native", "hooks.json"), Path.Combine(home, ".cursor", "hooks.json"), Path.Combine(_dir, "cursor-hook-wsl.json"));
    }

    private static List<string> StatusPaths(object status) =>
        JsonDocument.Parse(JsonSerializer.Serialize(status)).RootElement.GetProperty("paths").EnumerateArray()
            .Select(p => p.GetProperty("path").GetString()!).ToList();

    [Fact]
    public void WithWslWatchingOffAndNoHookThereWslIsNeverAsked()
    {
        var (wsl, native, inDistro, state) = WslSetup();

        Assert.Equal([native], StatusPaths(CursorHook.Status(false, native, wsl.List, state)));
        Assert.Null(CursorHook.ConfigureAll(true, 18765, 30, false, (native, false), wsl.List, state));
        Assert.Null(CursorHook.ConfigureAll(false, 18765, 30, false, (native, false), wsl.List, state));
        Assert.Equal(0, wsl.Calls); // no wsl.exe, no \\wsl.localhost read
        Assert.False(File.Exists(inDistro));
        Assert.False(File.Exists(state));
    }

    [Fact]
    public void InstallingWithWslWatchingOffWritesOnlyTheNativeHook()
    {
        var (wsl, native, inDistro, state) = WslSetup();

        Assert.Null(CursorHook.ConfigureAll(true, 18765, 30, false, (native, false), wsl.List, state));
        Assert.True(CursorHook.Installed(native));
        Assert.False(File.Exists(inDistro));

        // with it on, both
        Assert.Null(CursorHook.ConfigureAll(true, 18765, 30, true, (native, false), wsl.List, state));
        Assert.True(CursorHook.Installed(inDistro));
        Assert.Equal([inDistro], CursorHook.Remembered(state));
    }

    [Fact]
    public void AHookInstalledInWslIsShownAndRemovedAfterWslWatchingIsTurnedOff()
    {
        var (wsl, native, inDistro, state) = WslSetup();
        Assert.Null(CursorHook.ConfigureAll(true, 18765, 30, true, (native, false), wsl.List, state));
        Assert.True(CursorHook.Installed(native));
        Assert.True(CursorHook.Installed(inDistro));

        // WSL watching turned off: the page still shows the distro's hook, and Remove takes it off
        Assert.Equal([native, inDistro], StatusPaths(CursorHook.Status(false, native, wsl.List, state)));
        Assert.Null(CursorHook.ConfigureAll(false, 18765, 30, false, (native, false), wsl.List, state));
        Assert.False(CursorHook.HasAny(native));
        Assert.False(CursorHook.HasAny(inDistro));
        Assert.False(File.Exists(state)); // nothing left to remember

        // and WSL is no longer asked
        var calls = wsl.Calls;
        Assert.Equal([native], StatusPaths(CursorHook.Status(false, native, wsl.List, state)));
        Assert.Equal(calls, wsl.Calls);
    }

    [Fact]
    public void WithWslWatchingOnRemoveAlsoFindsAHookItDidNotRecord()
    {
        var (wsl, native, inDistro, state) = WslSetup();
        Assert.Null(CursorHook.Configure(true, 18765, 30, inDistro, false)); // e.g. copied there by hand

        Assert.Equal([native, inDistro], StatusPaths(CursorHook.Status(true, native, wsl.List, state)));
        Assert.Null(CursorHook.ConfigureAll(false, 18765, 30, true, (native, false), wsl.List, state));
        Assert.False(CursorHook.HasAny(inDistro));
    }

    [Fact]
    public void AStoppedDistroIsNotStartedToRemoveTheHookAndStaysRemembered()
    {
        var (wsl, native, inDistro, state) = WslSetup();
        Assert.Null(CursorHook.ConfigureAll(true, 18765, 30, true, (native, false), wsl.List, state));

        wsl.Running = false;
        Assert.Contains("not running", CursorHook.ConfigureAll(false, 18765, 30, false, (native, false), wsl.List, state));
        Assert.False(CursorHook.HasAny(native)); // the rest is still removed
        Assert.True(CursorHook.Installed(inDistro));
        Assert.Equal([inDistro], CursorHook.Remembered(state));

        // the page still offers Remove: the recorded hook counts as installed, marked as in a distro that is not running
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(CursorHook.Status(false, native, wsl.List, state))))
        {
            Assert.True(doc.RootElement.GetProperty("installed").GetBoolean());
            var stopped = doc.RootElement.GetProperty("paths").EnumerateArray().Single(p => p.GetProperty("path").GetString() == inDistro);
            Assert.True(stopped.GetProperty("installed").GetBoolean());
            Assert.False(stopped.GetProperty("running").GetBoolean());
        }

        // once the distro runs again, the page shows the hook and Remove takes it off
        wsl.Running = true;
        Assert.Equal([native, inDistro], StatusPaths(CursorHook.Status(false, native, wsl.List, state)));
        Assert.Null(CursorHook.ConfigureAll(false, 18765, 30, false, (native, false), wsl.List, state));
        Assert.False(CursorHook.HasAny(inDistro));
        Assert.Empty(CursorHook.Remembered(state));
    }

    [Fact]
    public void AShareThatDoesNotAnswerIsNotTakenForARemovedHook()
    {
        var (wsl, native, inDistro, state) = WslSetup();
        Assert.Null(CursorHook.ConfigureAll(true, 18765, 30, true, (native, false), wsl.List, state));
        var hidden = Path.Combine(_dir, "share-away");
        Directory.Move(Path.Combine(_dir, "share"), hidden); // listed as running, but its share cannot be read

        Assert.Contains("cannot be reached", CursorHook.ConfigureAll(false, 18765, 30, false, (native, false), wsl.List, state));
        Assert.Equal([inDistro], CursorHook.Remembered(state)); // still recorded

        Directory.Move(hidden, Path.Combine(_dir, "share"));
        Assert.Null(CursorHook.ConfigureAll(false, 18765, 30, false, (native, false), wsl.List, state));
        Assert.False(CursorHook.HasAny(inDistro));
        Assert.Empty(CursorHook.Remembered(state));
    }

    [Fact]
    public void ADistroIsRecordedBeforeItsHookIsWritten()
    {
        var (wsl, native, inDistro, state) = WslSetup();
        Directory.CreateDirectory(state); // the record cannot be written

        Assert.Contains(state, CursorHook.ConfigureAll(true, 18765, 30, true, (native, false), wsl.List, state));
        Assert.True(CursorHook.Installed(native));
        Assert.False(File.Exists(inDistro)); // no hook that Remove would not know about

        // a distro whose write fails stays recorded, and Remove tries it again
        Directory.Delete(state);
        File.WriteAllText(inDistro, "[1]");
        Assert.NotNull(CursorHook.ConfigureAll(true, 18765, 30, true, (native, false), wsl.List, state));
        Assert.Equal([inDistro], CursorHook.Remembered(state));
    }

    [Fact]
    public void InstallAndRemoveRunOneAtATime()
    {
        var (wsl, native, inDistro, state) = WslSetup();
        Parallel.For(0, 40, i => CursorHook.ConfigureAll(i % 2 == 0, 18765, 30, true, (native, false), wsl.List, state));
        // whatever ran last, the record and the distro's file agree
        Assert.Equal(CursorHook.HasAny(inDistro), CursorHook.Remembered(state).Count == 1);
    }

    [Fact]
    public void AFailedRemovalInADistroKeepsItRemembered()
    {
        var (wsl, native, inDistro, state) = WslSetup();
        Assert.Null(CursorHook.ConfigureAll(true, 18765, 30, true, (native, false), wsl.List, state));
        File.WriteAllText(inDistro, "[1]"); // cannot be rewritten: Configure fails on it
        Assert.Contains(inDistro, CursorHook.ConfigureAll(false, 18765, 30, false, (native, false), wsl.List, state));
        Assert.False(CursorHook.HasAny(native)); // the native one is still removed
        Assert.Equal([inDistro], CursorHook.Remembered(state)); // and the distro's is tried again next time
    }

    [Fact]
    public void APartialInstallIsFoundAndRemoved()
    {
        var path = Path.Combine(_dir, "partial.json");
        Assert.Null(CursorHook.Configure(true, 18765, 30, path, false));
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        root["hooks"]!.AsObject().Remove("beforeMCPExecution"); // one event removed by hand
        File.WriteAllText(path, root.ToJsonString());
        Assert.False(CursorHook.Installed(path));
        Assert.True(CursorHook.HasAny(path)); // still asks the phone: offered for removal
        Assert.Null(CursorHook.Configure(false, 18765, 30, path, false));
        Assert.False(CursorHook.HasAny(path));
    }

    [Fact]
    public void TheWslHookAlwaysExitsZero()
    {
        var command = CursorHook.HookCommand(18765, windows: false);
        Assert.EndsWith("|| true", command);
        // no decision (an empty answer, or no agent) prints nothing: "no output" fails open in Cursor, while any text
        // that is not a valid answer would block the command
        var windows = CursorHook.HookCommand(18765, windows: true);
        Assert.EndsWith("catch { }; exit 0", windows);
        Assert.Contains("$ProgressPreference = 'SilentlyContinue'", windows);
        Assert.True(CursorHook.IsOurs(new System.Text.Json.Nodes.JsonObject { ["command"] = command }));
        Assert.True(CursorHook.IsOurs(new System.Text.Json.Nodes.JsonObject { ["command"] = CursorHook.HookCommand(8765, windows: true) }));
    }

    [Fact]
    public async Task NothingIsAskedUntilCursorApprovalsAreTurnedOn()
    {
        var cfg = Config("""{"claude":{"phoneApprovals":true,"timeoutSec":30}}""");
        using var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "push.json"));
        var approvals = new ApprovalStore(push, NullLogger<ApprovalStore>.Instance);
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Null(await CursorHook.HandleAsync(Json(Shell), cfg, approvals, new SessionMonitor(NullLogger<SessionMonitor>.Instance), CancellationToken.None));
        Assert.DoesNotContain(approvals.Snapshot(), a => a.Source == "Cursor");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigureAddsBothEventsKeepsOtherHooksAndRemovesOnlyPhoneBox(bool windows)
    {
        var path = Path.Combine(_dir, $"hooks-{windows}.json");
        File.WriteAllText(path, """
            {"version":1,"hooks":{"afterFileEdit":[{"command":"./hooks/format.sh"}],
             "beforeShellExecution":[{"command":"./hooks/audit.sh","matcher":"rm"}]}}
            """);

        Assert.Null(CursorHook.Configure(true, 18765, 30, path, windows));
        Assert.True(CursorHook.Installed(path));
        Assert.True(File.Exists(path + ".phonebox.bak"));
        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var hooks = doc.RootElement.GetProperty("hooks");
            Assert.Equal(2, hooks.GetProperty("beforeShellExecution").GetArrayLength()); // audit.sh and ours
            var ours = hooks.GetProperty("beforeMCPExecution")[0];
            Assert.Equal(45, ours.GetProperty("timeout").GetInt32());
            var command = ours.GetProperty("command").GetString()!;
            Assert.Contains("127.0.0.1:18765" + CursorHook.Endpoint, command);
            Assert.Contains(windows ? "Invoke-WebRequest" : "--noproxy 127.0.0.1", command);
        }

        Assert.Null(CursorHook.Configure(true, 18765, 30, path, windows)); // again: still one entry of ours
        Assert.Equal(2, JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("hooks").GetProperty("beforeShellExecution").GetArrayLength());

        Assert.Null(CursorHook.Configure(false, 18765, 30, path, windows));
        Assert.False(CursorHook.Installed(path));
        var text = File.ReadAllText(path);
        Assert.Contains("format.sh", text);
        Assert.Contains("audit.sh", text);
        Assert.DoesNotContain(CursorHook.Endpoint, text);
        Assert.DoesNotContain("beforeMCPExecution", text);
    }

    [Fact]
    public void ANewFileGetsAVersionAndRemovingFromNoFileCreatesNothing()
    {
        var path = Path.Combine(_dir, "new", "hooks.json");
        Assert.Null(CursorHook.Configure(false, 18765, 30, path, false));
        Assert.False(File.Exists(path));
        Assert.Null(CursorHook.Configure(true, 18765, 30, path, false));
        Assert.Equal(1, JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public void AHooksValueOfAnotherShapeIsLeftForTheUser()
    {
        var path = Path.Combine(_dir, "odd.json");
        File.WriteAllText(path, """{"version":1,"hooks":{"beforeShellExecution":"oops"}}""");
        Assert.NotNull(CursorHook.Configure(true, 18765, 30, path, false));
        Assert.Contains("oops", File.ReadAllText(path));
    }
}
