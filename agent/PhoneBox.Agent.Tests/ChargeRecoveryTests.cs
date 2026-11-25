using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class ChargeRecoveryTests
{
    [Theory]
    [InlineData("", "no Home Assistant switch set")]
    [InlineData("switch.a", null)]
    public async Task DisablingRecoversOnlyWhenOffWasSubmitted(string entity, string? expectedError)
    {
        await using var f = new ChargeRecoveryFixture(gateOff: false);
        f.Configure(entity, enabled: true);
        Assert.Equal(expectedError, await f.Guard.Switch(f.Settings, "off"));
        f.Configure(entity, enabled: false);
        await f.Check();
        var status = JsonSerializer.SerializeToElement(f.Guard.Status());
        Assert.Equal(expectedError is null ? "on" : null, status.GetProperty("power").GetString());
        Assert.Equal(expectedError, status.GetProperty("error").GetString());
        Assert.Equal(expectedError is null ? ["off:switch.a", "on:switch.a"] : [], f.Calls);
    }

    [Fact]
    public async Task DisablingAfterASettingsChangeRestoresTheOriginalPlug()
    {
        await using var f = new ChargeRecoveryFixture();
        f.Configure("switch.a", enabled: true);
        var off = f.Guard.Switch(f.Settings, "off");
        await f.OffEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Configure("switch.b", enabled: false);
        f.ReleaseOff.TrySetResult();
        Assert.Null(await off.WaitAsync(TimeSpan.FromSeconds(5)));
        await f.Check();
        Assert.True(f.Power("switch.a"));
        Assert.Equal(["off:switch.a", "on:switch.a"], f.Calls);
    }

    [Fact]
    public async Task RecoveryUsesTheOriginalServerAndCredential()
    {
        await using var origin = new ChargeRecoveryFixture(gateOff: false);
        await using var replacement = new ChargeRecoveryFixture(gateOff: false);
        origin.Configure("switch.a", enabled: true);
        Assert.Null(await origin.Guard.Switch(origin.Settings, "off"));
        origin.Configure("switch.a", enabled: false, endpoint: replacement.Endpoint, token: "replacement-only");
        await origin.Check();
        Assert.True(origin.Power("switch.a"));
        Assert.Empty(replacement.Calls);
        Assert.Equal(["Bearer fixture-only", "Bearer fixture-only"], origin.Authorizations);
    }

    [Fact]
    public async Task AFailedRestoreStaysPendingAndBlocksAnotherOffTarget()
    {
        await using var f = new ChargeRecoveryFixture(gateOff: false);
        f.Configure("switch.a", enabled: true);
        Assert.Null(await f.Guard.Switch(f.Settings, "off"));
        f.Configure("switch.b", enabled: true);
        f.FailNextOn();
        Assert.NotNull(await f.Guard.Switch(f.Settings, "off"));
        Assert.False(f.Power("switch.a"));
        Assert.True(f.Power("switch.b"));
        Assert.Null(await f.Guard.Switch(f.Settings, "off"));
        Assert.True(f.Power("switch.a"));
        Assert.False(f.Power("switch.b"));
        f.Configure("switch.b", enabled: false);
        await f.Check();
        Assert.True(f.Power("switch.b"));
    }

    [Fact]
    public async Task ARejectedLocalAddressDoesNotInventAnOffTarget()
    {
        await using var f = new ChargeRecoveryFixture(gateOff: false);
        f.Configure("switch.a", enabled: true, endpoint: "invalid");
        Assert.NotNull(await f.Guard.Switch(f.Settings, "off"));
        Assert.Empty(f.Calls);
        f.Configure("switch.b", enabled: true);
        Assert.Null(await f.Guard.Switch(f.Settings, "off"));
        Assert.Equal(["off:switch.b"], f.Calls);
    }

    [Fact]
    public async Task AnAmbiguousOffResponseStillRequiresRecovery()
    {
        await using var f = new ChargeRecoveryFixture(gateOff: false) { FailOffResponse = true };
        f.Configure("switch.a", enabled: true);
        Assert.NotNull(await f.Guard.Switch(f.Settings, "off"));
        f.Configure("switch.b", enabled: false);
        await f.Check();
        Assert.True(f.Power("switch.a"));
        Assert.Equal(["off:switch.a", "on:switch.a"], f.Calls);
    }

    [Fact]
    public async Task AQueuedCheckRecoversTheOffWhoseResponseCompletesAfterReplacement()
    {
        await using var f = new ChargeRecoveryFixture();
        f.Configure("switch.a", enabled: true);
        var off = f.Guard.Switch(f.Settings, "off");
        await f.OffEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Configure("switch.b", enabled: false);
        var check = f.Check();
        f.ReleaseOff.TrySetResult();
        Assert.Null(await off.WaitAsync(TimeSpan.FromSeconds(5)));
        await check;
        Assert.True(f.Power("switch.a"));
        Assert.Equal(["off:switch.a", "on:switch.a"], f.Calls);
    }

    [Fact]
    public async Task ConcurrentOffTargetsRecoverTheOldSupplyBeforeTakingAnother()
    {
        await using var f = new ChargeRecoveryFixture();
        f.Configure("switch.a", enabled: true);
        var first = f.Guard.Switch(f.Settings, "off");
        await f.OffEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Configure("switch.b", enabled: true);
        var second = f.Guard.Switch(f.Settings, "off");
        f.ReleaseOff.TrySetResult();
        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(f.Power("switch.a"));
        Assert.False(f.Power("switch.b"));
        Assert.Equal(["off:switch.a", "on:switch.a", "off:switch.b"], f.Calls);
    }

    [Fact]
    public async Task RotatedCredentialsCanRestoreTheSameSupply()
    {
        await using var f = new ChargeRecoveryFixture(gateOff: false);
        f.Configure("switch.a", enabled: true);
        Assert.Null(await f.Guard.Switch(f.Settings, "off"));
        f.Configure("switch.a", enabled: false, token: "rotated-fixture-only");
        await f.Check();
        Assert.True(f.Power("switch.a"));
        Assert.Equal("Bearer rotated-fixture-only", f.Authorizations.Last());
        var before = f.Calls.Length;
        await f.Check();
        Assert.Equal(before, f.Calls.Length);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RemovingTheConnectionTokenDoesNotLeaveTheOwnedSupplyOff(bool enabled, bool manual)
    {
        await using var f = new ChargeRecoveryFixture(gateOff: false) { RequiredAuthorization = "Bearer fixture-only" };
        f.Configure("switch.a", enabled: true);
        Assert.Null(await f.Guard.Switch(f.Settings, "off"));
        Assert.False(f.Power("switch.a"));

        f.Configure("switch.a", enabled, token: "");
        if (manual) Assert.Null(await f.Guard.Switch(f.Settings, "on"));
        else await f.Check();

        Assert.Equal(["off:switch.a", "on:switch.a"], f.Calls);
        Assert.True(f.Power("switch.a"));
        Assert.Equal(["Bearer fixture-only", "Bearer fixture-only"], f.Authorizations);
    }

    [Theory]
    [InlineData(60_000, null, null)]
    [InlineData(60_000, "off", "on")]
    [InlineData(0, null, "off")]
    [InlineData(-60_000, "off", null)]
    [InlineData(-600_000, null, "off")]
    [InlineData(-600_001, "off", "on")]
    public void AReportFromBeforeAClockRollbackCannotDisableTheSupply(long offset, string? last, string? expected)
    {
        const long now = 2_000_000;
        var c = new ChargeConfig { Min = 50, Max = 80 };
        var reading = new PhoneBatteryReport(95, true, null, now + offset);
        Assert.Equal(expected, ChargeGuard.Decide(c, reading, now, last, now - 10_000));
    }

    [Fact]
    public async Task RecoveryDoesNotCarryTheOldSupplyStateIntoTheReplacement()
    {
        await using var f = new ChargeRecoveryFixture(gateOff: false);
        f.Configure("switch.a", enabled: true);
        Assert.Null(await f.Guard.Switch(f.Settings, "off"));
        f.Configure("switch.b", enabled: false);
        await f.Check();
        f.Configure("switch.b", enabled: true);
        var status = JsonSerializer.SerializeToElement(f.Guard.Status());
        const long now = 1_000_000_000;
        Assert.Equal("on", ChargeGuard.Decide(f.Settings, new PhoneBatteryReport(10, false, null, now),
            now, status.GetProperty("power").GetString(), now));
    }
}

// The fake applies off(A), then holds its response until the settings replacement is complete.
// Only the production guard/config/HTTP path runs; no phone reports or user services are modified.
public sealed class ChargeRecoveryFixture : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-charge-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly HttpListener _server = TestPorts.Listen();
    private readonly ConfigStore _store;
    private readonly Task _serve;
    private readonly Dictionary<string, bool> _power = new() { ["switch.a"] = true, ["switch.b"] = true };
    private readonly List<string> _calls = [];
    private readonly List<string> _authorizations = [];
    private int _failOn;
    public volatile bool FailOffResponse;
    public string? RequiredAuthorization { get; init; }
    private readonly System.Threading.Timer _pushTimer;
    private readonly AudioControl _audio = new();
    public readonly TaskCompletionSource OffEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource ReleaseOff = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ChargeGuard Guard { get; }
    public string Endpoint => _server.Prefixes.Single().TrimEnd('/');
    public ChargeConfig Settings => _store.Get().Charge;
    public string[] Calls { get { lock (_calls) return _calls.ToArray(); } }
    public string[] Authorizations { get { lock (_authorizations) return _authorizations.ToArray(); } }
    public bool Power(string id) { lock (_power) return _power.GetValueOrDefault(id, true); }
    public void FailNextOn() => Interlocked.Exchange(ref _failOn, 1);

    public ChargeRecoveryFixture(bool gateOff = true)
    {
        Directory.CreateDirectory(_dir);
        _store = new ConfigStore(Path.Combine(_dir, "config.json"));
        var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "data.json"));
        _pushTimer = (System.Threading.Timer)typeof(PushStore).GetField("_saveTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(push)!;
        Guard = new ChargeGuard(_store, new HomeAssistant(_store, NullLogger<HomeAssistant>.Instance), push, _audio, NullLogger<ChargeGuard>.Instance);
        if (!gateOff) ReleaseOff.TrySetResult();
        _serve = Serve();
    }

    public void Configure(string entity, bool enabled, string? endpoint = null, string token = "fixture-only")
    {
        var error = _store.Save(JsonSerializer.Serialize(new {
            homeAssistant = new { url = endpoint ?? Endpoint, token },
            charge = new { enabled, method = "ha", entity },
        }));
        if (error is not null) throw new Exception(error);
    }

    public Task Check() => ((Task)typeof(ChargeGuard).GetMethod("Check", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Guard, null)!)
        .WaitAsync(TimeSpan.FromSeconds(8));

    private async Task Serve()
    {
        while (_server.IsListening)
        {
            HttpListenerContext context;
            try { context = await _server.GetContextAsync(); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            using var reader = new StreamReader(context.Request.InputStream);
            using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
            var id = body.RootElement.GetProperty("entity_id").GetString()!;
            var on = context.Request.Url!.AbsolutePath.EndsWith("turn_on", StringComparison.Ordinal);
            lock (_calls) _calls.Add((on ? "on:" : "off:") + id);
            var authorization = context.Request.Headers["Authorization"] ?? "";
            lock (_authorizations) _authorizations.Add(authorization);
            if (RequiredAuthorization is { } required && authorization != required)
            {
                context.Response.StatusCode = 401;
                context.Response.Close();
                continue;
            }
            var fail = on && Volatile.Read(ref _failOn) > 0;
            if (fail) Interlocked.Decrement(ref _failOn);
            if (!fail) lock (_power) _power[id] = on;
            if (!on) { OffEntered.TrySetResult(); await ReleaseOff.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
            context.Response.StatusCode = fail || (!on && FailOffResponse) ? 500 : 200;
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("[]"));
            context.Response.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        ReleaseOff.TrySetResult();
        _server.Close();
        await _serve.WaitAsync(TimeSpan.FromSeconds(5));
        await _pushTimer.DisposeAsync();
        Guard.Dispose();
        Directory.Delete(_dir, recursive: true);
    }
}
