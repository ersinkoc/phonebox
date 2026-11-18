using Microsoft.Extensions.Logging;
using PhoneBox.Agent;
using Xunit;

public sealed class FileLoggerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-logger-{Guid.NewGuid():N}.log");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void LoggingAfterDisposalDoesNotThrowOrAppend()
    {
        using var provider = new FileLoggerProvider(_path);
        var logger = provider.CreateLogger("PhoneBox.Fixture");
        logger.LogInformation("before shutdown");
        provider.Dispose();
        provider.Dispose();
        logger.LogInformation("after shutdown");

        var text = File.ReadAllText(_path);
        Assert.Contains("before shutdown", text);
        Assert.DoesNotContain("after shutdown", text);
    }

    [Fact]
    public async Task DisposingWhileALogIsBeingFormattedDoesNotFailTheCaller()
    {
        using var provider = new FileLoggerProvider(_path);
        var logger = provider.CreateLogger("PhoneBox.Fixture");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Task.Run(() => logger.Log(LogLevel.Information, default, "late", null, (message, _) =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return message;
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            provider.Dispose();
        }
        finally { release.TrySetResult(); }

        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(File.ReadAllText(_path));
    }
}
