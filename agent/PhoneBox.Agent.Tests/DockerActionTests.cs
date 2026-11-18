using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>A start / stop / restart that first has to list the containers (cold cache).</summary>
public sealed class DockerActionTests : IDisposable
{
    private const string Container = "a1b2c3d4e5f6";
    private const string Ps = "{\"ID\":\"a1b2c3d4e5f6\",\"Image\":\"redis:7\",\"Names\":\"cache-1\",\"State\":\"running\",\"Status\":\"Up 5 minutes\"}\n";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-docker-act-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigStore _store;

    public DockerActionTests()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{}");
        _store = new ConfigStore(path);
        Configure(pageEnabled: true);
    }

    private void Configure(bool pageEnabled)
    {
        var error = _store.Save(JsonSerializer.Serialize(new { pages = new object[] {
            new { id = "home", title = "Home", enabled = true, widgets = new[] { new { id = "c1", type = "clock", x = 0, y = 0, w = 2, h = 2 } } },
            new { id = "p1", title = "Main", enabled = pageEnabled, widgets = new object[] {
                new { id = "d1", type = "docker", x = 0, y = 0, w = 6, h = 4, options = new { control = true } } } },
        } }));
        if (error is not null) throw new Exception(error);
    }

    [Fact]
    public async Task HidingTheWidgetDuringColdDiscoveryStopsTheAction()
    {
        var psEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = 0;
        var mon = new DockerMonitor(_store, NullLogger<DockerMonitor>.Instance)
        {
            CliOverride = "docker",
            RunnerOverride = async (_, _, args) =>
            {
                if (args is ["ps", ..])
                {
                    psEntered.TrySetResult();
                    await releasePs.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    return (0, Ps, "");
                }
                if (args is ["restart", ..]) Interlocked.Increment(ref actions);
                return (0, "", "");
            },
        };
        var act = mon.ActAsync(Container, "restart");
        try
        {
            await psEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Configure(pageEnabled: false);
        }
        finally { releasePs.TrySetResult(); }
        Assert.NotNull(await act.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, Volatile.Read(ref actions));
    }

    [Fact]
    public async Task AColdActionDoesNotWaitForContainerMetrics()
    {
        var releaseStats = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mon = new DockerMonitor(_store, NullLogger<DockerMonitor>.Instance)
        {
            CliOverride = "docker",
            RunnerOverride = async (_, _, args) =>
            {
                if (args is ["ps", ..]) return (0, Ps, "");
                if (args is ["stats", ..]) { await releaseStats.Task.WaitAsync(TimeSpan.FromSeconds(10)); return (0, "", ""); }
                if (args is ["restart", Container]) restarted.TrySetResult();
                return (0, "", "");
            },
        };
        var act = mon.ActAsync(Container, "restart");
        try { await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); } // sent while stats is still stalled
        finally { releaseStats.TrySetResult(); }
        Assert.Null(await act.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
