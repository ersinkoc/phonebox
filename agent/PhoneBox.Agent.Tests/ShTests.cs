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
        var run = Task.Run(() => Sh.RunWithTimeout(4000, tool, args));
        try
        {
            await WaitForFile(ready);
            var winner = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(7)));
            Assert.Same(run, winner);
            Assert.Null(await run);
            Assert.False(File.Exists(done));
        }
        finally
        {
            File.WriteAllText(release, "");
            await run.WaitAsync(TimeSpan.FromSeconds(16));
            // A late child can still create the ready file. Keep its release signal until it has finished.
            try { await WaitForFile(done); }
            catch (OperationCanceledException) { /* the readiness assertion carries the failure */ }
            if (File.Exists(done))
            {
                File.Delete(ready);
                File.Delete(release);
                File.Delete(done);
            }
        }
    }

    private static (string Tool, string[] Args) HoldingProcess(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var escaped = path.Replace("'", "''");
            var script = $"$p = '{escaped}'; [IO.File]::WriteAllText($p + '.ready', ''); "
                + "$timer = [Diagnostics.Stopwatch]::StartNew(); "
                + "while (![IO.File]::Exists($p + '.release') -and $timer.Elapsed.TotalSeconds -lt 20) { Start-Sleep -Milliseconds 25 }; "
                + "[IO.File]::WriteAllText($p + '.done', '')";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            // start /b launches the pipe holder with cmd's inherited handles, without a second PowerShell startup.
            var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            return (cmd, ["/d", "/c", "start", "/b", "powershell.exe", "-NoLogo", "-NoProfile",
                "-NonInteractive", "-EncodedCommand", encoded]);
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
