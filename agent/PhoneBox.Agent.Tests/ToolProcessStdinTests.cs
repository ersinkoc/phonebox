using System.Diagnostics;
using PhoneBox.Agent;
using Xunit;

/// <summary>Handing a program its input: it arrives whole, and a program that does not read it can neither hang the caller nor make it throw.</summary>
public sealed class ToolProcessStdinTests
{
    private static ProcessStartInfo Tool(string name, params string[] args)
    {
        var psi = new ProcessStartInfo(name);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    [Fact]
    public async Task ALargeInputReachesTheProgramWhole()
    {
        if (OperatingSystem.IsWindows()) return;
        var text = string.Concat(Enumerable.Repeat("Καλημέρα 🌍 line\n", 20_000));
        var result = await ToolProcess.RunAsync(Tool("/bin/cat"), TimeSpan.FromSeconds(20), CancellationToken.None, text);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(text, result.Output);
    }

    [Fact]
    public async Task AProgramThatNeverReadsItsInputEndsAtTheDeadlineNotNever()
    {
        if (OperatingSystem.IsWindows()) return;
        var big = new string('x', 1_000_000);
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => ToolProcess.RunAsync(Tool("/bin/sleep", "30"), TimeSpan.FromSeconds(1), CancellationToken.None, big));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "took " + watch.Elapsed);
    }

    [Fact]
    public async Task AProgramThatEndsWithoutReadingIsAnExitCodeNotAnException()
    {
        if (OperatingSystem.IsWindows()) return;
        var big = new string('x', 1_000_000);
        var result = await ToolProcess.RunAsync(Tool("/usr/bin/false"), TimeSpan.FromSeconds(10), CancellationToken.None, big);
        Assert.Equal(1, result.ExitCode);
    }
}
