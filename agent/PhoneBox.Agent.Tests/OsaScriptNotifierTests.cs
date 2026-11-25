using PhoneBox.Agent;
using Xunit;

/// <summary>
/// Notification text comes from the phone (a message's sender and words), so it is data and nothing else: it is handed to
/// osascript as arguments after a "--", never spliced into the script and never readable as an osascript option.
/// </summary>
public sealed class OsaScriptNotifierTests
{
    private const string Script = "display notification (item 1 of argv) with title (item 2 of argv) subtitle (item 3 of argv)";

    [Fact]
    public void ArgumentsAreTheScriptThenTheSeparatorThenBodyTitleSubtitle()
    {
        Assert.Equal(
            ["-e", "on run argv", "-e", Script, "-e", "end run", "--", "the body", "the title", "the subtitle"],
            OsaScriptNotifier.BuildArguments("the title", "the subtitle", "the body"));
    }

    [Fact]
    public void HostileTextStaysAsSingleValuesAfterTheSeparator()
    {
        var title = "He said \"hi\" & do shell script \"touch /tmp/pb-injected\"";
        var body = "line1\nline2 \\ backslash 'quote' $(touch /tmp/pb-injected2) `x`";
        var subtitle = "-e";
        var args = OsaScriptNotifier.BuildArguments(title, subtitle, body);
        var separator = args.ToList().IndexOf("--");
        Assert.Equal(6, separator);
        Assert.Equal([body, title, subtitle], args.Skip(separator + 1));
        // the only script statements are the three fixed ones that come before the separator
        Assert.Equal(3, args.Take(separator).Count(a => a == "-e"));
        Assert.DoesNotContain(args.Take(separator), a => a.Contains("touch") || a.Contains("injected"));
    }

    [Theory]
    [InlineData("-e", "return \"INJECTED\"", "-l JavaScript", "--")]
    [InlineData("", "a", "", "-x")]
    [InlineData("multi\nline", "tab\there", "emoji 🌍 Καλημέρα", "\\\"quoted\\\"")]
    public void TheRealOsascriptReadsEveryValueAsData(string a, string b, string c, string d)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var args = OsaScriptNotifier.ScriptArguments(
            ["on run argv", "return (item 1 of argv) & \"|\" & (item 2 of argv) & \"|\" & (item 3 of argv) & \"|\" & (item 4 of argv)", "end run"],
            [a, b, c, d]);
        var output = Sh.RunWithTimeout(10_000, "osascript", args.ToArray());
        Assert.NotNull(output);
        Assert.Equal($"{a}|{b}|{c}|{d}", output.TrimEnd('\n'));
    }

    // the default runner: kill on cancel (the agent stopping), kill at the timeout, the exit code as the answer
    [Fact]
    public async Task TheRunnerKillsTheProcessWhenCancelled()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cts = new CancellationTokenSource(200);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OsaScriptNotifier.Run("sleep", ["30"], 20_000, cts.Token));
        Assert.True(watch.ElapsedMilliseconds < 10_000, $"took {watch.ElapsedMilliseconds} ms"); // not the 20 s timeout
    }

    [Fact]
    public async Task TheRunnerGivesUpAtTheTimeout()
    {
        if (OperatingSystem.IsWindows()) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await OsaScriptNotifier.Run("sleep", ["30"], 300, CancellationToken.None));
        Assert.True(watch.ElapsedMilliseconds < 10_000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task TheRunnerAnswersWithTheExitCode()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.True(await OsaScriptNotifier.Run("true", [], 5000, CancellationToken.None));
        Assert.False(await OsaScriptNotifier.Run("false", [], 5000, CancellationToken.None));
        Assert.False(await OsaScriptNotifier.Run("pb-no-such-tool-" + Guid.NewGuid().ToString("N"), [], 5000, CancellationToken.None));
    }

    [Fact]
    public async Task PostAsyncHandsTheBuiltArgumentsToTheRunner()
    {
        IReadOnlyList<string>? seen = null;
        var notifier = new OsaScriptNotifier((args, _) => { seen = args; return Task.FromResult(true); });
        Assert.True(await notifier.PostAsync("t", "s", "b", null, null, CancellationToken.None));
        Assert.Equal(OsaScriptNotifier.BuildArguments("t", "s", "b"), seen);
    }

    [Fact]
    public async Task PostAsyncIsFalseWhenTheRunnerFailsOrThrows()
    {
        Assert.False(await new OsaScriptNotifier((_, _) => Task.FromResult(false)).PostAsync("t", "s", "b", null, null, CancellationToken.None));
        Assert.False(await new OsaScriptNotifier((_, _) => throw new InvalidOperationException("boom")).PostAsync("t", "s", "b", null, null, CancellationToken.None));
    }
}
