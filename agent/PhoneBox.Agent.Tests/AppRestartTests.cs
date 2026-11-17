using PhoneBox.Agent;
using Xunit;

/// <summary>#182: under launchd or systemd the agent leaves the restart to the service manager.</summary>
public class AppRestartTests
{
    private static bool Managed(Func<string, string?> env, string? parent = null) => AppRestart.ServiceManaged(env, 4242, () => parent);

    private static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void TheSetupLoginItemAndSystemdAreServiceManagers()
    {
        Assert.True(Managed(Env(("XPC_SERVICE_NAME", "net.oxog.phonebox.agent"))));
        Assert.True(Managed(Env(("INVOCATION_ID", "5f0c1c3a9e8b4f0d"), ("SYSTEMD_EXEC_PID", "4242"))));
        Assert.True(Managed(Env(("INVOCATION_ID", "5f0c1c3a9e8b4f0d")), parent: "systemd")); // systemd before 248
    }

    [Fact]
    public void ATerminalOrTheTrayIsNot()
    {
        // macOS sets XPC_SERVICE_NAME for apps and Terminal sessions too ("0" or the app's own id)
        Assert.False(Managed(Env(("XPC_SERVICE_NAME", "0"))));
        Assert.False(Managed(Env(("XPC_SERVICE_NAME", "application.com.apple.Terminal.1234"))));
        Assert.False(Managed(Env()));
        Assert.False(Managed(Env(("INVOCATION_ID", ""))));
    }

    [Fact]
    public void AProcessThatOnlyInheritedTheUnitIsNot()
    {
        // started by hand from a shell inside a systemd unit: same INVOCATION_ID, but systemd started the shell
        Assert.False(Managed(Env(("INVOCATION_ID", "5f0c1c3a9e8b4f0d"), ("SYSTEMD_EXEC_PID", "1000"))));
        Assert.False(Managed(Env(("INVOCATION_ID", "5f0c1c3a9e8b4f0d")), parent: "bash"));
    }

    [Fact]
    public void TheRestartCodeIsAFailure() => Assert.NotEqual(0, AppRestart.ServiceRestartCode);
}

/// <summary>#179: no adb while Windows ends the session, and adb again if the shutdown was cancelled.</summary>
[Collection("adb session")] // not alongside AdbReverseTests: the window is static
public class SessionEndingTests
{
    [Fact]
    public void AdbWaitsAMinuteAfterTheSessionStartsEnding()
    {
        var now = 1_000_000L;
        var clock = AdbReverse.Clock;
        AdbReverse.Clock = () => now;
        try
        {
            Assert.False(AdbReverse.SessionEnding);
            AdbReverse.MarkSessionEnding();
            Assert.True(AdbReverse.SessionEnding);
            now += AdbReverse.EndingWindowMs - 1;
            Assert.True(AdbReverse.SessionEnding);
            // still running a minute later: the shutdown was cancelled, adb is allowed again
            now += 2;
            Assert.False(AdbReverse.SessionEnding);
        }
        finally { AdbReverse.Clock = clock; AdbReverse.ResetSessionEnding(); }
    }
}
