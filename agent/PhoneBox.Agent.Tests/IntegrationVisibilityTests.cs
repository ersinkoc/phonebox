using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class IntegrationVisibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenPagesGrantNeitherDockerNorHomeAssistantControl(bool stack)
    {
        await using var f = new IntegrationVisibilityFixture();
        f.Configure(enabled: false, control: true, stack);
        Assert.False(f.Docker.ControlAllowed());
        Assert.NotNull(await f.Ha.ToggleAsync("light.desk"));
        Assert.Equal(0, f.Mutations);
        Assert.Empty(await f.StateIds());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HidingAfterSuccessRevokesBothControlPermissions(bool stack)
    {
        await using var f = new IntegrationVisibilityFixture();
        f.Configure(enabled: true, control: false, stack);
        Assert.False(f.Docker.ControlAllowed());
        Assert.NotNull(await f.Ha.ToggleAsync("light.desk"));
        Assert.Equal(0, f.Mutations);
        f.Configure(enabled: true, control: true, stack);
        Assert.True(f.Docker.ControlAllowed());
        Assert.Null(await f.Ha.ToggleAsync("light.desk"));
        Assert.Equal(1, f.Mutations);
        f.Configure(enabled: false, control: true, stack);
        Assert.False(f.Docker.ControlAllowed());
        Assert.NotNull(await f.Ha.ToggleAsync("light.desk"));
        Assert.Equal(1, f.Mutations);
    }

    [Fact]
    public async Task DisabledStackChildrenGrantNoControlOrEntityAccess()
    {
        await using var f = new IntegrationVisibilityFixture();
        f.Configure(enabled: true, control: true, stack: true, childEnabled: false);
        Assert.False(f.Docker.ControlAllowed());
        Assert.NotNull(await f.Ha.ToggleAsync("light.desk"));
        Assert.Equal(0, f.Mutations);
        Assert.Empty(await f.StateIds());
    }
}

// A loopback peer receives actual production HomeAssistant HTTP calls. Docker checks exercise its public
// authorization predicate; no Docker daemon or container is accessed by these tests.
public sealed class IntegrationVisibilityFixture : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-integration-visibility-" + Guid.NewGuid().ToString("N"));
    private readonly HttpListener _server = TestPorts.Listen();
    private readonly ConfigStore _store;
    private readonly Task _serve;
    private int _mutations;
    public int Mutations => Volatile.Read(ref _mutations);
    public DockerMonitor Docker { get; }
    public HomeAssistant Ha { get; }

    public IntegrationVisibilityFixture()
    {
        Directory.CreateDirectory(_dir);
        _store = new ConfigStore(Path.Combine(_dir, "config.json"));
        Docker = new DockerMonitor(_store, NullLogger<DockerMonitor>.Instance);
        Ha = new HomeAssistant(_store, NullLogger<HomeAssistant>.Instance);
        _serve = Serve();
    }

    public void Configure(bool enabled, bool control, bool stack = false, bool childEnabled = true)
    {
        object[] widgets = [new { type = "docker", enabled = childEnabled, options = new { control } },
            new { type = "ha", enabled = childEnabled, options = new { entities = "light.desk", control } }];
        if (stack) widgets = [new { type = "stack", options = new { widgets } }];
        var error = _store.Save(JsonSerializer.Serialize(new {
            homeAssistant = new { url = _server.Prefixes.Single().TrimEnd('/'), token = "fixture-only" },
            pages = new object[] {
                new { id = "home", enabled = true, widgets = new[] { new { type = "clock" } } },
                new { id = "integrations", enabled, widgets },
            },
        }));
        if (error is not null) throw new Exception(error);
    }

    public async Task<string[]> StateIds()
    {
        var result = JsonSerializer.SerializeToElement(await Ha.StatesAsync(["light.desk"]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return result.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();
    }

    private async Task Serve()
    {
        while (_server.IsListening)
        {
            HttpListenerContext context;
            try { context = await _server.GetContextAsync(); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            using var reader = new StreamReader(context.Request.InputStream);
            await reader.ReadToEndAsync();
            if (context.Request.HttpMethod == "POST") Interlocked.Increment(ref _mutations);
            var body = context.Request.Url!.AbsolutePath == "/api/states"
                ? """[{"entity_id":"light.desk","state":"off","attributes":{"friendly_name":"Desk"}}]""" : "[]";
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
            context.Response.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _server.Close();
        await _serve.WaitAsync(TimeSpan.FromSeconds(5));
        Docker.Dispose();
        Directory.Delete(_dir, recursive: true);
    }
}
