using System.Diagnostics;
using System.Reflection;

namespace PhoneBox.Agent;

public enum NotifierState { NotStarted, Preparing, Ready, NotAllowed, Undecided, Unavailable }

/// <summary>
/// Shows a phone's notification through "PhoneBox.app", a small program the agent builds on the Mac with Apple's Swift compiler
/// the first time it is needed (Xcode or the command line tools), so the notification carries the PhoneBox icon and the phone app's
/// icon as its picture. Nothing is downloaded or installed. Until it is ready, and wherever it cannot be built or macOS does not
/// allow it, <see cref="PostAsync"/> says false and <see cref="FallbackNotifier"/> uses osascript instead.
/// </summary>
internal sealed class MacAppNotifier : IMacNotifier, IWarmable, IDisposable
{
    internal delegate Task<(int Exit, string Output)> Run(string file, IReadOnlyList<string> args, string? stdin, TimeSpan timeout, CancellationToken ct);

    private static readonly TimeSpan PostTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(240);
    private static readonly TimeSpan AuthorizeTimeout = TimeSpan.FromSeconds(180); // the person has to answer macOS's question
    private const long RecheckEveryMs = 10 * 60_000;     // a permission question left open is asked again this often
    private const long RecheckDeniedEveryMs = 60_000;     // a refusal is looked at (never asked) this often, so allowing it in System Settings works within a minute
    private const int MaxConsecutiveFailures = 3;         // after three failed/timed-out posts in a row, stop stalling every notification and let osascript handle them

    private readonly string _dir;
    private readonly Run _run;
    private readonly Func<string?> _swiftc;
    private readonly Func<(string Source, byte[] Icon)?> _inputs;
    private readonly Func<long> _clock;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private int _started;
    private int _failures;
    private long _deniedAt;
    private volatile NotifierState _state = NotifierState.NotStarted;

    public NotifierState State => _state;

    public void Dispose() { _stop.Cancel(); _stop.Dispose(); }

    /// <summary>Starts building and asking now, when phone notifications are switched on, not at the first notification.</summary>
    public void Warm() { if (_state == NotifierState.NotStarted) StartPreparing(); }
    internal string ExecutablePath => Path.Combine(_dir, "PhoneBox.app", "Contents", "MacOS", "PhoneBoxNotifier");

    internal MacAppNotifier(string dir, Run run, Func<string?> swiftc, Func<(string Source, byte[] Icon)?> inputs, Func<long> clock, ILogger log)
    {
        _dir = dir;
        _run = run;
        _swiftc = swiftc;
        _inputs = inputs;
        _clock = clock;
        _log = log;
    }

    public static MacAppNotifier Create(string dir, ILogger log) => new(dir, RunTool, FindSwiftc, EmbeddedInputs, () => Environment.TickCount64, log);

    public async Task<bool> PostAsync(string title, string subtitle, string body, byte[]? iconPng, string? replaceId, CancellationToken ct)
    {
        switch (_state)
        {
            case NotifierState.NotStarted:
                StartPreparing();
                return false;
            case NotifierState.Undecided when _clock() - _deniedAt >= RecheckEveryMs:
                _deniedAt = _clock(); // the permission question was left open: ask again now and then, only when a notification is waiting
                StartPreparing();
                return false;
            case NotifierState.Ready:
                return await ShowAsync(title, subtitle, body, iconPng, replaceId, ct);
            case NotifierState.NotAllowed when _clock() - _deniedAt >= RecheckDeniedEveryMs:
                _deniedAt = _clock(); // looked at again now and then: the person may have allowed it in System Settings (this never asks)
                if (await RunModeAsync("--status", TimeSpan.FromSeconds(10), ct) == 0) { SetState(NotifierState.Ready); return await ShowAsync(title, subtitle, body, iconPng, replaceId, ct); }
                return false;
            default:
                return false;
        }
    }

