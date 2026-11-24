using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

/// <summary>Cursor's beforeShellExecution / beforeMCPExecution hooks, with inputs shaped like the ones Cursor 3.30 sends.</summary>
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

    [Fact]
    public void AUsersOwnHookPostingToTheSameEndpointIsLeftAlone()
    {
        var path = Path.Combine(_dir, "own.json");
        File.WriteAllText(path, """
            {"version":1,"hooks":{"beforeShellExecution":[{"command":"./hooks/log.sh && curl -s http://127.0.0.1:18765/api/hooks/cursor/permission -d @-"}]}}
            """);
        Assert.False(CursorHook.HasAny(path)); // not reported as PhoneBox's
        Assert.Null(CursorHook.Configure(true, 18765, 30, path, false));
        Assert.Null(CursorHook.Configure(false, 18765, 30, path, false));
        Assert.Contains("./hooks/log.sh", File.ReadAllText(path)); // survives install and removal
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
