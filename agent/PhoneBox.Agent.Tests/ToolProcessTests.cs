using System.Diagnostics;
using PhoneBox.Agent;
using Xunit;

// Starts real processes against a deadline: run apart from other collections, so a busy two-core runner cannot
// push a child's start past it (see ShTests).
[Collection("Process deadline")]
public sealed class ToolProcessTests
{
    private static ProcessStartInfo Shell(string windows, string unix)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe") : "/bin/sh");
        foreach (var arg in OperatingSystem.IsWindows() ? new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", windows } : new[] { "-c", unix }) info.ArgumentList.Add(arg);
        return info;
    }

    [Fact]
    public async Task DrainsBothStreamsAndReportsFailure()
    {
        var result = await ToolProcess.RunAsync(Shell("[Console]::Out.Write('hello'); [Console]::Error.Write('failed'); exit 7", "printf hello; printf failed >&2; exit 7"), TimeSpan.FromSeconds(30), default);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("hello", result.Output);
        Assert.Equal("failed", result.Error);
    }

    [Fact]
    public async Task TimeoutTerminatesTheChildAndCancellationRemainsCancellation()
    {
        var marker = Path.Combine(Path.GetTempPath(), "pb-process-" + Guid.NewGuid());
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => ToolProcess.RunAsync(Shell(
                $"[IO.File]::WriteAllText('{marker}', [string]$PID); Start-Sleep -Seconds 60",
                $"echo $$ > '{marker}'; exec sleep 60"), TimeSpan.FromSeconds(8), default));
            Assert.True(File.Exists(marker), "The child must have started before timing out");
            var pid = int.Parse(File.ReadAllText(marker).Trim());
            var alive = false;
            try { using var child = Process.GetProcessById(pid); alive = !child.HasExited; } catch (ArgumentException) { }
            Assert.False(alive);
            using var ct = new CancellationTokenSource(); ct.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolProcess.RunAsync(Shell("Start-Sleep 60", "sleep 60"), TimeSpan.FromSeconds(30), ct.Token));
        }
        finally { File.Delete(marker); }
    }
    [Fact]
    public async Task CancellingGitHubRefreshReapsTheCli()
    {
        var marker = Path.Combine(Path.GetTempPath(), "pb-gh-process-" + Guid.NewGuid());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var start = Shell(
            $"[IO.File]::WriteAllText('{marker}', [string]$PID); Start-Sleep -Seconds 60",
            $"echo $$ > '{marker}'; exec sleep 60");
        start.UseShellExecute = false;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        int pid = 0;
        try
        {
            var refresh = GitHubMonitor.RunGh(start, cancellation.Token);
            while (pid == 0)
            {
                // the child may still be writing it (PowerShell holds the file without sharing): try again then
                if (File.Exists(marker))
                    try { int.TryParse((await File.ReadAllTextAsync(marker, cancellation.Token)).Trim(), out pid); }
                    catch (IOException) { }
                if (pid == 0) await Task.Delay(20, cancellation.Token);
            }
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
            Assert.False(IsAlive(pid), "GitHub CLI survived cancellation");
        }
        finally
        {
            if (pid > 0 && IsAlive(pid))
            {
                try { using var child = Process.GetProcessById(pid); child.Kill(entireProcessTree: true); }
                catch (ArgumentException) { }
            }
            try { File.Delete(marker); } catch (IOException) { }
        }

        static bool IsAlive(int id)
        {
            try { using var process = Process.GetProcessById(id); return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }
    }

}