    private async Task<bool> ShowAsync(string title, string subtitle, string body, byte[]? iconPng, string? replaceId, CancellationToken ct)
    {
        try
        {
            var (exit, _) = await _run(ExecutablePath, [], NotifierBundle.RequestJson(title, subtitle, body, iconPng, replaceId), PostTimeout, ct);
            if (exit == 0) { _failures = 0; return true; }
            if (exit == 3) { _failures = 0; _deniedAt = _clock(); SetState(NotifierState.NotAllowed); return false; }
            if (exit == 5) { _failures = 0; _deniedAt = _clock(); SetState(NotifierState.Undecided, "macOS has not been told yet whether PhoneBox may show notifications"); return false; }
            if (++_failures >= MaxConsecutiveFailures) SetState(NotifierState.Unavailable, "the PhoneBox notifier stopped answering; falling back to osascript");
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning("the PhoneBox notifier did not run: {Error}", ex.GetType().Name);
            if (++_failures >= MaxConsecutiveFailures) SetState(NotifierState.Unavailable, "the PhoneBox notifier failed repeatedly; falling back to osascript");
            return false;
        }
    }

    /// <summary>One of the program's modes ("--authorize" may show macOS's question, "--status" never does); its exit code, or -1 when it did not answer.</summary>
    private async Task<int> RunModeAsync(string mode, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            if (mode != "--authorize") return (await _run(ExecutablePath, [mode], null, timeout, ct)).Exit;
            // macOS shows its permission question only to an app that LaunchServices started: run that way, and read the answer from a file
            // (a program started straight from this process is refused without being asked)
            var result = Path.Combine(_dir, "authorize.result");
            File.Delete(result);
            await _run("/usr/bin/open", ["-W", "-n", "-a", Path.Combine(_dir, "PhoneBox.app"), "--args", "--authorize", "--result", result], null, timeout, ct);
            return File.Exists(result) && int.TryParse(File.ReadAllText(result).Trim(), out var exit) ? exit : -1;
        }
        catch (OperationCanceledException) { throw; }
        catch { return -1; }
    }

    private void StartPreparing()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await PrepareAsync(_stop.Token); }
            catch (OperationCanceledException) { }
            finally { Interlocked.Exchange(ref _started, 0); }
        });
    }

    /// <summary>Builds the program when it is missing or old, then asks macOS for permission once. Runs in the background; the notifications use osascript meanwhile.</summary>
    internal async Task PrepareAsync(CancellationToken ct)
    {
        SetState(NotifierState.Preparing);
        try
        {
            if (_inputs() is not { } inputs) { SetState(NotifierState.Unavailable, "its files are missing"); return; }
            var version = NotifierBundle.Version(inputs.Source, inputs.Icon);
            // the mode overload throws on Windows, where the tests (with a fake runner) run this too; there is no notifier to protect there
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_dir);
            else Directory.CreateDirectory(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (!NotifierBundle.DirectoryIsSafe(_dir)) { SetState(NotifierState.Unavailable, "its folder can be written by other users"); return; }
            var stamp = Path.Combine(_dir, "version");
            var built = File.Exists(ExecutablePath) && File.Exists(Path.Combine(_dir, "PhoneBox.app", "Contents", "Info.plist")) &&
                File.Exists(stamp) && File.ReadAllText(stamp).Trim() == version;
            if (!built)
            {
                if (_swiftc() is null) { SetState(NotifierState.Unavailable, "Xcode or the command line tools are not installed"); return; }
                if (!await BuildAsync(inputs.Source, inputs.Icon, version, ct)) { SetState(NotifierState.Unavailable, "it could not be built"); return; }
            }
            var exit = await RunModeAsync("--authorize", AuthorizeTimeout, ct);
            _deniedAt = _clock();
            if (exit == 0) SetState(NotifierState.Ready);
            else if (exit == 3) SetState(NotifierState.NotAllowed, "notifications are not allowed for PhoneBox in System Settings › Notifications");
            else SetState(NotifierState.Undecided, "the permission question was not answered"); // asked again later, not given up on
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { SetState(NotifierState.Unavailable, ex.GetType().Name); }
    }

    internal async Task<bool> BuildAsync(string source, byte[] icon, string version, CancellationToken ct)
    {
        var stage = Path.Combine(_dir, "build");
        var app = Path.Combine(_dir, "PhoneBox.app");
        // The whole app is made and signed in the build folder and moved into place last, with the version stamp after it: a build
        // cut short at any point leaves no stamp, so it is never taken for a finished one.
        File.Delete(Path.Combine(_dir, "version"));
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        var staged = Path.Combine(stage, "PhoneBox.app");
        var stagedExe = Path.Combine(staged, "Contents", "MacOS", "PhoneBoxNotifier");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedExe)!);
        Directory.CreateDirectory(Path.Combine(staged, "Contents", "Resources"));
        var big = Path.Combine(stage, "icon.png");
        await File.WriteAllBytesAsync(big, icon, ct);
        await File.WriteAllTextAsync(Path.Combine(stage, "main.swift"), source, ct);
        var iconset = Path.Combine(stage, "PhoneBox.iconset");
        Directory.CreateDirectory(iconset);
        foreach (var (file, pixels) in NotifierBundle.IconsetSizes)
            if ((await _run("/usr/bin/sips", ["-z", pixels.ToString(), pixels.ToString(), big, "--out", Path.Combine(iconset, file)], null, TimeSpan.FromSeconds(20), ct)).Exit != 0) return false;
        if ((await _run("/usr/bin/iconutil", ["-c", "icns", iconset, "-o", Path.Combine(staged, "Contents", "Resources", "PhoneBox.icns")], null, TimeSpan.FromSeconds(20), ct)).Exit != 0) return false;
        // through xcrun, which finds the compiler and the macOS SDK together (the compiler on its own cannot find the standard library);
        // the developer folder was checked first, so xcrun has nothing to offer to install
        if ((await _run("/usr/bin/xcrun", ["--sdk", "macosx", "swiftc", "-O", Path.Combine(stage, "main.swift"), "-o", stagedExe], null, BuildTimeout, ct)).Exit != 0) return false;
        await File.WriteAllTextAsync(Path.Combine(staged, "Contents", "Info.plist"), NotifierBundle.InfoPlist(version), ct);
        // an ad-hoc signature: Apple Silicon will not run an unsigned program, and notifications need an app with an identity
        if ((await _run("/usr/bin/codesign", ["--force", "--deep", "-s", "-", staged], null, TimeSpan.FromSeconds(30), ct)).Exit != 0) return false;
        if (Directory.Exists(app)) Directory.Delete(app, true);
        Directory.Move(staged, app);
        await File.WriteAllTextAsync(Path.Combine(_dir, "version"), version, ct);
        Directory.Delete(stage, true);
        return true;
    }

    private void SetState(NotifierState state, string? why = null)
    {
        if (_state == state) return;
        _state = state;
        if (state == NotifierState.Ready) _log.LogInformation("phone notifications now show the PhoneBox icon and the phone app's icon");
        else if (why is not null) _log.LogInformation("phone notifications use the plain macOS notification (PhoneBox icon not available: {Why})", why);
    }

    internal static string? FindSwiftc() => NotifierBundle.FindSwiftc(() => Sh.Run("/usr/bin/xcode-select", "-p")?.Trim(), File.Exists);

    internal static (string Source, byte[] Icon)? EmbeddedInputs()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var swift = assembly.GetManifestResourceStream("notifier.swift");
        using var ico = assembly.GetManifestResourceStream("phonebox.ico");
        if (swift is null || ico is null) return null;
        using var reader = new StreamReader(swift);
        using var bytes = new MemoryStream();
        ico.CopyTo(bytes);
        return NotifierBundle.LargestPng(bytes.ToArray()) is { } png ? (reader.ReadToEnd(), png) : null;
    }

    private static async Task<(int Exit, string Output)> RunTool(string file, IReadOnlyList<string> args, string? stdin, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file);
        foreach (var a in args) psi.ArgumentList.Add(a);
        var result = await ToolProcess.RunAsync(psi, timeout, ct, stdin);
        return (result.ExitCode, result.Output + result.Error);
    }
}

/// <summary>Tries the PhoneBox notifier first and the plain osascript notification when it is not ready or fails, so a notification is never lost.</summary>
internal sealed class FallbackNotifier(IMacNotifier primary, IMacNotifier fallback) : IMacNotifier, IWarmable, IDisposable
{
    public void Warm() => (primary as IWarmable)?.Warm();
    public void Dispose() { (primary as IDisposable)?.Dispose(); (fallback as IDisposable)?.Dispose(); }

    public async Task<bool> PostAsync(string title, string subtitle, string body, byte[]? iconPng, string? replaceId, CancellationToken ct)
    {
        try { if (await primary.PostAsync(title, subtitle, body, iconPng, replaceId, ct)) return true; }
        catch (OperationCanceledException) { throw; }
        catch { }
        return await fallback.PostAsync(title, subtitle, body, iconPng, replaceId, ct);
    }
}
