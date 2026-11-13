#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PhoneBox.Agent;

internal sealed partial class WindowsDeckNative
{
    private readonly Lock _hardwareGate = new();
    private IReadOnlyList<DeckMonitor> _monitors = [];
    private IReadOnlyList<DeckItem> _plans = [];
    private long _monitorAt, _plansAt;

    public IReadOnlyList<DeckMonitor> Monitors()
    {
        lock (_hardwareGate)
        {
            if (Environment.TickCount64 - _monitorAt < 30_000 && _monitors.Count > 0) return _monitors;
            _monitorAt = Environment.TickCount64;
            _monitors = Screen.AllScreens.Select(s =>
            {
                var mode = Mode(s.DeviceName);
                var brightness = Physical(s, h => GetMonitorBrightness(h, out var min, out var current, out var max) && max > min
                    ? (int?)Math.Round((current - min) * 100d / (max - min)) : null);
                return new DeckMonitor(s.DeviceName, s.DeviceName + (s.Primary ? " (primary)" : ""), brightness,
                    (int)mode.Width, (int)mode.Height, (int)mode.Frequency);
            }).ToArray();
            return _monitors;
        }
    }
    public bool Brightness(string id, int value)
    {
        if (value is < 0 or > 100) return false;
        var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == id);
        if (screen is null) return false;
        var ok = Physical(screen, h => GetMonitorBrightness(h, out var min, out _, out var max)
            && SetMonitorBrightness(h, min + (uint)Math.Round((max - min) * value / 100d)) ? (int?)1 : null) is 1;
        lock (_hardwareGate) _monitorAt = 0;
        return ok;
    }
    private static int? Physical(Screen screen, Func<nint, int?> use)
    {
        var monitor = MonitorFromPoint(new Point { X = screen.Bounds.Left + 1, Y = screen.Bounds.Top + 1 }, 2);
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count is 0 or > 16) return null;
        var items = new PhysicalMonitor[count];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, items)) return null;
        try { foreach (var item in items) if (use(item.Handle) is { } value) return value; return null; }
        finally { DestroyPhysicalMonitors(count, items); }
    }

    public IReadOnlyList<DeckItem> PowerPlans()
    {
        lock (_hardwareGate)
        {
            if (Environment.TickCount64 - _plansAt < 30_000 && _plans.Count > 0) return _plans;
            _plansAt = Environment.TickCount64;
            var output = Run("powercfg.exe", ["/list"], CancellationToken.None).GetAwaiter().GetResult();
            _plans = output.ExitCode != 0 ? [] : Regex.Matches(output.Output,
                @"(?<id>[a-fA-F0-9]{8}-(?:[a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12})\s+\((?<name>[^\r\n]+)\)(?<active>\s*\*)?")
                .Select(m => new DeckItem(m.Groups["id"].Value, m.Groups["name"].Value, "power-plan", m.Groups["active"].Success ? "Active" : "Available")).ToArray();
            return _plans;
        }
    }

    public async Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct)
    {
        switch (kind)
        {
            case "desktop": KeySender.SendKeys(value < 0 ? "Win+Ctrl+Left" : "Win+Ctrl+Right"); return true;
            case "focus": return Focus(target);
            case "window-layout": return Layout(target, argument ?? "left");
            case "display-profile": return DisplayProfile(target, argument ?? "{}");
            case "power-plan":
                if (!PowerPlans().Any(p => p.Id == target)) return false;
                var plan = await Run("powercfg.exe", ["/setactive", target], ct);
                lock (_hardwareGate) _plansAt = 0;
                return plan.ExitCode == 0;
            case "service":
                if (target.Length is 0 or > 256 || argument is not ("start" or "stop" or "restart")) return false;
                if (argument is "stop" or "restart")
                {
                    if (ServiceState(target) != 1 && (await Run("sc.exe", ["stop", target], ct)).ExitCode != 0) return false;
                    if (!await WaitService(target, 1, ct)) return false;
                    if (argument == "stop") return true;
                }
                if (ServiceState(target) != 4 && (await Run("sc.exe", ["start", target], ct)).ExitCode != 0) return false;
                return await WaitService(target, 4, ct);
            case "presentation":
                if (target is not ("POWERPNT" or "soffice")) return false;
                if (!Focus(target)) return false;
                var key = argument switch { "start" => "F5", "next" => "Right", "prev" => "Left", "end" => "Esc", "blank" => "B", _ => null };
                if (key is null) return false;
                KeySender.SendKeys(key); return true;
            case "power":
                if (target is "restart" or "shutdown") return (await Run("shutdown.exe", [target == "restart" ? "/r" : "/s", "/t", "5"], ct)).ExitCode == 0;
                if (target == "sleep") { _ = Task.Run(async () => { await Task.Delay(500); SetSuspendState(false, false, false); }); return true; }
                return false;
        }
        return false;
    }
    public string? ActionState(DeckAction action)
    {
        if (action.Kind is "sound" or "radio") { lock (_gate) return _sounds.ContainsKey(action.Id) || _hls.ContainsKey(action.Id) ? "Playing" : "Stopped"; }
        return action.Kind == "service" ? ServiceState(action.Target) switch
        { 1 => "Stopped", 2 => "Starting", 3 => "Stopping", 4 => "Running", 5 => "Resuming", 6 => "Pausing", 7 => "Paused", _ => "Service unavailable" } : null;
    }
    private static async Task<bool> WaitService(string name, int expected, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 12_000;
        do { if (ServiceState(name) == expected) return true; await Task.Delay(250, ct); } while (Environment.TickCount64 < deadline);
        return false;
    }
    private static int ServiceState(string name)
    {
        var manager = OpenSCManager(null, null, 1); if (manager == 0) return 0;
        try
        {
            var service = OpenService(manager, name, 4); if (service == 0) return 0;
            try { return QueryServiceStatus(service, out var status) ? status.State : 0; }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public int Type, State, Accepted, Win32Exit, SpecificExit, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern nint OpenSCManager(string? machine, string? database, int access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern nint OpenService(nint manager, string name, int access);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(nint handle);
    private static Task<ToolProcess.Result> Run(string exe, string[] args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return ToolProcess.RunAsync(start, TimeSpan.FromSeconds(5), ct);
    }
    private static nint Window(string process)
    {
        var session = Process.GetCurrentProcess().SessionId;
        var name = Path.GetFileNameWithoutExtension(process);
        if (name.Length == 0) return 0; // a folder: an empty name would match every process
        var processes = Process.GetProcessesByName(name);
        try { return processes.FirstOrDefault(p => p.SessionId == session && p.MainWindowHandle != 0)?.MainWindowHandle ?? 0; }
        finally { foreach (var p in processes) p.Dispose(); }
    }
    private static bool Focus(string process)
    {
        var handle = Window(process); if (handle == 0) return false;
        ShowWindow(handle, 9);
        return SetForegroundWindow(handle);
    }
    private static bool Layout(string process, string layout)
    {
        var handle = Window(process); if (handle == 0) return false;
        var screen = Screen.FromHandle(handle).WorkingArea;
        if (layout == "max") { ShowWindow(handle, 3); return true; }
        var rect = layout switch
        {
            "left" => new System.Drawing.Rectangle(screen.Left, screen.Top, screen.Width / 2, screen.Height),
            "right" => new System.Drawing.Rectangle(screen.Left + screen.Width / 2, screen.Top, screen.Width / 2, screen.Height),
            "center" => new System.Drawing.Rectangle(screen.Left + screen.Width / 8, screen.Top + screen.Height / 8, screen.Width * 3 / 4, screen.Height * 3 / 4),
            _ => System.Drawing.Rectangle.Empty,
        };
        if (rect.IsEmpty) return false;
        ShowWindow(handle, 9);
        return SetWindowPos(handle, 0, rect.X, rect.Y, rect.Width, rect.Height, 0x14);
    }
    private static bool DisplayProfile(string id, string profile)
    {
        if (!Screen.AllScreens.Any(s => s.DeviceName == id)) return false;
        using var doc = JsonDocument.Parse(profile);
        var root = doc.RootElement;
        var width = root.GetProperty("width").GetInt32(); var height = root.GetProperty("height").GetInt32();
        var hz = root.TryGetProperty("hz", out var refresh) ? refresh.GetInt32() : 0;
        if (width is < 320 or > 16384 || height is < 200 or > 16384 || hz is < 0 or > 1000) return false;
        var mode = Mode(id); mode.Width = (uint)width; mode.Height = (uint)height;
        mode.Fields = 0x80000 | 0x100000;
        if (hz > 0) { mode.Frequency = (uint)hz; mode.Fields |= 0x400000; }
        if (root.TryGetProperty("x", out var x) && root.TryGetProperty("y", out var y))
        { mode.X = Math.Clamp(x.GetInt32(), -32768, 32768); mode.Y = Math.Clamp(y.GetInt32(), -32768, 32768); mode.Fields |= 0x20; }
        return ChangeDisplaySettingsEx(id, ref mode, 0, 0, 0) == 0;
    }
    private static DisplayMode Mode(string id)
    {
        var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>(), DeviceName = "", FormName = "" };
        EnumDisplaySettingsEx(id, -1, ref mode, 0); return mode;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct PhysicalMonitor
    { public nint Handle; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra; public uint Fields;
        public int X, Y; public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels; public uint Bits, Width, Height, Flags, Frequency, IcmMethod, IcmIntent, Media, Dither, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint h, out uint count);
    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)] private static extern bool GetPhysicalMonitorsFromHMONITOR(nint h, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] private static extern bool GetMonitorBrightness(nint h, out uint min, out uint current, out uint max);
    [DllImport("dxva2.dll")] private static extern bool SetMonitorBrightness(nint h, uint brightness);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsEx(string name, int mode, ref DisplayMode settings, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string name, ref DisplayMode settings, nint window, uint flags, nint parameter);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint h, int mode);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
}
#endif
