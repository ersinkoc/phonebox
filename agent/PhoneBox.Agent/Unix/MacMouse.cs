using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>
/// The Trackpad widget on macOS: moves the pointer, clicks, drags and scrolls by posting Quartz events in-process, so no
/// helper has to start for every finger movement. Like sending keys, posting events needs the Accessibility permission.
/// </summary>
internal static class MacMouse
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    private const uint LeftDown = 1, LeftUp = 2, RightDown = 3, RightUp = 4, Moved = 5, LeftDragged = 6;
    private const uint ButtonLeft = 0, ButtonRight = 1;
    private const uint FieldClickState = 1, FieldDeltaX = 4, FieldDeltaY = 5;
    private const uint HidTap = 0;
    private const uint UnitPixel = 0;

    /// <summary>The phone counts like a Windows wheel (120 for a notch); a notch scrolls 40 pixels, so 3 units are 1 pixel.</summary>
    private const int UnitsPerPixel = 3;
    /// <summary>A second click this soon and this close to the first is a double click.</summary>
    internal const long DoubleClickMs = 500;
    internal const double DoubleClickPixels = 5;

    [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CGRect { public double X, Y, Width, Height; }

    [DllImport(CoreGraphics)] private static extern nint CGEventCreate(nint source);
    [DllImport(CoreGraphics)] private static extern CGPoint CGEventGetLocation(nint ev);
    [DllImport(CoreGraphics)] private static extern nint CGEventCreateMouseEvent(nint source, uint type, CGPoint position, uint button);
    // The variadic CGEventCreateScrollWheelEvent cannot be called safely from managed code; this one takes the same arguments.
    [DllImport(CoreGraphics)] private static extern nint CGEventCreateScrollWheelEvent2(nint source, uint units, uint wheelCount, int wheel1, int wheel2, int wheel3);
    [DllImport(CoreGraphics)] private static extern void CGEventSetIntegerValueField(nint ev, uint field, long value);
    [DllImport(CoreGraphics)] private static extern void CGEventPost(uint tap, nint ev);
    [DllImport(CoreGraphics)] private static extern int CGGetActiveDisplayList(uint max, [Out] uint[] displays, out uint count);
    [DllImport(CoreGraphics)] private static extern CGRect CGDisplayBounds(uint display);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
    [DllImport(ApplicationServices)] private static extern byte AXIsProcessTrusted();

    private static readonly object Gate = new();
    private static bool _leftDown;
    private static int _clicks;
    private static long _lastClickAt;
    private static double _lastClickX, _lastClickY;
    private static int _scrollV, _scrollH;

    /// <summary>Moves the pointer by (dx, dy) screen points, or drags while the button is held down.</summary>
    public static void Move(int dx, int dy)
    {
        lock (Gate)
        {
            Require();
            var (x, y) = Location();
            var (nx, ny) = Clamp(x + dx, y + dy, Displays());
            var ev = CGEventCreateMouseEvent(0, _leftDown ? LeftDragged : Moved, new CGPoint { X = nx, Y = ny }, ButtonLeft);
            Post(ev, e =>
            {
                CGEventSetIntegerValueField(e, FieldDeltaX, (long)Math.Round(nx - x));
                CGEventSetIntegerValueField(e, FieldDeltaY, (long)Math.Round(ny - y));
            });
        }
    }

    public static void Click()
    {
        lock (Gate)
        {
            Require();
            PressLeft(down: true);
            PressLeft(down: false);
        }
    }

    public static void Down() { lock (Gate) { Require(); if (!_leftDown) PressLeft(down: true); } }

    public static void Up() { lock (Gate) { Require(); if (_leftDown) PressLeft(down: false); } }

    public static void RightClick()
    {
        lock (Gate)
        {
            Require();
            var (x, y) = Location();
            foreach (var type in new[] { RightDown, RightUp })
                Post(CGEventCreateMouseEvent(0, type, new CGPoint { X = x, Y = y }, ButtonRight), e => CGEventSetIntegerValueField(e, FieldClickState, 1));
        }
    }

    /// <summary>Scrolls by a Windows-style delta (120 per notch); positive is up.</summary>
    public static void Scroll(int delta)
    {
        lock (Gate) { Require(); var n = Take(ref _scrollV, delta); if (n != 0) Wheel(n, 0); }
    }

    /// <summary>Scrolls sideways by a Windows-style delta; positive is to the right.</summary>
    public static void HorizontalScroll(int delta)
    {
        lock (Gate) { Require(); var n = Take(ref _scrollH, delta); if (n != 0) Wheel(0, -n); }
    }

    private static void PressLeft(bool down)
    {
        var (x, y) = Location();
        if (down)
        {
            var now = Environment.TickCount64;
            _clicks = NextClickCount(_clicks, now - _lastClickAt, Math.Sqrt((x - _lastClickX) * (x - _lastClickX) + (y - _lastClickY) * (y - _lastClickY)));
            _lastClickAt = now;
            _lastClickX = x;
            _lastClickY = y;
        }
        _leftDown = down;
        var clicks = _clicks;
        Post(CGEventCreateMouseEvent(0, down ? LeftDown : LeftUp, new CGPoint { X = x, Y = y }, ButtonLeft), e => CGEventSetIntegerValueField(e, FieldClickState, clicks));
    }

    private static void Wheel(int vertical, int horizontal) =>
        Post(CGEventCreateScrollWheelEvent2(0, UnitPixel, 2, vertical, horizontal, 0), null);

    /// <summary>Adds a Windows-style delta to what is left over and returns the whole pixels to scroll now.</summary>
    internal static int Take(ref int remainder, int delta)
    {
        remainder += delta;
        var pixels = remainder / UnitsPerPixel;
        remainder -= pixels * UnitsPerPixel;
        return pixels;
    }

    /// <summary>1 for a single click, 2 and 3 for the clicks that follow quickly in the same place, then 1 again.</summary>
    internal static int NextClickCount(int previous, long sinceLastMs, double distance) =>
        previous is >= 1 and < 3 && sinceLastMs <= DoubleClickMs && distance <= DoubleClickPixels ? previous + 1 : 1;

    /// <summary>The point of the displays closest to (x, y), so the pointer cannot leave the desktop (or enter a gap).</summary>
    internal static (double X, double Y) Clamp(double x, double y, IReadOnlyList<(double X, double Y, double Width, double Height)> displays)
    {
        if (displays.Count == 0) return (x, y);
        (double X, double Y) best = (x, y);
        var bestDistance = double.MaxValue;
        foreach (var d in displays)
        {
            var cx = Math.Clamp(x, d.X, d.X + Math.Max(d.Width - 1, 0));
            var cy = Math.Clamp(y, d.Y, d.Y + Math.Max(d.Height - 1, 0));
            var distance = (cx - x) * (cx - x) + (cy - y) * (cy - y);
            if (distance < bestDistance) { bestDistance = distance; best = (cx, cy); }
            if (distance == 0) break;
        }
        return best;
    }

    private static List<(double X, double Y, double Width, double Height)> Displays()
    {
        var ids = new uint[16];
        var list = new List<(double, double, double, double)>();
        if (CGGetActiveDisplayList((uint)ids.Length, ids, out var count) != 0) return list;
        for (var i = 0; i < count && i < ids.Length; i++)
        {
            var r = CGDisplayBounds(ids[i]);
            list.Add((r.X, r.Y, r.Width, r.Height));
        }
        return list;
    }

    private static (double X, double Y) Location()
    {
        var ev = CGEventCreate(0);
        if (ev == 0) return (0, 0);
        try { var p = CGEventGetLocation(ev); return (p.X, p.Y); }
        finally { CFRelease(ev); }
    }

    private static void Post(nint ev, Action<nint>? configure)
    {
        if (ev == 0) throw new InvalidOperationException("macOS could not create the mouse event");
        try
        {
            configure?.Invoke(ev);
            CGEventPost(HidTap, ev);
        }
        finally { CFRelease(ev); }
    }

    /// <summary>macOS drops posted events silently without the permission, so say so instead.</summary>
    private static void Require()
    {
        if (AXIsProcessTrusted() == 0) throw new InvalidOperationException(KeySender.AccessibilityHint);
    }
}
