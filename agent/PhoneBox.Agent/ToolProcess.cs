using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>Bounded execution for optional tools. Cancellation also reaps the process tree.</summary>
internal static class ToolProcess
{
    public static string? Find(string tool)
    {
        var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe", tool } : [tool];
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(OperatingSystem.IsWindows() ? [] : new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" });
        return dirs.SelectMany(d => names.Select(n => Path.Combine(d.Trim('"', ' '), n))).FirstOrDefault(File.Exists);
    }

    internal sealed record Result(int ExitCode, string Output, string Error);

    public static Task<Result> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct) => RunAsync(start, timeout, ct, null);

    /// <param name="stdin">Text written to the program's standard input before it is closed; null leaves the input alone.</param>
    public static async Task<Result> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct, string? stdin)
    {
        ct.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.RedirectStandardInput = stdin is not null;
        start.WorkingDirectory = AppPaths.NeutralCwd;
        using var process = Process.Start(start) ?? throw new IOException("Could not start " + start.FileName);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            if (stdin is not null)
            {
                // inside the deadline: a program that never reads its input must not hold the caller for ever
                try { await process.StandardInput.WriteAsync(stdin.AsMemory(), deadline.Token); await process.StandardInput.FlushAsync(deadline.Token); process.StandardInput.Close(); }
                catch (IOException) { } // the program ended before it read everything: its exit code says why
            }
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(output, error).WaitAsync(deadline.Token);
            return new Result(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(Path.GetFileName(start.FileName) + " timed out");
        }
    }
}
