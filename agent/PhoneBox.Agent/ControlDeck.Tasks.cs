using System.Diagnostics;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>Tracks only processes started by a configured Control Deck task.</summary>
internal sealed class ControlTasks(ConfigStore config, ILogger log) : IDisposable
{
    private sealed class Job
    {
        public readonly CancellationTokenSource Stop = new();
        public string State = "Starting";
        public string Output = "";
        public Task? Work;
    }
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Job> _jobs = [];
    public string State(string id) { lock (_gate) return _jobs.TryGetValue(id, out var job) ? job.State : "Not started"; }
    public string Output(string id) { lock (_gate) return _jobs.TryGetValue(id, out var job) ? job.Output : "No task output"; }
    public DeckResult Start(DeckAction action, bool stop)
    {
        lock (_gate)
        {
            if (stop)
            {
                if (!_jobs.TryGetValue(action.Id, out var current) || current.Work?.IsCompleted != false) return new(false, "This task is not running");
                current.Stop.Cancel(); return new(true);
            }
            if (_jobs.TryGetValue(action.Id, out var previous) && previous.Work?.IsCompleted == false) return new(false, "This task is already running");
            var shortcut = config.Get().Shortcuts.FirstOrDefault(s => s.Id == action.Target);
            if (shortcut is null || shortcut.Cmd.StartsWith("builtin:", StringComparison.Ordinal)) return new(false, "Choose an executable shortcut for this task");
            previous?.Stop.Dispose();
            var job = new Job(); _jobs[action.Id] = job;
            job.Work = Run(job, shortcut, action.Kind == "server" ? TimeSpan.FromHours(24) : TimeSpan.FromMinutes(30));
            return job.State == "Failed to start" ? new(false, "The configured task could not be started") : new(true);
        }
    }

    /// <summary>Cancels a running task or server job if still active without failing when stopped.</summary>
    public void StopIfRunning(string id)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(id, out var current) && current.Work?.IsCompleted == false)
                current.Stop.Cancel();
        }
    }
    private async Task Run(Job job, Shortcut shortcut, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(shortcut.Cmd, shortcut.Args ?? "")
        {
            WorkingDirectory = shortcut.Cwd ?? AppPaths.NeutralCwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Could not start configured task");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(job.Stop.Token);
            deadline.CancelAfter(timeout);
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var output = new StringBuilder(); var outputGate = new object();
            async Task Read(StreamReader reader)
            {
                var buffer = new char[2048];
                try
                {
                    int count;
                    while ((count = await reader.ReadAsync(buffer, drain.Token)) > 0)
                        lock (outputGate)
                        {
                            output.Append(buffer, 0, count); if (output.Length > 65536) output.Remove(0, output.Length - 65536);
                            lock (_gate) job.Output = output.ToString();
                        }
                }
                catch (OperationCanceledException) when (drain.IsCancellationRequested) { }
            }
            var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
            lock (_gate) job.State = "Running";
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                // A daemon child can inherit a pipe after its parent exits; do not wait forever for that pipe.
                drain.CancelAfter(TimeSpan.FromSeconds(1));
                await Task.WhenAll(stdout, stderr);
                lock (_gate) { job.State = process.ExitCode == 0 ? "Completed" : $"Failed ({process.ExitCode})"; job.Output = output.ToString(); }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                await Task.WhenAll(stdout, stderr);
                lock (_gate) { job.State = job.Stop.IsCancellationRequested ? "Stopped" : "Timed out"; job.Output = output.ToString(); }
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "Configured Control Deck task failed"); lock (_gate) job.State = "Failed to start"; }
    }
    public void Dispose()
    {
        lock (_gate) foreach (var job in _jobs.Values) job.Stop.Cancel();
    }
    public async Task StopAsync()
    {
        Task[] work;
        lock (_gate) { foreach (var job in _jobs.Values) job.Stop.Cancel(); work = _jobs.Values.Select(j => j.Work).OfType<Task>().ToArray(); }
        try { await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(8)); } catch (TimeoutException) { }
    }
}
