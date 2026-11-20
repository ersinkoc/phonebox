using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public sealed class CodexHookTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public async Task PermissionRequestIsShownAsCodexAndReturnsThePhoneDecision()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-codex-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """{"claude":{"phoneApprovals":true,"onlyWhenAway":false,"timeoutSec":30}}""");
            var cfg = new ConfigStore(cfgPath);
            using var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(dir, "push.json"));
            var approvals = new ApprovalStore(push, NullLogger<ApprovalStore>.Instance);
            var sessions = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
            Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var pending = CodexHook.HandleAsync(Json("""
                {"hook_event_name":"PermissionRequest","session_id":"thr_123","cwd":"/work/phonebox",
                 "tool_name":"Bash","tool_input":{"command":"npm test","description":"Run the test suite"}}
                """), cfg, approvals, sessions, CancellationToken.None);

            var approval = Assert.Single(approvals.Snapshot());
            Assert.Equal("Codex", approval.Source);
            Assert.Equal("phonebox", approval.Project);
            Assert.Equal("/work/phonebox", approval.Cwd);
            Assert.Equal("Run a command", approval.Tool);
            Assert.Equal("npm test", approval.Detail);
            Assert.Equal("Run the test suite", approval.Description);
            Assert.True(approvals.Decide(approval.Id, "allow"));

            var output = JsonSerializer.Serialize(await pending);
            Assert.Contains("PermissionRequest", output);
            Assert.Contains("\"behavior\":\"allow\"", output);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ConfigurePreservesOtherCodexHooksAndRemovesOnlyPhoneBox()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-codex-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "hooks.json");
            File.WriteAllText(path, """
                {"description":"mine","hooks":{"PostToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"echo done"}]}]}}
                """);

            Assert.Null(CodexHook.Configure(true, 18765, 30, path));
            Assert.True(CodexHook.Installed(path));
            Assert.Contains(CodexHook.Endpoint, File.ReadAllText(path));
            Assert.True(File.Exists(path + ".phonebox.bak"));

            Assert.Null(CodexHook.Configure(false, 18765, 30, path));
            Assert.False(CodexHook.Installed(path));
            var text = File.ReadAllText(path);
            Assert.Contains("echo done", text);
            Assert.DoesNotContain(CodexHook.Endpoint, text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UnrelatedHookMentioningEndpointIsNotTreatedAsOurs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-codex-unrelated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "hooks.json");
            File.WriteAllText(path, """
                {"hooks":{"PermissionRequest":[{"matcher":".*","hooks":[{"type":"command","command":"echo /api/hooks/codex/permission"}]}]}}
                """);

            Assert.False(CodexHook.Installed(path));
            Assert.Null(CodexHook.Configure(true, 18765, 30, path));
            Assert.True(CodexHook.Installed(path));

            Assert.Null(CodexHook.Configure(false, 18765, 30, path));
            Assert.False(CodexHook.Installed(path));
            var text = File.ReadAllText(path);
            Assert.Contains("echo /api/hooks/codex/permission", text);
            Assert.DoesNotContain("127.0.0.1:18765", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AHookOnALongerPathAtTheSameAddressIsNotOurs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-codex-longer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "hooks.json");
            File.WriteAllText(path, """
                {"hooks":{"PermissionRequest":[{"matcher":".*","hooks":[{"type":"command","command":"curl -s http://127.0.0.1:9000/api/hooks/codex/permission-audit -d @-"}]}]}}
                """);

            Assert.False(CodexHook.Installed(path));
            Assert.Null(CodexHook.Configure(true, 18765, 30, path));
            Assert.Null(CodexHook.Configure(false, 18765, 30, path));
            Assert.Contains("/api/hooks/codex/permission-audit", File.ReadAllText(path)); // someone else's hook survives
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheInstalledHookNeverGoesThroughAProxy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-codex-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "hooks.json");
            Assert.Null(CodexHook.Configure(true, 18765, 30, path));
            Assert.Contains("--noproxy 127.0.0.1", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
