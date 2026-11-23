using PhoneBox.Agent;
using Xunit;

public sealed class LinuxSessionLockTests
{
    [Theory]
    [InlineData("LockedHint=yes\n", true)]
    [InlineData("LockedHint=no\n", false)]
    [InlineData("LockedHint=true\n", true)]
    [InlineData("LockedHint=false\n", false)]
    [InlineData("b true\n", true)]
    [InlineData("b false\n", false)]
    [InlineData("random output", null)]
    [InlineData(null, null)]
    public void ParseLockOutput(string? output, bool? expected)
    {
        Assert.Equal(expected, LinuxSessionLock.ParseLockState(output));
    }
}
