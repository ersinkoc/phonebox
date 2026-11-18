using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// Lock and vacuum toggles pick their service from the current state: that state must come from a successful read
/// with the same settings the call goes to, and the permission must still hold when the call is sent.
/// </summary>
public sealed class HomeAssistantStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-ha-state-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigStore _store;
    private readonly FakeHomeAssistant _a = new("token-a");
    private readonly FakeHomeAssistant _b = new("token-b");

    public HomeAssistantStateTests()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{}");
        _store = new ConfigStore(path);
    }

    private HomeAssistant Client() => new(_store, NullLogger<HomeAssistant>.Instance);

    private void Configure(FakeHomeAssistant server, bool control = true, string? token = null)
    {
        var error = _store.Save(JsonSerializer.Serialize(new
        {
            homeAssistant = new { url = server.Url, token = token ?? server.Token },
            pages = new[] { new { id = "home", title = "Home", widgets = new object[] {
                new { id = "ha", type = "ha", x = 0, y = 0, w = 6, h = 3, options = new { entities = "lock.door, vacuum.robot, sensor.temp", control } },
            } } },
        }));
        if (error is not null) throw new Exception(error);
    }

    [Fact]
    public async Task AVacuumIsNotStartedWhenItsStateCannotBeRead()
    {
        _a.FailStates = true;
        _a.Vacuum = "cleaning";
        Configure(_a);
        Assert.NotNull(await Client().ToggleAsync("vacuum.robot").WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Empty(_a.Calls);
    }

    [Fact]
    public async Task HidingTheWidgetDuringTheStateReadCancelsTheCall()
    {
        Configure(_a);
        _a.HoldStates = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var toggle = Client().ToggleAsync("lock.door");
        try
        {
            await _a.StatesEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Configure(_a, control: false);
        }
        finally { _a.HoldStates.TrySetResult(); }
        Assert.NotNull(await toggle.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Empty(_a.Calls);
    }

    [Fact]
    public async Task ALostAnswerStillMakesTheNextToggleReadTheState()
    {
        Configure(_a);
        var ha = Client();
        _a.AbortServiceAnswers = true;
        Assert.NotNull(await ha.ToggleAsync("lock.door").WaitAsync(TimeSpan.FromSeconds(15))); // applied, answer lost
        _a.AbortServiceAnswers = false;
        Assert.Null(await ha.ToggleAsync("lock.door").WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(["/api/services/lock/unlock", "/api/services/lock/lock"], _a.Calls);
    }

    [Fact]
    public async Task ALockToggleReadsTheStateAgainInsteadOfTheWidgetCache()
    {
        Configure(_a);
        var ha = Client();
        await ha.StatesAsync(["lock.door"]).WaitAsync(TimeSpan.FromSeconds(15)); // the widget poll caches "locked"
        _a.Door = "unlocked";                                                     // unlocked by hand right after
        Assert.Null(await ha.ToggleAsync("lock.door").WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(["/api/services/lock/lock"], _a.Calls);
    }

    [Fact]
    public async Task ALockToggleWithAnUnknownStateCallsNothing()
    {
        Configure(_a);
        _a.Door = "unknown";
        Assert.Equal("Home Assistant does not report this entity's state",
            await Client().ToggleAsync("lock.door").WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Empty(_a.Calls);
    }

    [Fact]
    public async Task ALockToggleUsesTheStateOfTheServerItCalls()
    {
        _b.Door = "unlocked";
        Configure(_a);
        var ha = Client();
        await ha.StatesAsync(["lock.door"]).WaitAsync(TimeSpan.FromSeconds(15)); // caches the first server's "locked"
        Configure(_b);
        Assert.Null(await ha.ToggleAsync("lock.door").WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Empty(_a.Calls);
        Assert.Equal(["/api/services/lock/lock"], _b.Calls);
    }

    [Fact]
    public async Task AFailedReadForNewSettingsShowsNoStateFromTheOldServer()
    {
        Configure(_a);
        var ha = Client();
        var before = JsonSerializer.SerializeToElement(await ha.StatesAsync(["sensor.temp"]).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("21.43", before.GetProperty("entities")[0].GetProperty("State").GetString());
        Configure(_b, token: "rejected"); // the new server refuses this token
        var after = JsonSerializer.SerializeToElement(await ha.StatesAsync(["sensor.temp"]).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("error").ValueKind);
        Assert.Equal("unknown", after.GetProperty("entities")[0].GetProperty("State").GetString());
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A Home Assistant REST endpoint with one token, a lock, a vacuum and a sensor.</summary>
    private sealed class FakeHomeAssistant : IDisposable
    {
        private readonly HttpListener _server = TestPorts.Listen();
        private readonly List<string> _calls = [];
        private readonly Task _loop;
        public string Token { get; }
        public string Url => _server.Prefixes.Single().TrimEnd('/');
        public volatile string Door = "locked";
        public volatile string Vacuum = "docked";
        public volatile bool FailStates;
        public volatile bool AbortServiceAnswers;
        public TaskCompletionSource? HoldStates;
        public readonly TaskCompletionSource StatesEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string[] Calls { get { lock (_calls) return _calls.ToArray(); } }

        public FakeHomeAssistant(string token)
        {
            Token = token;
            _loop = Task.Run(Serve);
        }

        private async Task Serve()
        {
            while (_server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _server.GetContextAsync(); }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }
                _ = Task.Run(() => Answer(ctx));
            }
        }

        private async Task Answer(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url!.AbsolutePath;
                string body;
                if (ctx.Request.Headers["Authorization"] != "Bearer " + Token) { ctx.Response.StatusCode = 401; body = "{}"; }
                else if (path == "/api/states")
                {
                    StatesEntered.TrySetResult();
                    if (HoldStates is { } hold) await hold.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    if (FailStates) { ctx.Response.StatusCode = 500; body = "{}"; }
                    else body = JsonSerializer.Serialize(new object[]
                    {
                        new { entity_id = "lock.door", state = Door, attributes = new { friendly_name = "Door" } },
                        new { entity_id = "vacuum.robot", state = Vacuum, attributes = new { friendly_name = "Robot" } },
                        new { entity_id = "sensor.temp", state = "21.43", attributes = new { friendly_name = "Office" } },
                    });
                }
                else
                {
                    lock (_calls) _calls.Add(path);
                    if (path == "/api/services/lock/unlock") Door = "unlocked";
                    else if (path == "/api/services/lock/lock") Door = "locked";
                    // the call was applied but the answer is a failure: the same on every platform, unlike
                    // HttpListener.Abort(), which can still send a default 200 on Linux and macOS
                    if (AbortServiceAnswers) { ctx.Response.StatusCode = 504; body = "{}"; }
                    else body = "[]";
                }
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            HoldStates?.TrySetResult();
            _server.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        }
    }
}
