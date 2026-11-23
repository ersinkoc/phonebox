using PhoneBox.Agent;
using Xunit;

/// <summary>When the USB loop restarts a hung adb server, and how it finds the server's process.</summary>
public sealed class AdbWatchdogTests
{
    [Fact]
    public void TheAdbServerIsRestartedAfterAMinuteOfSilenceAndNotAgainRightAway()
    {
        var dog = new AdbWatchdog(silentMs: 60_000, cooldownMs: 300_000);
        Assert.False(dog.Silent(0));
        Assert.False(dog.Silent(40_000));
        Assert.True(dog.Silent(60_000));        // a minute without an answer
        Assert.False(dog.Silent(80_000));
        Assert.False(dog.Silent(200_000));      // still hung, but restarted less than 5 minutes ago
        Assert.True(dog.Silent(360_000));

        var answered = new AdbWatchdog(silentMs: 60_000, cooldownMs: 300_000);
        Assert.False(answered.Silent(0));
        answered.Answered();                    // one answer in between: the minute starts over
        Assert.False(answered.Silent(50_000));
        Assert.False(answered.Silent(100_000));
        Assert.True(answered.Silent(110_000));
    }

    [Fact]
    public void TheAdbServerProcessIsFoundInNetstatInAnyLanguage()
    {
        const string english = """

            Active Connections

              Proto  Local Address          Foreign Address        State           PID
              TCP    127.0.0.1:5037         127.0.0.1:50211        ESTABLISHED     4242
              TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1180
              TCP    127.0.0.1:5037         0.0.0.0:0              LISTENING       25760
            """;
        Assert.Equal(25760, AdbReverse.ListenerFromNetstat(english, 5037));
        Assert.Equal(1180, AdbReverse.ListenerFromNetstat(english, 135));
        Assert.Equal(3344, AdbReverse.ListenerFromNetstat("  TCP    127.0.0.1:5037         0.0.0.0:0              ABHÖREN         3344\r\n", 5037));
        Assert.Equal(77, AdbReverse.ListenerFromNetstat("  TCP    [::1]:5037             [::]:0                 LISTENING       77", 5037));
        Assert.Null(AdbReverse.ListenerFromNetstat("  TCP    127.0.0.1:15037        0.0.0.0:0              LISTENING       9", 5037));
        Assert.Null(AdbReverse.ListenerFromNetstat(english.Replace("25760", "x"), 5037));
    }
}
