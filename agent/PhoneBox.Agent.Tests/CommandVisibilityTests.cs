using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

// Starts real processes against a deadline: run apart from other collections, so a busy two-core runner cannot
// push a child's start past it (see ShTests).
[Collection("Process deadline")]
public sealed class CommandVisibilityTests
{
    [Fact]
    public async Task AHiddenPageCannotRunItsCommandWidget()
    {
        using var f = new CommandVisibilityFixture();
        f.Configure(enabled: false);
        Assert.False(await f.TryExecute());
    }

    [Fact]
    public async Task HidingAPreviouslyVisibleCommandRevokesAccess()
    {
        using var f = new CommandVisibilityFixture();
        f.Configure(enabled: true);
        Assert.True(await f.TryExecute());
        f.Configure(enabled: false);
        Assert.Null(f.Runner.Get("commands", "probe"));
    }

    // A run queued while its page was visible must not start after the page was hidden.
    [Fact]
    public async Task ARunQueuedBeforeThePageWasHiddenDoesNotStart()
    {
        using var f = new CommandVisibilityFixture();
        Func<Task>? queued = null;
        var runner = new CommandRunner(f.Store, NullLogger<CommandRunner>.Instance)
        {
            StartWorker = work => { queued = work; return Task.CompletedTask; },
        };
        f.Configure(enabled: true);
        Assert.NotNull(runner.Get("commands", "probe"));
        Assert.NotNull(queued);

        f.Configure(enabled: false);
        await queued!().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(f.Ran);
    }

    [Fact]
    public async Task RetiredInFlightCommandDoesNotPublishClockRollbackRefreshesAndSurrogatesStayIntact()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pb-cmd-retire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            var startedFile = Path.Combine(dir, "started");
            var releaseFile = Path.Combine(dir, "release");
            string MakeJson(bool enabled, string cmd, string? cmdUnix = null) => JsonSerializer.Serialize(new
            {
                pages = new object[]
                {
                    new { id = "home", title = "Home", enabled = true, widgets = new[] { new { id = "clk", type = "clock" } } },
                    new
                    {
                        id = "cmds",
                        title = "Cmds",
                        enabled,
                        widgets = new[] { new { id = "w1", type = "command", options = new { command = cmd, commandUnix = cmdUnix ?? cmd, interval = 60 } } },
                    },
                },
            });

            static string Ps(string s) => "'" + s.Replace("'", "''") + "'";
            static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";
            var winCmd = $"[IO.File]::WriteAllText({Ps(startedFile)},'1'); while (-not [IO.File]::Exists({Ps(releaseFile)})) {{ Start-Sleep -Milliseconds 10 }}; Write-Output 'retired-secret'";
            var unixCmd = $"printf '1' > {Sh(startedFile)}; while [ ! -f {Sh(releaseFile)} ]; do sleep 0.01; done; printf 'retired-secret\\n'";

            File.WriteAllText(cfgPath, MakeJson(true, winCmd, unixCmd));
            var store = new ConfigStore(cfgPath);
            var runner = new CommandRunner(store, NullLogger<CommandRunner>.Instance);
            var resultsField = typeof(CommandRunner).GetField("_results", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var runningField = typeof(CommandRunner).GetField("_running", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var results = (System.Collections.Concurrent.ConcurrentDictionary<string, CommandResult>)resultsField.GetValue(runner)!;
            var running = (System.Collections.Concurrent.ConcurrentDictionary<string, byte>)runningField.GetValue(runner)!;

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var watch = new FileSystemWatcher(dir) { EnableRaisingEvents = true })
            {
                watch.Created += (_, e) => { if (e.FullPath == startedFile) entered.TrySetResult(); };
                _ = runner.Get("cmds", "w1");
                if (File.Exists(startedFile)) entered.TrySetResult();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }

            Assert.Null(store.Save(MakeJson(false, winCmd, unixCmd)));
            File.WriteAllText(releaseFile, "go");
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                while (!running.IsEmpty && !cts.IsCancellationRequested) await Task.Yield();
            Assert.Empty(running); // the worker finished: an empty result list below means it was discarded, not still due
            Assert.Empty(results);

            // Clock rollback (`At` in the future) marks cached result stale and starts a run
            Assert.Null(store.Save(MakeJson(true, "Write-Output 'ok'", "printf 'ok\\n'")));
            var resolved = OperatingSystem.IsWindows() ? "Write-Output 'ok'" : "printf 'ok\\n'";
            var key = $"cmds/w1:{resolved.GetHashCode()}";
            results[key] = new CommandResult("before-rollback", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3_600_000L, true, false, 1);
            _ = runner.Get("cmds", "w1", force: false)!;
            // the rerun publishes a fresh result; waited for, since it may finish before or after Get returns
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                while ((!running.IsEmpty || results.GetValueOrDefault(key)?.Output != "ok") && !cts.IsCancellationRequested) await Task.Delay(10);
            var rerun = results.GetValueOrDefault(key);
            Assert.Equal("ok", rerun?.Output);
            Assert.True(rerun!.Ok);

            // Surrogate pair at MaxOutput boundary is never split into a lone high surrogate
            var truncated = CommandRunner.TruncateOutput(new string('a', 3999) + "🚀tail", 4000);
            Assert.Equal(4000, truncated.Length);
            Assert.DoesNotContain(truncated, char.IsSurrogate);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

// The configured command only writes an owned marker. Its atomic rename gates observation of execution;
// the PID is captured before the marker appears so teardown can wait for the shell to exit.
public sealed class CommandVisibilityFixture : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-command-visibility-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigStore _store;
    private readonly string _marker;
    public CommandRunner Runner { get; }
    public ConfigStore Store => _store;
    /// <summary>The command wrote its marker (it ran).</summary>
    public bool Ran => File.Exists(_marker) || File.Exists(_marker + ".tmp");

    public CommandVisibilityFixture()
    {
        Directory.CreateDirectory(_dir);
        _marker = Path.Combine(_dir, "ran");
        _store = new ConfigStore(Path.Combine(_dir, "config.json"));
        Runner = new CommandRunner(_store, NullLogger<CommandRunner>.Instance);
    }

    public void Configure(bool enabled)
    {
        static string Ps(string s) => "'" + s.Replace("'", "''") + "'";
        static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";
        var command = $"[IO.File]::WriteAllText({Ps(_marker + ".tmp")},[string]$PID); [IO.File]::Move({Ps(_marker + ".tmp")},{Ps(_marker)})";
        var commandUnix = $"printf '%s' $$ > {Sh(_marker + ".tmp")}; mv {Sh(_marker + ".tmp")} {Sh(_marker)}";
        var error = _store.Save(JsonSerializer.Serialize(new { pages = new object[] {
            new { id = "home", enabled = true, widgets = new[] { new { type = "clock" } } },
            new { id = "commands", enabled, widgets = new[] { new { id = "probe", type = "command", options = new { command, commandUnix } } } },
        } }));
        if (error is not null) throw new Exception(error);
    }

    public async Task<bool> TryExecute()
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = new FileSystemWatcher(_dir) { EnableRaisingEvents = true };
        watch.Created += (_, e) => { if (e.FullPath == _marker) observed.TrySetResult(); };
        watch.Renamed += (_, e) => { if (e.FullPath == _marker) observed.TrySetResult(); };
        if (Runner.Get("commands", "probe") is null) return false;
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var pid = int.Parse(await File.ReadAllTextAsync(_marker));
        try { using var shell = Process.GetProcessById(pid); await shell.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (ArgumentException) { } // already exited
        return true;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
