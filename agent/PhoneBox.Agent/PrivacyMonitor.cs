namespace PhoneBox.Agent;

/// <param name="Mic">Whether an app is using a microphone; null where this cannot be detected.</param>
public sealed record PrivacySnapshot(bool? Mic, bool? Camera, IReadOnlyList<string> MicApps, IReadOnlyList<string> CameraApps);

/// <summary>
/// Is the microphone or the camera in use, and by which app? For the "On air" widget and rules.
///  - Windows: recording sessions on the microphones (Core Audio) and the camera streams Media Foundation reports.
///    The privacy consent store (behind the taskbar's microphone and camera icons) is the fallback where those can't
///    be read: Windows 11 stopped updating it in late 2026 builds (26200 with the October updates, 26300).
///  - Linux: ALSA capture streams that are running, and processes with a /dev/video* device open.
///  - macOS: what the menu bar's microphone and camera dots show, from CoreAudio and CoreMediaIO (see Unix/MacPrivacyProbe.cs).
/// </summary>
public sealed class PrivacyMonitor(ILogger<PrivacyMonitor> log) : BackgroundService
{
    private volatile PrivacySnapshot? _snap;

    public PrivacySnapshot? Snapshot() => _snap;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string? lastLog = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (mic, cam) = Read();
                _snap = new(mic.Count > 0, cam.Count > 0, mic, cam);
                var line = $"mic: {(mic.Count > 0 ? string.Join(", ", mic) : "off")}, camera: {(cam.Count > 0 ? string.Join(", ", cam) : "off")}";
                if (line != lastLog) { if (lastLog is not null) log.LogInformation("{State}", line); lastLog = line; }
            }
            catch (Exception ex) { log.LogDebug("privacy: {Msg}", ex.Message); }
            await Task.Delay(2000, ct);
        }
    }

    private static (List<string> Mic, List<string> Camera) Read()
    {
#if WINDOWS
        var mic = CaptureSessions();
        var camera = CameraActivity.Streaming();
        if (mic is not null && camera is not null) return (mic, camera);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in System.Diagnostics.Process.GetProcesses()) using (p) { try { running.Add(p.ProcessName); } catch { } }
        return (mic ?? ConsentStore("microphone", running), camera ?? ConsentStore("webcam", running));
#else
        return OperatingSystem.IsMacOS() ? MacPrivacyProbe.Read() : (LinuxMic(), LinuxCamera());
#endif
    }

    /// <summary>The session's own name, or the process name; "Windows" for the system's own sessions.</summary>
    internal static string SessionName(int pid, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith('@')) return displayName.Trim();
        if (pid == 0) return "Windows";
        return ProcessName(pid) ?? "an app";
    }

    internal static List<string> Distinct(IEnumerable<string> apps) =>
        apps.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    internal static string? ProcessName(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName; } catch { return null; }
    }

#if WINDOWS
    /// <summary>
    /// Apps with a recording session that is running (not just open) on any active microphone; null when Core Audio
    /// can't be read.
    /// </summary>
    private static List<string>? CaptureSessions()
    {
        var apps = new List<string>();
        try
        {
            using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var all = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active);
            foreach (var d in all)
            using (d)
            {
                try
                {
                    var list = d.AudioSessionManager.Sessions;
                    for (var i = 0; i < list.Count; i++)
                    using (var s = list[i])
                    {
                        try
                        {
                            if (s.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                            apps.Add(SessionName((int)s.GetProcessID, s.DisplayName));
                        }
                        catch (System.Runtime.InteropServices.COMException) { /* the session ended meanwhile */ }
                    }
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException) { return null; }
        return Distinct(apps);
    }

    /// <summary>
    /// Apps whose last use of the capability has started but not stopped. An app that crashed mid-call leaves such an
    /// entry behind, so desktop apps must still be running, and store apps must have started within 12 hours.
    /// </summary>
    private static List<string> ConsentStore(string capability, HashSet<string> running)
    {
        var staleBefore = DateTime.Now.AddHours(-12).ToFileTime();
        var apps = new List<string>();
        using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{capability}");
        if (root is null) return apps;
        void Scan(Microsoft.Win32.RegistryKey key, bool desktopApps)
        {
            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                if (sub is null) continue;
                if (name == "NonPackaged") { Scan(sub, true); continue; }
                if (sub.GetValue("LastUsedTimeStop") is not long stop || stop != 0 || sub.GetValue("LastUsedTimeStart") is not long start || start <= 0) continue;
                if (desktopApps ? !running.Contains(FriendlyExe(name)) : start < staleBefore) continue;
                apps.Add(desktopApps ? FriendlyExe(name) : FriendlyPackage(name));
            }
        }
        Scan(root, false);
        return apps.Distinct().ToList();
    }

    // "C:#Program Files#Zoom#bin#Zoom.exe" -> "Zoom"
    private static string FriendlyExe(string key) => Path.GetFileNameWithoutExtension(key.Replace('#', '\\'));

    // "Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "WindowsCamera"
    private static string FriendlyPackage(string key)
    {
        var name = key.Split('_')[0];
        return name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
    }
#else
    /// <summary>Running ALSA capture substreams (PipeWire / PulseAudio open them while something records).</summary>
    private static List<string> LinuxMic()
    {
        var apps = new List<string>();
        if (!Directory.Exists("/proc/asound")) return apps;
        foreach (var status in Directory.EnumerateFiles("/proc/asound", "status", SearchOption.AllDirectories))
        {
            if (!status.Contains("c/sub")) continue; // pcmNc = capture
            var text = Sh.ReadFile(status);
            if (text is null || !text.Contains("state: RUNNING")) continue;
            var pid = text.Split('\n').FirstOrDefault(l => l.StartsWith("owner_pid"))?.Split(':').LastOrDefault()?.Trim();
            apps.Add(pid is not null && Sh.ReadFile($"/proc/{pid}/comm")?.Trim() is { Length: > 0 } comm ? comm : "microphone");
        }
        return apps.Distinct().ToList();
    }

    /// <summary>Processes (of this user) with a video device open.</summary>
    private static List<string> LinuxCamera()
    {
        var apps = new List<string>();
        if (!Directory.EnumerateFiles("/dev", "video*").Any()) return apps;
        foreach (var proc in Directory.EnumerateDirectories("/proc"))
        {
            var pid = Path.GetFileName(proc);
            if (!pid.All(char.IsDigit)) continue;
            try
            {
                foreach (var fd in Directory.EnumerateFiles(Path.Combine(proc, "fd")))
                {
                    if (new FileInfo(fd).LinkTarget?.StartsWith("/dev/video") != true) continue;
                    apps.Add(Sh.ReadFile(Path.Combine(proc, "comm"))?.Trim() ?? pid);
                    break;
                }
            }
            catch { /* another user's process */ }
        }
        return apps.Distinct().ToList();
    }
#endif
}
