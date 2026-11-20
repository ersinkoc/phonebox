using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

/// <summary>The parts of the macOS mouse that do not need a screen or the Accessibility permission.</summary>
public class MacMouseTests
{
    private static readonly (double X, double Y, double Width, double Height) Main = (0, 0, 1440, 900);
    private static readonly (double X, double Y, double Width, double Height) Right = (1440, -180, 1920, 1080);

    [Fact]
    public void PointerStaysOnTheDesktop()
    {
        Assert.Equal((0.0, 0.0), MacMouse.Clamp(-30, -5, [Main]));
        Assert.Equal((1439.0, 899.0), MacMouse.Clamp(2000, 1200, [Main]));
        Assert.Equal((700.0, 300.0), MacMouse.Clamp(700, 300, [Main]));
    }

    [Fact]
    public void PointerCrossesBetweenDisplaysAndNotIntoTheGapAboveOne()
    {
        // inside the second display, which starts higher than the first
        Assert.Equal((1500.0, -100.0), MacMouse.Clamp(1500, -100, [Main, Right]));
        // above the first display, left of the second: the nearest point is on the first
        Assert.Equal((1000.0, 0.0), MacMouse.Clamp(1000, -50, [Main, Right]));
        // on the edge between the two displays the point belongs to the second
        Assert.Equal((1440.0, 880.0), MacMouse.Clamp(1440, 880, [Main, Right]));
    }

    [Fact]
    public void WithoutDisplaysThePointIsLeftAlone() =>
        Assert.Equal((12.0, -4.0), MacMouse.Clamp(12, -4, []));

    [Fact]
    public void ClicksInTheSamePlaceCountUpToThreeThenStartOver()
    {
        var count = MacMouse.NextClickCount(0, 10_000, 0);
        Assert.Equal(1, count);
        count = MacMouse.NextClickCount(count, 200, 1);
        Assert.Equal(2, count);
        count = MacMouse.NextClickCount(count, 200, 1);
        Assert.Equal(3, count);
        Assert.Equal(1, MacMouse.NextClickCount(count, 200, 1));
    }

    [Fact]
    public void ASlowOrDistantSecondClickIsASingleClick()
    {
        Assert.Equal(1, MacMouse.NextClickCount(1, MacMouse.DoubleClickMs + 1, 0));
        Assert.Equal(1, MacMouse.NextClickCount(1, 100, MacMouse.DoubleClickPixels + 1));
    }

    [Fact]
    public void ScrollKeepsTheFractionOfAPixelForTheNextMovement()
    {
        var rest = 0;
        // a full notch (120) is 40 pixels; 100 is 33 and a third: the third carries over
        Assert.Equal(40, MacMouse.Take(ref rest, 120));
        Assert.Equal(33, MacMouse.Take(ref rest, 100));
        Assert.Equal(34, MacMouse.Take(ref rest, 101));
        Assert.Equal(-40, MacMouse.Take(ref rest, -120));
        // a tiny movement adds up instead of being lost
        rest = 0;
        Assert.Equal(0, MacMouse.Take(ref rest, 1));
        Assert.Equal(0, MacMouse.Take(ref rest, 1));
        Assert.Equal(1, MacMouse.Take(ref rest, 1));
    }
}
