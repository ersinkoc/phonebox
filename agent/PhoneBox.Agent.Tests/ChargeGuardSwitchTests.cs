using PhoneBox.Agent;
using Xunit;

/// <summary>The charge guard's desktop test switch and its recovery of a supply it still tracks as off.</summary>
public sealed class ChargeGuardSwitchTests
{
    [Fact]
    public async Task AQueuedTestDoesNotSwitchASupplyTheSettingsReplaced()
    {
        await using var f = new ChargeRecoveryFixture();
        f.Configure("switch.a", enabled: true);
        var first = f.Guard.Switch(f.Settings, "off");
        await f.OffEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = f.Guard.Switch(f.Settings, "off"); // asked for switch.a, waits behind the first switch
        f.Configure("switch.b", enabled: true);
        f.ReleaseOff.TrySetResult();
        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(await queued.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["off:switch.a"], f.Calls);
        Assert.True(f.Power("switch.b"));
    }

    [Fact]
    public async Task ATestOnForAnotherSupplyDoesNotHideTheTrackedOffSupply()
    {
        // No battery report reaches the guard in this test, so only the "lost the phone" rule can switch power back on.
        await using var f = new ChargeRecoveryFixture(gateOff: false);
        f.Configure("switch.a", enabled: true);
        Assert.Null(await f.Guard.Switch(f.Settings, "off"));
        f.Configure("switch.b", enabled: true);
        Assert.Null(await f.Guard.Switch(f.Settings, "on"));
        f.Configure("switch.a", enabled: true);
        await f.Check();
        Assert.True(f.Power("switch.a"));
        Assert.Equal(["off:switch.a", "on:switch.b", "on:switch.a"], f.Calls);
    }
}
