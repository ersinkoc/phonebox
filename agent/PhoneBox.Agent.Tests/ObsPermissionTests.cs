using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class ObsPermissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenPagesCannotGrantObsControl(bool stack)
    {
        await using var fixture = await ObsPermissionFixture.Create();
        fixture.Configure(pageEnabled: false, control: true, stack);
        Assert.Contains("turned off", await fixture.Monitor.ActAsync("record", null));
        Assert.False((await fixture.Monitor.DeckAct("record", null, null, null, default)).Ok);
        Assert.Equal(0, fixture.Mutations);
    }

    [Fact]
    public async Task VisibleWidgetPermissionStillControlsBothObsSurfaces()
    {
        await using var fixture = await ObsPermissionFixture.Create();
        fixture.Configure(pageEnabled: true, control: false);
        Assert.NotNull(await fixture.Monitor.ActAsync("record", null));
        Assert.False((await fixture.Monitor.DeckAct("record", null, null, null, default)).Ok);
        Assert.Equal(0, fixture.Mutations);
        fixture.Configure(pageEnabled: true, control: true);
        Assert.Null(await fixture.Monitor.ActAsync("record", null));
        Assert.True((await fixture.Monitor.DeckAct("record", null, null, null, default)).Ok);
        Assert.Equal(2, fixture.Mutations);
        fixture.Configure(pageEnabled: false, control: true);
        Assert.NotNull(await fixture.Monitor.ActAsync("record", null));
        Assert.False((await fixture.Monitor.DeckAct("record", null, null, null, default)).Ok);
        Assert.Equal(2, fixture.Mutations);
    }
}

// A real WebSocket at the OBS transport boundary; all action dispatch and permission checks are production code.
public sealed class ObsPermissionFixture : IAsyncDisposable
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-obs-permission-" + Guid.NewGuid().ToString("N"));
    private readonly HttpListener _server = TestPorts.Listen();
    private readonly ClientWebSocket _client = new();
    private readonly CancellationTokenSource _stop = new();
    private WebSocket? _peer;
    private Task? _serve;
    private Task? _read;
    private int _mutations;
    private readonly ConfigStore _config;
    public ObsMonitor Monitor { get; }
    public int Mutations => Volatile.Read(ref _mutations);

    private ObsPermissionFixture()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{}");
        _config = new ConfigStore(path);
        Monitor = new ObsMonitor(_config, NullLogger<ObsMonitor>.Instance);
    }

    public static async Task<ObsPermissionFixture> Create()
    {
        var f = new ObsPermissionFixture();
        try
        {
            var incoming = f._server.GetContextAsync();
            var connecting = f._client.ConnectAsync(new Uri(f._server.Prefixes.Single().Replace("http://", "ws://", StringComparison.Ordinal)), default);
            var context = await incoming.WaitAsync(TimeSpan.FromSeconds(5));
            f._peer = (await context.AcceptWebSocketAsync(null)).WebSocket;
            await connecting.WaitAsync(TimeSpan.FromSeconds(5));
            typeof(ObsMonitor).GetField("_ws", Fields)!.SetValue(f.Monitor, f._client);
            typeof(ObsMonitor).GetField("_snap", Fields)!.SetValue(f.Monitor,
                new ObsSnapshot(true, null, false, 0, false, false, 0, null, [], null, null));
            f._read = (Task)typeof(ObsMonitor).GetMethod("ReadLoop", Fields)!.Invoke(f.Monitor, [f._client, f._stop.Token])!;
            f._serve = f.Serve();
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }

    public void Configure(bool pageEnabled, bool control, bool stack = false)
    {
        object widget = new { type = "obs", options = new { control } };
        if (stack) widget = new { type = "stack", options = new { widgets = new[] { widget } } };
        var error = _config.Save(JsonSerializer.Serialize(new { pages = new object[] {
            new { id = "home", enabled = true, widgets = new[] { new { type = "clock" } } },
            new { id = "obs", enabled = pageEnabled, widgets = new[] { widget } },
        } }));
        if (error is not null) throw new Exception(error);
    }

    private async Task Serve()
    {
        var buffer = new byte[65536];
        while (!_stop.IsCancellationRequested)
        {
            var r = await _peer!.ReceiveAsync(buffer, _stop.Token);
            if (r.MessageType == WebSocketMessageType.Close) return;
            var d = JsonNode.Parse(buffer.AsSpan(0, r.Count))!["d"]!;
            var type = d["requestType"]!.GetValue<string>();
            if (type == "ToggleRecord") Interlocked.Increment(ref _mutations);
            var reply = new JsonObject { ["op"] = 7, ["d"] = new JsonObject {
                ["requestId"] = d["requestId"]!.DeepClone(), ["requestStatus"] = new JsonObject { ["result"] = true },
                ["responseData"] = new JsonObject(),
            } };
            await _peer.SendAsync(Encoding.UTF8.GetBytes(reply.ToJsonString()), WebSocketMessageType.Text, true, _stop.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _client.Abort();
        _peer?.Abort();
        foreach (var task in new[] { _read, _serve }) if (task is not null)
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } catch (WebSocketException) { }
        _peer?.Dispose();
        _client.Dispose();
        _server.Close();
        Monitor.Dispose();
        _stop.Dispose();
        Directory.Delete(_dir, recursive: true);
    }
}
