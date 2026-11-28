using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class ApprovalHighRiskTests
{
    [Theory]
    [InlineData("Write", true)]
    [InlineData("Edit", true)]
    [InlineData("MultiEdit", true)]
    [InlineData("NotebookEdit", true)]
    [InlineData("Bash", true)]
    [InlineData("PowerShell", true)] // Claude Code on Windows
    [InlineData("mcp__x__y", true)]
    [InlineData("mcp__server__write_file", true)]
    [InlineData("Read", false)]
    [InlineData("Glob", false)]
    [InlineData("Grep", false)]
    [InlineData("LS", false)]
    [InlineData("WebFetch", false)]
    [InlineData("WebSearch", false)]
    [InlineData("TodoWrite", false)]
    [InlineData("AskUserQuestion", false)]
    [InlineData("mcp", true)] // not on the whitelist: fail-closed (the old exemption was the prefix itself, which says nothing)
    public void JudgesTheWriteClassToolsHighRisk(string tool, bool expected) =>
        Assert.Equal(expected, ClaudeHook.IsHighRisk(tool));

    // fail-closed: anything outside the read-only whitelist is high risk —
    // an empty tool name, a tool Claude Code adds in the future, an
    // orchestrating agent tool no one has named yet
    [Theory]
    [InlineData("", true)]
    [InlineData("ExitPlanMode", true)]
    [InlineData("Task", true)]
    [InlineData("SomeFutureTool", true)]
    [InlineData("Read", false)]
    public void UnknownToolsAreHighRisk(string tool, bool expected) =>
        Assert.Equal(expected, ClaudeHook.IsHighRisk(tool));

    [Fact]
    public void TheRecordCarriesTheFlagWithFalseAsItsDefault()
    {
        // existing constructors keep working: the flag defaults to false
        var plain = new Approval("a", "Claude Code", "s", "p", "/w", "Read a file", "d", null, 1, 2);
        Assert.False(plain.HighRisk);
        var risky = new Approval("b", "Claude Code", "s", "p", "/w", "Run a command", "d", null, 1, 2, HighRisk: true);
        Assert.True(risky.HighRisk);
    }

    [Fact]
    public void AQuestionIsNeverMarkedHighRisk()
    {
        // the gate DecideAsync applies for AskUserQuestion: a question is answered,
        // not allowed, so it is never marked — even a write-shaped tool name
        Assert.False(ClaudeHook.IsHighRisk("AskUserQuestion"));
        Assert.True(ClaudeHook.IsHighRisk("Bash")); // the same tool as a permission would be high-risk
    }
}
