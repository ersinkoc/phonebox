#if !WINDOWS
using PhoneBox.Agent;
using Xunit;
using static PhoneBox.Agent.KeySender;

[CollectionDefinition("KeySender tools", DisableParallelization = true)]
public sealed class KeySenderToolsCollection { }

/// <summary>Which Linux tool presses keys and types, in what order, with fake tools in place of the real ones.</summary>
[Collection("KeySender tools")]
public sealed class KeySenderLinuxOrderTests : IDisposable
{
    private const string Help01 = "Usage: type [--delay milliseconds] [--key-delay milliseconds] [--args N]";
    private const string Help1x = "Usage: type [OPTION]... [STRINGS]...\n  -d, --key-delay=N\n  -e, --escape=BOOL";

    private readonly Func<string, string?> _find = FindTool;
    private readonly Func<bool> _wayland = OnWayland;
    private readonly Func<string, string[], TimeSpan, CancellationToken, (ToolRun, string)> _run = RunTool;
    private readonly Dictionary<string, ToolRun> _tools = [];
    private readonly List<string> _ran = [];
    private string _help = Help01;

    public KeySenderLinuxOrderTests()
    {
        YdotoolOk = false;
        FindTool = t => _tools.ContainsKey(t) ? "/usr/bin/" + t : null;
        OnWayland = () => true;
        RunTool = (tool, args, _, _) =>
        {
            if (args is ["type", "--help"]) return (ToolRun.Ok, _help);
            _ran.Add(tool + " " + string.Join(" ", args));
            return (_tools[tool], "");
        };
    }

    public void Dispose()
    {
        (FindTool, OnWayland, RunTool, YdotoolOk) = (_find, _wayland, _run, false);
    }

    private string[] Tools() => [.. _ran.Select(r => r.Split(' ')[0])];

    [Fact]
    public void GnomeWithoutYdotoolReportsFailureInsteadOfTrustingXdotool()
    {
        if (OperatingSystem.IsMacOS()) return;
        _tools["wtype"] = ToolRun.Failed;
        _tools["xdotool"] = ToolRun.Ok; // exits 0 on GNOME, having typed nothing
        Assert.False(SendCombo(["ctrl", "c"]));
        Assert.False(TryTypeText("hi", CancellationToken.None));
        Assert.Equal(["wtype", "wtype"], Tools());
    }

    [Fact]
    public void FailedWtypeFallsToYdotoolAndAFailedYdotoolEndsIt()
    {
        if (OperatingSystem.IsMacOS()) return;
        _tools["wtype"] = ToolRun.Failed;
        _tools["ydotool"] = ToolRun.Failed;
        _tools["xdotool"] = ToolRun.Ok;
        Assert.False(SendCombo(["ctrl", "c"]));
        Assert.False(TryTypeText("hi", CancellationToken.None));
        Assert.Equal(["wtype -M ctrl -k c -m ctrl", "ydotool key ctrl+c", "wtype -- hi", "ydotool type -- hi"], _ran);
    }

    [Fact]
    public void TimedOutWtypeIsNotFollowedByAnotherTool() // it may have pressed the keys already
    {
        if (OperatingSystem.IsMacOS()) return;
        _tools["wtype"] = ToolRun.TimedOut;
        _tools["ydotool"] = ToolRun.Ok;
        Assert.False(SendCombo(["ctrl", "c"]));
        Assert.False(TryTypeText("hi", CancellationToken.None));
        Assert.Equal(["wtype", "wtype"], Tools());
    }

    [Fact]
    public void XdotoolRunsOnWaylandOnlyWithoutWtypeAndYdotool()
    {
        if (OperatingSystem.IsMacOS()) return;
        _tools["xdotool"] = ToolRun.Ok;
        Assert.True(SendCombo(["ctrl", "c"]));
        Assert.True(TryTypeText("hi", CancellationToken.None));
        Assert.Equal(["xdotool", "xdotool"], Tools());
    }

    [Fact]
    public void X11UsesXdotoolFirst()
    {
        if (OperatingSystem.IsMacOS()) return;
        OnWayland = () => false;
        _tools["xdotool"] = ToolRun.Ok;
        _tools["wtype"] = ToolRun.Ok;
        Assert.True(SendCombo(["ctrl", "c"]));
        Assert.Equal(["xdotool key --clearmodifiers ctrl+c"], _ran);
    }

    [Fact]
    public void WhatYdotoolCannotDoIsSaidAndNothingElseRuns()
    {
        if (OperatingSystem.IsMacOS()) return;
        _tools["ydotool"] = ToolRun.Ok;
        _tools["xdotool"] = ToolRun.Ok;
        var e = Assert.Throws<InvalidOperationException>(() => TryTypeText("café", CancellationToken.None));
        Assert.Contains("ASCII", e.Message);
        Assert.DoesNotContain("café", e.Message);
        Assert.Throws<InvalidOperationException>(() => SendCombo(["volumeup"]));
        Assert.Empty(_ran);
    }

    [Fact]
    public void WindowsLineEndingsAreTypedAsNewLines()
    {
        if (OperatingSystem.IsMacOS()) return;
        _tools["ydotool"] = ToolRun.Ok;
        Assert.True(TryTypeText("a\r\nb", CancellationToken.None));
        Assert.Equal(["ydotool type -- a\nb"], _ran);
    }

    [Fact]
    public void Ydotool1xIsNamedNotUsedAndOnlyAYesIsRemembered()
    {
        if (OperatingSystem.IsMacOS()) return;
        _tools["ydotool"] = ToolRun.Ok;
        _help = Help1x;
        var e = Assert.Throws<InvalidOperationException>(() => SendCombo(["ctrl", "c"]));
        Assert.Contains("0.1.x", e.Message);
        _help = Help01; // e.g. /dev/uinput became ready after login
        Assert.True(SendCombo(["ctrl", "c"]));
        _help = "";
        Assert.True(SendCombo(["ctrl", "c"]));
        Assert.Equal(["ydotool", "ydotool"], Tools());
    }
}
#endif
