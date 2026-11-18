using System.Text;
using PhoneBox.Agent;
using Xunit;

[CollectionDefinition("Process deadline", DisableParallelization = true)]
public sealed class ProcessDeadlineCollection { }

// Its deadline includes starting the shell and its child, so run the process fixture apart from other collections.
[Collection("Process deadline")]
public sealed class ShTests
{
    [Fact]
    public async Task OutputDeadlineAppliesAfterTheDirectProcessExits()
    {
        var path = Path.Combine(Path.GetTempPath(), "phonebox-sh-" + Guid.NewGuid().ToString("N"));
        var ready = path + ".ready";
        var release = path + ".release";
        var done = path + ".done";
        var (tool, args) = HoldingProcess(path);
        var run = Task.Run(() => Sh.RunWithTimeout(2000, tool, args));
        try
        {
            await WaitForFile(ready);
            var winner = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(4)));
            Assert.Same(run, winner);
            Assert.Null(await run);
            Assert.False(File.Exists(done));
        }
        finally
        {
            File.WriteAllText(release, "");
            await run.WaitAsync(TimeSpan.FromSeconds(16));
            await WaitForFile(done);
            File.Delete(ready);
            File.Delete(release);
            File.Delete(done);
        }
    }

    private static (string Tool, string[] Args) HoldingProcess(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var escaped = path.Replace("'", "''");
            var script = $"$p = '{escaped}'; [IO.File]::WriteAllText($p + '.ready', ''); "
                + "$timer = [Diagnostics.Stopwatch]::StartNew(); "
                + "while (![IO.File]::Exists($p + '.release') -and $timer.Elapsed.TotalSeconds -lt 12) { Start-Sleep -Milliseconds 25 }; "
                + "[IO.File]::WriteAllText($p + '.done', '')";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var parent = "$info = New-Object Diagnostics.ProcessStartInfo; "
                + "$info.FileName = $env:ComSpec; $info.UseShellExecute = $false; "
                + $"$info.Arguments = '/d /c start /b powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}'; "
                + "$cmd = [Diagnostics.Process]::Start($info); $cmd.WaitForExit(); exit $cmd.ExitCode";
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe").Replace('\\', '/');
            return (powershell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(parent))]);
        }

        const string unix = """
            (
                : > "$1.ready"
                for i in 1 2 3 4 5 6 7 8 9 10 11 12; do
                    [ -f "$1.release" ] && break
                    sleep 1
                done
                : > "$1.done"
            ) &
            """;
        return ("/bin/sh", ["-c", unix, "phonebox-test", path]);
    }

    private static async Task WaitForFile(string path)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!File.Exists(path))
            await Task.Delay(25, limit.Token);
    }
}
