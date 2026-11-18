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

    [Fact]
    public async Task HidingTheWidgetDuringDiscoveryStopsTheDeckAction()
    {
        await using var fixture = await ObsPermissionFixture.Create();
        fixture.Configure(pageEnabled: true, control: true, obsEnabled: true);
        fixture.HoldInputs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var act = fixture.Monitor.DeckAct("record", null, null, null, default);
        try
        {
            await fixture.InputsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Configure(pageEnabled: false, control: true, obsEnabled: true);
        }
        finally { fixture.HoldInputs.TrySetResult(); }
        Assert.False((await act.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.Equal(0, fixture.Mutations);
    }

    [Fact]
    public async Task TurningObsOffStopsServingTheCachedDeck()
    {
        await using var fixture = await ObsPermissionFixture.Create();
        fixture.Configure(pageEnabled: true, control: true, obsEnabled: true);
        Assert.True((await fixture.Monitor.DeckSnapshot(default).WaitAsync(TimeSpan.FromSeconds(10)))["connected"]!.GetValue<bool>());
        fixture.Configure(pageEnabled: true, control: true, obsEnabled: false);
        Assert.False((await fixture.Monitor.DeckSnapshot(default).WaitAsync(TimeSpan.FromSeconds(10)))["connected"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ARequestDroppedByDisconnectCleanupStillEndsAtItsDeadline()
    {
        await using var fixture = await ObsPermissionFixture.Create();
        fixture.Configure(pageEnabled: true, control: true);
        fixture.Silent = true;
        var act = fixture.Monitor.ActAsync("record", null);
        for (var i = 0; i < 100 && fixture.Mutations == 0; i++) await Task.Delay(50);
        Assert.Equal(1, fixture.Mutations);
        // what RunConnection's cleanup does to a request it did not see while failing the others
        var pending = typeof(ObsMonitor).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Monitor)!;
        pending.GetType().GetMethod("Clear")!.Invoke(pending, null);
        Assert.Equal("OBS did not answer", await act.WaitAsync(TimeSpan.FromSeconds(15)));
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

    /// <summary>When set, GetInputList is answered only after it completes (bounded), so discovery stays in flight.</summary>
    public TaskCompletionSource? HoldInputs { get; set; }
    public TaskCompletionSource InputsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>When set, requests are read but never answered.</summary>
    public volatile bool Silent;

    public void Configure(bool pageEnabled, bool control, bool stack = false, bool obsEnabled = false)
    {
        object widget = new { type = "obs", options = new { control } };
        if (stack) widget = new { type = "stack", options = new { widgets = new[] { widget } } };
        var error = _config.Save(JsonSerializer.Serialize(new { obs = new { enabled = obsEnabled }, pages = new object[] {
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
            if (Silent) continue;
            if (type == "GetInputList")
            {
                InputsEntered.TrySetResult();
                if (HoldInputs is { } hold) await hold.Task.WaitAsync(TimeSpan.FromSeconds(10), _stop.Token);
            }
            var reply = new JsonObject { ["op"] = 7, ["d"] = new JsonObject {
                ["requestId"] = d["requestId"]!.DeepClone(), ["requestStatus"] = new JsonObject { ["result"] = true },
                ["responseData"] = new JsonObject(),
            } };
            await _peer.SendAsync(Encoding.UTF8.GetBytes(reply.ToJsonString()), WebSocketMessageType.Text, true, _stop.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _stop.Cancel();
            HoldInputs?.TrySetResult();
            _client.Abort();
            _peer?.Abort();
            foreach (var task in new[] { _read, _serve }) if (task is not null)
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } catch (WebSocketException) { }
        }
        finally
        {
            // a peer task that failed or overran still reports its error, after the listener and files are released
            _peer?.Dispose();
            _client.Dispose();
            _server.Close();
            Monitor.Dispose();
            _stop.Dispose();
            Directory.Delete(_dir, recursive: true);
        }
    }
}
