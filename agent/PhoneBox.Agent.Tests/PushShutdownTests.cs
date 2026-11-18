using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class PushShutdownTests
{
    private sealed class SaveWarning : ILogger<PushStore>
    {
        public TaskCompletionSource Warning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Warning.TrySetResult();
        }
    }

    [Fact]
    public void DisposingTheHostFlushesUpdatesStillWaitingForTheTimer()
    {
        var directory = Directory.CreateTempSubdirectory("phonebox-push-shutdown-");
        try
        {
            var path = Path.Combine(directory.FullName, "data.json");
            using (var services = new ServiceCollection()
                .AddSingleton(_ => new PushStore(NullLogger<PushStore>.Instance, path))
                .BuildServiceProvider())
            {
                var store = services.GetRequiredService<PushStore>();
                Assert.Null(store.Set("deployment", PushStore.ParseValue("ready"), null));
            }
            using var restarted = new PushStore(NullLogger<PushStore>.Instance, path);
            Assert.Equal("ready", restarted.Get("deployment")?.Data.GetString());
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void APlainFilenamePersistsInTheWorkingDirectory()
    {
        var name = "phonebox-push-" + Guid.NewGuid().ToString("N") + ".json";
        try
        {
            using (var store = new PushStore(NullLogger<PushStore>.Instance, name))
                Assert.Null(store.Set("deployment", PushStore.ParseValue("ready"), null));

            using var restarted = new PushStore(NullLogger<PushStore>.Instance, name);
            Assert.Equal("ready", restarted.Get("deployment")?.Data.GetString());
        }
        finally { File.Delete(name); }
    }

    [Fact]
    public async Task SavingRetriesAfterATemporaryFilesystemFailure()
    {
        var directory = Directory.CreateTempSubdirectory("phonebox-push-retry-");
        try
        {
            var path = Path.Combine(directory.FullName, "data.json");
            var blockedDestination = Directory.CreateDirectory(path);
            var logger = new SaveWarning();
            using var store = new PushStore(logger, path);
            Assert.Null(store.Set("deployment", PushStore.ParseValue("ready"), null));
            await logger.Warning.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("ready", store.Get("deployment")?.Data.GetString());

            blockedDestination.Delete();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            while (!File.Exists(path) && !timeout.IsCancellationRequested)
                await Task.Delay(50);
            Assert.True(File.Exists(path), "The unchanged value should retry once the destination is available.");
            using var restarted = new PushStore(NullLogger<PushStore>.Instance, path);
            Assert.Equal("ready", restarted.Get("deployment")?.Data.GetString());
        }
        finally { directory.Delete(recursive: true); }
    }
}
