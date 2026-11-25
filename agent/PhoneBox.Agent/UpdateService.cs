using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record UpdateStatus(string AgentVersion, string? AppVersion, string? Latest, string? ReleaseUrl,
    bool AgentUpdate, bool AppUpdate, long? LastCheck, string State, string? Error);

/// <summary>
/// Keeps the agent and the phone app up to date from GitHub releases (PhoneBox-&lt;v&gt;.apk and
/// PhoneBoxAgent-&lt;v&gt;-&lt;rid&gt;.zip, rid = win-x64, linux-x64, linux-arm64, osx-arm64 or osx-x64):
///  - phone: installed with "adb install -r" while the phone is on USB (no prompt on the phone). Android refuses
///    an APK signed with a different key, and the agent never uninstalls to force it.
///  - agent: files are swapped in place (running files are renamed to *.old, which Windows allows) and the agent
///    restarts itself. Only when running from the installed location, never from a build folder. An agent running
///    from PhoneBox.app (the macOS disk image, PhoneBox-&lt;v&gt;-&lt;rid&gt;.dmg) replaces the whole app from the new image.
/// </summary>
public sealed class UpdateService(ConfigStore cfg, AdbReverse adb, IHostApplicationLifetime life, ILogger<UpdateService> log) : BackgroundService
{
    private static readonly string InstallDir = AppPaths.InstallDir;
    private static readonly string ExeName = OperatingSystem.IsWindows() ? "PhoneBoxAgent.exe" : "PhoneBoxAgent";
    private static readonly string Downloads = Path.Combine(AppPaths.LocalDir, "updates");
    internal string DownloadsDir { get; init; } = Downloads;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Release? _latest;
    private long? _lastCheck;
    private string _state = "idle";
    private string? _error;
    // phones whose app update failed for _phoneRetryFor; kept apart from AppVersion, which any phone's request sets
    private volatile string[] _phoneRetry = [];
    private volatile string? _phoneRetryFor;

    /// <summary>Android app version reported by the Android client.</summary>
    public static volatile string? AppVersion;

    public static bool RunningInstalled => SameFolder(AppContext.BaseDirectory, InstallDir);

    /// <summary>The PhoneBox.app this agent runs from (macOS disk image installs), else null.</summary>
    internal static string? BundleDir(string baseDirectory)
    {
        var exeDir = Resolved(baseDirectory);
        if (!exeDir.EndsWith("/Contents/MacOS", StringComparison.Ordinal)) return null;
        var app = Path.GetDirectoryName(Path.GetDirectoryName(exeDir));
        return app is not null && app.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? app : null;
    }

    public static bool RunningInBundle => OperatingSystem.IsMacOS() && BundleDir(AppContext.BaseDirectory) is not null;

    /// <summary>The agent can replace itself: from the install folder (setup, zip) or as PhoneBox.app (disk image).</summary>
    public static bool CanUpdateAgent => RunningInstalled || RunningInBundle;

    /// <summary>
    /// On macOS/Linux the .NET host reports the agent's folder with symbolic links resolved, while the install folder
    /// comes from HOME / XDG_DATA_HOME and may go through one (a home folder moved to another disk and linked back), so
    /// both sides are resolved before comparing.
    /// </summary>
    internal static bool SameFolder(string a, string b) =>
        string.Equals(Resolved(a), Resolved(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Resolved(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd('\\', '/');
        if (OperatingSystem.IsWindows()) return full;
        IntPtr real;
        try { real = realpath(full, IntPtr.Zero); } // null when the folder does not exist: compare the path as it is
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return full; } // runs at startup: never fail it
        if (real == IntPtr.Zero) return full;
        try { return (Marshal.PtrToStringUTF8(real) ?? full).TrimEnd('/'); }
        finally { free(real); }
    }

    [DllImport("libc")] private static extern IntPtr realpath(string path, IntPtr resolved);
    [DllImport("libc")] private static extern void free(IntPtr pointer);

    /// <summary>A phone whose app update to the latest release failed has not had a successful retry yet.</summary>
    private bool PhoneRetryPending => _latest?.Version is { } latest && _phoneRetryFor == latest
        && _phoneRetry.Any(cfg.Get().UsesUsbDevice);

    public UpdateStatus Status(bool includeAndroid = true)
    {
        var latest = _latest?.Version;
        var appVersion = includeAndroid ? AppVersion : null;
        return new UpdateStatus(AppPaths.Version, appVersion, latest, _latest?.Url,
            latest is not null && Newer(latest, AppPaths.Version) && (RunningInBundle ? _latest!.AgentDmg : _latest!.AgentZip) is not null,
            includeAndroid && latest is not null && _latest!.Apk is not null
                && (appVersion is not null && Newer(latest, appVersion) || PhoneRetryPending),
            _lastCheck, _state, _error);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        CleanupOldFiles();
        if (RunningInBundle) CleanupBundleLeftovers(BundleDir(AppContext.BaseDirectory)!);
        if (RunningInstalled) RemoveFrameworkDependentLeftovers(InstallDir, SingleFile);
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        var nextCheck = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow >= nextCheck)
            {
                await CheckAsync(ct);
                // a failed check (GitHub answering 504 now and then) is retried soon instead of in 6 hours
                nextCheck = DateTime.UtcNow + (_error?.StartsWith("Update check failed") == true ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(6));
            }
            if (cfg.Get().Updates.Auto) await ApplyAsync(ct, automatic: true);
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    public async Task CheckAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await CheckCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    /// <summary>The check itself; the caller holds <see cref="_gate"/> (it is not reentrant).</summary>
    private async Task CheckCoreAsync(CancellationToken ct)
    {
        try
        {
            _state = "checking";
            var repo = cfg.Get().Updates.Repo;
            using var doc = JsonDocument.Parse(await ApiStringAsync($"https://api.github.com/repos/{repo}/releases/latest", ct));
            var r = doc.RootElement;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
                if (a.GetProperty("browser_download_url").GetString() is { } url && a.TryGetProperty("url", out var api) && api.GetString() is { } apiUrl)
                    _apiUrls[url] = apiUrl;
            _latest = SelectAssets(r, AppPaths.Rid);
            _lastCheck = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _error = null;
            _state = "idle";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _error = "Update check failed: " + ex.Message;
            _state = "idle";
        }
    }

    /// <summary>Installs whatever is newer: the phone app first (needs USB), then the agent (restarts it).</summary>
    /// <param name="automatic">Called by the once-a-minute loop: an app update that already failed for this release is not tried again by itself.</param>
    public async Task ApplyAsync(CancellationToken ct = default, bool automatic = false)
    {
        var s = Status();
        if (!s.AppUpdate && !s.AgentUpdate) return;
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            // the agent was just replaced and is restarting: this (old) process still sees the update as pending,
            // and a second swap would trip over its own files
            if (_state == "restarting") return;
            var attempted = false;
            var failed = false;
            if (s.AppUpdate && adb.Connected)
            {
                attempted = true;
                // a phone that refuses the app must not keep the agent itself from updating
                try { await UpdatePhoneAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    failed = true;
                    _error = ex.Message;
                    log.LogWarning("phone update failed: {Msg}", ex.Message);
                }
            }
            if (s.AgentUpdate && CanUpdateAgent && !(automatic && RunningInBundle && _bundleFailedVersion is { } failed1 && failed1 == _latest?.Version))
            {
                attempted = true;
                if (RunningInBundle) await UpdateBundleAsync(ct);
                else await UpdateAgentAsync(ct);
            }
            // A phone still waiting for its retry (unplugged during this round, so nothing was installed on it) keeps the
            // reason its update failed: the update stays pending in Status and needs that explanation next to it.
            if (attempted && !failed && !PhoneRetryPending) _error = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            log.LogWarning("update failed: {Msg}", ex.Message);
        }
        finally
        {
            if (_state != "restarting") _state = "idle";
            _gate.Release();
        }
    }

    /// <summary>
    /// Installs the phone app on the connected phones that do not have it, from the GitHub release of this agent's
    /// version (or the latest one): for agents installed on their own, e.g. with winget or Scoop.
    /// </summary>
    public async Task<string?> InstallAppAsync(CancellationToken ct = default)
    {
        var targets = adb.MissingApp.Where(cfg.Get().UsesUsbDevice).ToArray();
        if (targets.Length == 0) return "no connected phone is missing the app";
        if (!await _gate.WaitAsync(0, ct)) return "an update is already running";
        try
        {
            _state = "installing the phone app";
            if (_latest is null) { await CheckCoreAsync(ct); _state = "installing the phone app"; } // not CheckAsync: this method already holds the gate
            if (_latest?.Apk is null) return "could not find the phone app in the GitHub releases";
            var apk = await VerifiedDownloadAsync(_latest.Apk, $"PhoneBox-{_latest.Version}.apk", ct);
            var adbExe = AdbReverse.FindAdb(cfg.Get().AdbPath) ?? throw new InvalidOperationException("adb not found");
            var failed = new List<string>();
            var attempted = false;
            foreach (var serial in targets)
            {
                try
                {
                    if (!cfg.Get().UsesUsbDevice(serial)) continue;
                    attempted = true;
                    var output = await InstallAsync(adbExe, serial, apk, replace: false, ct);
                    if (!output.Contains("Success")) failed.Add($"installing on {serial} failed: {output.Trim().Split('\n').LastOrDefault()}");
                    else log.LogInformation("phone app {Version} installed on {Serial}", _latest.Version, serial);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    failed.Add($"installing on {serial} failed: {ex.Message}");
                }
            }
            return failed.Count > 0 ? string.Join("; ", failed)
                : attempted ? null : "no connected phone is missing the app";
        }
        catch (Exception ex) { return ex.Message; }
        finally
        {
            _state = "idle";
            _gate.Release();
        }
    }

    private async Task UpdatePhoneAsync(CancellationToken ct)
    {
        _state = "updating phone app";
        // decided before the download: a phone that already has the new app can report it while the APK downloads,
        // and must not turn this into a retry that skips the phones that never got it
        var appCurrent = AppVersion is not null && !Newer(_latest!.Version, AppVersion);
        var apk = await VerifiedDownloadAsync(_latest!.Apk!, $"PhoneBox-{_latest.Version}.apk", ct);
        var adbExe = AdbReverse.FindAdb(cfg.Get().AdbPath) ?? throw new InvalidOperationException("adb not found");
        var connected = adb.Devices.Split(", ", StringSplitOptions.RemoveEmptyEntries);
        var retry = _phoneRetryFor == _latest.Version ? _phoneRetry : [];
        var targets = PhoneTargets(connected, appCurrent, retry);
        var attempted = new HashSet<string>();
        var failed = new List<(string Serial, string Why)>();
        foreach (var serial in targets)
        {
            if (!cfg.Get().UsesUsbDevice(serial)) continue;
            string output;
            try
            {
                // updated another way meanwhile (by hand, or from a store): nothing to install, and no retry left (#353)
                var current = AppCurrentOn(await InstalledDumpAsync(adbExe, serial, ct), _latest.Version);
                if (!cfg.Get().UsesUsbDevice(serial)) continue;
                attempted.Add(serial);
                if (current)
                {
                    log.LogInformation("phone app on {Serial} is already {Version}", serial, _latest.Version);
                    continue;
                }
                output = await InstallAsync(adbExe, serial, apk, replace: true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                attempted.Add(serial);
                failed.Add((serial, ex.Message)); // a phone that hangs (time limit) must not cost the others their update
                continue;
            }
            if (!output.Contains("Success"))
            {
                // the next phone still gets its update (a refused one, e.g. signed with another key, keeps its app)
                failed.Add((serial, output.Trim().Split('\n').LastOrDefault() ?? ""));
                continue;
            }
            log.LogInformation("phone app on {Serial} updated to {Version}", serial, _latest.Version);
            try { await Run(adbExe, ["-s", serial, "shell", "am", "start", "-n", AdbReverse.PanelActivity], ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogInformation("{Serial}: could not open the panel after the update ({Msg})", serial, ex.Message); // updated anyway
            }
        }
        _phoneRetry = NextPhoneRetry(retry, attempted.ToArray(), failed.Select(f => f.Serial).Concat(targets.Except(attempted)).ToArray());
        _phoneRetryFor = _latest.Version;
        // a phone that failed keeps the update pending (Status), whatever version the other phones report
        if (failed.Count > 0) throw new InvalidOperationException("phone update failed on " + string.Join("; ", failed.Select(f => $"{f.Serial}: {f.Why}")));
        if (attempted.Count > 0) AppVersion = _latest.Version;
    }

    // Next to PhoneBox.app, so the swap is a rename on one volume.
    private const string BundleStaging = ".PhoneBox-update.app", BundleOld = ".PhoneBox-old.app";

    /// <summary>
    /// PhoneBox.app (macOS disk image): download the release's .dmg (checked against SHA256SUMS like every asset), copy
    /// the new PhoneBox.app out of it, check its signature, swap it for the running one and restart. The app is replaced
    /// as a whole, because a bundle's signature seals its files: swapping only the executable would break it.
    /// </summary>
    private async Task UpdateBundleAsync(CancellationToken ct)
    {
        try { await UpdateBundleCoreAsync(ct); }
        // A failure that trying again will not change (a folder that is not writable, a signature that does not match) is
        // remembered for this release: the loop that runs every minute must not download and check the image again each time.
        catch (Exception ex) when (ex is not OperationCanceledException && !Transient(ex, ct))
        {
            _bundleFailedVersion = _latest?.Version;
            throw;
        }
    }

    // the release whose app update failed for a reason that a retry does not fix; a newer release, or Install update by hand, tries again
    private volatile string? _bundleFailedVersion;

    private async Task UpdateBundleCoreAsync(CancellationToken ct)
    {
        _state = "updating agent";
        var app = BundleDir(AppContext.BaseDirectory)!;
        var parent = Path.GetDirectoryName(app)!;
        var staging = Path.Combine(parent, BundleStaging);
        // before the download: an app in a folder the user cannot write to fails at once, not after 50 MB
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        try { Directory.CreateDirectory(staging); Directory.Delete(staging); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException($"{parent} is not writable, so PhoneBox.app cannot update itself there; move it to ~/Applications or install the new version by hand");
        }
        var dmg = await VerifiedDownloadAsync(_latest!.AgentDmg!, $"PhoneBox-{_latest.Version}-{AppPaths.Rid}.dmg", ct, requireSums: true);
        try
        {
            await ExtractAppAsync(dmg, staging, ct);
            var verify = new ProcessStartInfo("/usr/bin/codesign");
            foreach (var arg in new[] { "--verify", "--deep", "--strict", staging }) verify.ArgumentList.Add(arg);
            var signed = await ToolProcess.RunAsync(verify, TimeSpan.FromMinutes(1), ct);
            if (signed.ExitCode != 0) throw new InvalidOperationException("the new PhoneBox.app's signature does not verify; the update was not installed: " + (signed.Output + signed.Error).Trim());
            // The new app must be signed by the identity of the one that runs. A valid signature alone would accept any
            // certificate (SHA256SUMS comes from the same release as the image). A copy signed ad hoc (a build from source) has
            // only a hash as its requirement, which cannot be pinned.
            var running = await DesignatedRequirementAsync(app, ct);
            if (!CodeHashOnly(running))
            {
                var pin = new ProcessStartInfo("/usr/bin/codesign");
                foreach (var arg in new[] { "--verify", "-R=" + running, staging }) pin.ArgumentList.Add(arg);
                var pinned = await ToolProcess.RunAsync(pin, TimeSpan.FromMinutes(1), ct);
                if (pinned.ExitCode != 0) throw new InvalidOperationException("the new PhoneBox.app is not signed by the same identity as the running one; the update was not installed: " + (pinned.Output + pinned.Error).Trim());
            }
            else log.LogWarning("the running PhoneBox.app is signed ad hoc (its requirement is only a code hash), so the update's signing identity cannot be checked against it");
            // The path is read now: once the running app is renamed, the OS no longer says where it lives.
            AppRestart.Executable = Environment.ProcessPath;
            SwapBundle(app, staging);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            throw;
        }
        log.LogInformation("agent updated to {Version}, restarting", _latest.Version);
        _state = "restarting";
        AppRestart.Requested = true;
        life.StopApplication();
    }

    /// <summary>The designated requirement codesign reports for a signed app or binary; throws when it cannot be read.</summary>
    internal static async Task<string> DesignatedRequirementAsync(string path, CancellationToken ct)
    {
        var start = new ProcessStartInfo("/usr/bin/codesign");
        foreach (var arg in new[] { "-d", "-r-", path }) start.ArgumentList.Add(arg);
        var result = await ToolProcess.RunAsync(start, TimeSpan.FromMinutes(1), ct);
        // A lookup that failed is not "ad hoc": it must not turn the identity check off
        if (result.ExitCode != 0 || ParseDesignatedRequirement(result.Output + "\n" + result.Error) is not { } requirement)
            throw new InvalidOperationException("could not read the running PhoneBox.app's signing requirement, so the update's signer cannot be checked and it was not installed: " + (result.Output + result.Error).Trim());
        return requirement;
    }

    internal static string? ParseDesignatedRequirement(string codesignOutput)
    {
        const string prefix = "designated => ";
        foreach (var line in codesignOutput.Split('\n'))
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line[prefix.Length..].Trim();
        return null;
    }

    /// <summary>
    /// True only for an ad hoc signature, whose whole requirement is the hash of that one build (cdhash H"..."): there is no
    /// identity to pin. Any other requirement is enforced on the update, and an empty one is refused.
    /// </summary>
    internal static bool CodeHashOnly(string? requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement)) throw new InvalidOperationException("the running PhoneBox.app has an empty signing requirement, so the update's signer cannot be checked and it was not installed");
        // the whole expression, nothing before or after: "cdhash H"..." or anchor apple generic" is not a plain hash
        return System.Text.RegularExpressions.Regex.IsMatch(requirement.Trim(), "^cdhash H\"[0-9a-fA-F]+\"$");
    }

    /// <summary>Copies PhoneBox.app out of a disk image into <paramref name="destination"/> (the image is mounted read-only and detached again).</summary>
    internal static async Task ExtractAppAsync(string dmg, string destination, CancellationToken ct)
    {
        var mount = Path.Combine(Path.GetTempPath(), "phonebox-dmg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mount);
        try
        {
            var attach = new ProcessStartInfo("/usr/bin/hdiutil");
            foreach (var arg in new[] { "attach", dmg, "-nobrowse", "-readonly", "-noverify", "-mountpoint", mount }) attach.ArgumentList.Add(arg);
            var attached = await ToolProcess.RunAsync(attach, TimeSpan.FromMinutes(2), ct);
            // "Resource temporarily unavailable" (EAGAIN): the disk image service is busy for a moment; try again
            for (var retry = 0; retry < 3 && attached.ExitCode != 0 && (attached.Output + attached.Error).Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase); retry++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                attached = await ToolProcess.RunAsync(attach, TimeSpan.FromMinutes(2), ct);
            }
            if (attached.ExitCode != 0) throw new InvalidOperationException("could not open the disk image: " + (attached.Output + attached.Error).Trim());
            try
            {
                var source = Path.Combine(mount, "PhoneBox.app");
                if (!Directory.Exists(source)) throw new InvalidOperationException("the disk image has no PhoneBox.app");
                var copy = new ProcessStartInfo("/usr/bin/ditto"); // keeps the signature, permissions and extended attributes
                copy.ArgumentList.Add(source);
                copy.ArgumentList.Add(destination);
                var copied = await ToolProcess.RunAsync(copy, TimeSpan.FromMinutes(2), ct);
                if (copied.ExitCode != 0) throw new InvalidOperationException("could not copy PhoneBox.app out of the disk image: " + (copied.Output + copied.Error).Trim());
            }
            finally
            {
                var detach = new ProcessStartInfo("/usr/bin/hdiutil");
                foreach (var arg in new[] { "detach", mount, "-force" }) detach.ArgumentList.Add(arg);
                try { await ToolProcess.RunAsync(detach, TimeSpan.FromMinutes(1), CancellationToken.None); } catch { }
            }
        }
        finally
        {
            try { Directory.Delete(mount); } catch { } // an empty folder once detached
        }
    }

    /// <summary>Puts <paramref name="staging"/> where <paramref name="app"/> is, in two renames; the old app is kept aside until the new one is in place.</summary>
    internal static void SwapBundle(string app, string staging)
    {
        var old = Path.Combine(Path.GetDirectoryName(app)!, BundleOld);
        if (Directory.Exists(old)) Directory.Delete(old, true);
        Directory.Move(app, old); // a running app can be renamed: its open files stay valid
        try { Directory.Move(staging, app); }
        catch
        {
            Directory.Move(old, app);
            throw;
        }
        try { Directory.Delete(old, true); } catch { } // the next start removes what is left
    }

    private static void CleanupBundleLeftovers(string app)
    {
        var parent = Path.GetDirectoryName(app)!;
        foreach (var name in new[] { BundleStaging, BundleOld })
            try { var path = Path.Combine(parent, name); if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private async Task UpdateAgentAsync(CancellationToken ct)
    {
        _state = "updating agent";
        var zip = await VerifiedDownloadAsync(_latest!.AgentZip!, $"PhoneBoxAgent-{_latest.Version}.zip", ct);
        var staging = Path.Combine(DownloadsDir, "agent-" + _latest.Version);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zip, staging);
        if (!File.Exists(Path.Combine(staging, ExeName))) throw new InvalidOperationException($"the agent package has no {ExeName}");

        foreach (var src in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(staging, src);
            var dst = Path.Combine(InstallDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (File.Exists(dst))
            {
                var old = dst + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(dst, old); // works even while the file is in use
            }
            File.Move(src, dst);
        }
        if (!OperatingSystem.IsWindows()) // zip files do not keep the executable bit
            File.SetUnixFileMode(Path.Combine(InstallDir, ExeName), (UnixFileMode)0b111_101_101);
        log.LogInformation("agent updated to {Version}, restarting", _latest.Version);
        _state = "restarting";
        AppRestart.Requested = true;
        life.StopApplication();
    }

    /// <summary>Release asset download links → the same assets on api.github.com (filled by <see cref="CheckAsync"/>).</summary>
    private readonly Dictionary<string, string> _apiUrls = [];

    private async Task<string> DownloadAsync(string url, string name, CancellationToken ct)
    {
        Directory.CreateDirectory(DownloadsDir);
        var path = Path.Combine(DownloadsDir, name);
        if (File.Exists(path)) return path;
        var tmp = path + ".part";
        try { await DownloadToAsync(url, null, tmp, ct); }
        catch (Exception ex) when (Transient(ex, ct) && _apiUrls.TryGetValue(url, out var api))
        {
            // github.com's download links sometimes answer 504 while the API serves the same file fine
            log.LogInformation("download of {Name} failed ({Msg}); trying the GitHub API", name, ex.Message);
            try { await DownloadToAsync(api, "application/octet-stream", tmp, ct); }
            catch (Exception ex2) when (Transient(ex2, ct))
            {
                var token = await GhTokenAsync(ct);
                if (token is null) throw;
                await DownloadToAsync(api, "application/octet-stream", tmp, ct, token);
            }
        }
        File.Move(tmp, path, true);
        return path;
    }

    /// <summary>
    /// GET on api.github.com. GitHub sometimes answers anonymous requests with 503 / 504 while it serves signed-in ones;
    /// then the request is repeated with the GitHub CLI's token, if gh is installed and signed in. The token is sent to
    /// api.github.com only (.NET drops the Authorization header when it follows a redirect to the download host).
    /// </summary>
    private async Task<string> ApiStringAsync(string url, CancellationToken ct)
    {
        try { return await _http.GetStringAsync(url, ct); }
        catch (Exception ex) when (Transient(ex, ct))
        {
            var token = await GhTokenAsync(ct);
            if (token is null) throw;
            log.LogInformation("GitHub API without a token failed ({Msg}); retrying with the gh CLI's token", ex.Message);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (IsGitHubApi(url)) req.Headers.Authorization = new("Bearer", token);
            using var res = await _http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync(ct);
        }
    }

    /// <summary>A failed request worth another route: an HTTP error or HttpClient's own timeout (not our cancellation).</summary>
    public static bool Transient(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    /// <summary>The only host the GitHub CLI's token is ever sent to.</summary>
    public static bool IsGitHubApi(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The GitHub CLI's token (gh auth token), or null when gh is missing or not signed in.</summary>
    private static async Task<string?> GhTokenAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var t = (await GitHubMonitor.Gh(["auth", "token"], cts.Token)).Trim();
            return t.Length > 0 && !t.Contains(' ') ? t : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    private async Task DownloadToAsync(string url, string? accept, string file, CancellationToken ct, string? token = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (accept is not null) { req.Headers.Accept.Clear(); req.Headers.Accept.ParseAdd(accept); }
        if (token is not null && IsGitHubApi(url)) req.Headers.Authorization = new("Bearer", token);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(file);
        await src.CopyToAsync(dst, ct);
    }

    /// <summary>Removes *.old files left behind by the previous update, plus old downloads.</summary>
    private static void CleanupOldFiles()
    {
        try
        {
            if (Directory.Exists(InstallDir))
                foreach (var f in Directory.EnumerateFiles(InstallDir, "*.old", SearchOption.AllDirectories))
                    try { File.Delete(f); } catch { }
            if (Directory.Exists(Downloads))
                foreach (var f in Directory.EnumerateFileSystemEntries(Downloads).Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)))
                    try { if (Directory.Exists(f)) Directory.Delete(f, true); else File.Delete(f); } catch { }
        }
        catch { }
    }

    /// <summary>True when this agent runs as a self-contained single file (the release builds).</summary>
#pragma warning disable IL3000 // empty in a single-file app: that is exactly what this checks
    internal static bool SingleFile => string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location);
#pragma warning restore IL3000

    /// <summary>
    /// Files of the framework-dependent agent (releases up to 0.9.6). The updater only adds and replaces files, so an
    /// install updated to a single-file release keeps them; the single-file agent never loads them.
    /// </summary>
    internal static readonly string[] FrameworkDependentFiles =
    [
        "PhoneBoxAgent.dll", "PhoneBoxAgent.deps.json", "PhoneBoxAgent.runtimeconfig.json",
        "NAudio.dll", "NAudio.Core.dll", "NAudio.Midi.dll", "NAudio.Wasapi.dll", "NAudio.WinMM.dll",
        "WinRT.Runtime.dll", "Microsoft.Windows.SDK.NET.dll", "System.Numerics.Tensors.dll",
    ];

    /// <summary>
    /// Deletes <see cref="FrameworkDependentFiles"/> from a single-file install. Does nothing for a framework-dependent
    /// agent (a source install by install.ps1 / install.sh needs them), so either kind can replace the other.
    /// </summary>
    internal static void RemoveFrameworkDependentLeftovers(string dir, bool singleFile)
    {
        if (!singleFile || !Directory.Exists(dir)) return;
        foreach (var name in FrameworkDependentFiles)
            try { File.Delete(Path.Combine(dir, name)); } catch { } // missing files are fine; File.Delete ignores them
    }

    private static bool Newer(string candidate, string current) =>
        Version.TryParse(candidate, out var a) && Version.TryParse(current.Split('-')[0], out var b) && a > b;

    /// <summary>
    /// Runs adb for the updater, bounded and reaped like every other tool (<see cref="ToolProcess"/>): an install that
    /// never returns (a stuck phone or cable) would otherwise hold the update gate until the agent restarts, and a
    /// cancelled update would leave adb running. A slow phone can take a while to install, hence the generous default.
    /// Each argument is passed on its own (ArgumentList), so a path with spaces or quotes needs no quoting.
    /// </summary>
    private static async Task<string> Run(string file, string[] args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(file);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var result = await ToolProcess.RunAsync(start, timeout ?? TimeSpan.FromMinutes(3), ct);
        return result.Output + result.Error;
    }

    internal sealed record Release(string Version, string? Url, string? Apk, string? AgentZip, string? Sums, string? AgentDmg = null);

    /// <summary>
    /// The phone app, this machine's agent zip and disk image and the checksum file of a GitHub release
    /// (PhoneBox-&lt;v&gt;.apk, PhoneBoxAgent-&lt;v&gt;-&lt;rid&gt;.zip, PhoneBox-&lt;v&gt;-&lt;rid&gt;.dmg, SHA256SUMS-&lt;v&gt;.txt, as release.yml names them).
    /// </summary>
    internal static Release SelectAssets(JsonElement release, string rid)
    {
        string? apk = null, zip = null, sums = null, dmg = null;
        foreach (var a in release.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString();
            if (name.StartsWith("PhoneBox-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) apk = url;
            if (name.StartsWith("PhoneBoxAgent-", StringComparison.OrdinalIgnoreCase) && name.EndsWith($"-{rid}.zip", StringComparison.OrdinalIgnoreCase)) zip = url;
            if (name.StartsWith("PhoneBox-", StringComparison.OrdinalIgnoreCase) && name.EndsWith($"-{rid}.dmg", StringComparison.OrdinalIgnoreCase)) dmg = url;
            if (name.StartsWith("SHA256SUMS-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) sums = url;
        }
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        return new Release(tag.TrimStart('v', 'V'), release.GetProperty("html_url").GetString(), apk, zip, sums, dmg);
    }

    /// <summary>
    /// The phones to update: all connected ones, or, when the app the phones report is already current, only those whose
    /// update failed before (AppVersion comes from whichever phone spoke last, so it cannot speak for the others).
    /// </summary>
    /// <summary>
    /// "adb install", and once more with --no-streaming when the streamed install failed on its way to the phone. A
    /// phone whose kernel cannot splice() the streamed APK (a new Android on an old kernel) answers "failed to write;
    /// splice failed: EINVAL" every time, but installs a pushed APK (#353). A refusal by the package manager
    /// (INSTALL_FAILED_..., INSTALL_PARSE_FAILED_...) is not a transfer problem and is not tried again.
    /// </summary>
    private async Task<string> InstallAsync(string adbExe, string serial, string apk, bool replace, CancellationToken ct)
    {
        string[] args = replace ? ["-s", serial, "install", "-r"] : ["-s", serial, "install"];
        using var installing = adb.Installing(); // the USB loop doesn't restart the adb server meanwhile (it would end this)
        var output = await Run(adbExe, [.. args, apk], ct);
        if (!RetryWithoutStreaming(output)) return output;
        log.LogInformation("{Serial}: the streamed install failed ({Why}); trying again without streaming", serial,
            output.Trim().Split('\n').LastOrDefault());
        return await Run(adbExe, [.. args, "--no-streaming", apk], ct);
    }

    /// <summary>"dumpsys package" for the app; empty when the phone does not answer (the install then goes ahead).</summary>
    private static async Task<string> InstalledDumpAsync(string adbExe, string serial, CancellationToken ct)
    {
        try { return await Run(adbExe, ["-s", serial, "shell", "dumpsys", "package", "net.oxog.phonebox.deskpanel"], ct, TimeSpan.FromSeconds(20)); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return ""; }
    }

    internal static bool RetryWithoutStreaming(string output) =>
        !output.Contains("Success", StringComparison.Ordinal)
        && !output.Contains("INSTALL_FAILED_", StringComparison.Ordinal)
        && !output.Contains("INSTALL_PARSE_FAILED_", StringComparison.Ordinal);

    /// <summary>The app's version in "dumpsys package" output is the latest release or newer.</summary>
    internal static bool AppCurrentOn(string dumpsys, string latest) =>
        // the whole versionName field, and only a version that compares: 0.9.25+local or 0.9.26_local installs as before
        System.Text.RegularExpressions.Regex.Match(dumpsys, @"versionName=(\S+)") is { Success: true } m
        && Version.TryParse(latest, out var l) && Version.TryParse(m.Groups[1].Value.Split('-')[0], out var i) && i >= l;

    internal static string[] PhoneTargets(string[] connected, bool appCurrent, string[] retry) =>
        appCurrent ? connected.Where(retry.Contains).ToArray() : connected;

    /// <summary>The phones still waiting for the update: earlier failures not tried now, plus the ones that failed now.</summary>
    internal static string[] NextPhoneRetry(string[] retry, string[] attempted, string[] failed) =>
        retry.Except(attempted).Union(failed).ToArray();

    /// <summary>The asset's file name: the last segment of its download link.</summary>
    internal static string AssetName(string url) => Uri.UnescapeDataString(new Uri(url).Segments[^1]);

    /// <summary>
    /// The SHA-256 that a "sha256sum" listing gives for a file ("&lt;hex&gt;  name" or "&lt;hex&gt; *name"), lower case,
    /// or null when the file is not listed or the text is not such a listing (an error page saved instead).
    /// </summary>
    internal static string? ExpectedSha256(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 66 || line[64] != ' ') continue;
            var hex = line[..64];
            if (!hex.All(char.IsAsciiHexDigit)) continue;
            var name = line[65..].TrimStart('*', ' ');
            if (name == fileName) return hex.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>True when the file's SHA-256 is the expected one (hex, any case).</summary>
    internal static bool HashMatches(string file, string expectedHex)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream)) == expectedHex.ToLowerInvariant();
    }

    /// <summary>
    /// Downloads a release asset and checks it against the release's SHA256SUMS. A copy that does not match (a damaged
    /// download, or an old partial file kept in the updates folder) is deleted and fetched once more, then refused. A
    /// release without a checksum file (none so far) is installed as before, with a warning.
    /// </summary>
    /// <param name="requireSums">Refuse an asset of a release without a checksum file instead of installing it unchecked (an app update replaces a signed app, so it never goes unchecked).</param>
    private async Task<string> VerifiedDownloadAsync(string url, string localName, CancellationToken ct, bool requireSums = false)
    {
        var release = _latest!;
        if (release.Sums is null && requireSums)
            throw new InvalidOperationException($"release {release.Version} has no SHA256SUMS file, so {localName} cannot be checked and the update was not installed");
        if (release.Sums is null)
        {
            log.LogWarning("release {Version} has no SHA256SUMS file; {Name} is installed without a checksum check", release.Version, localName);
            return await DownloadAsync(url, localName, ct);
        }
        var asset = AssetName(url);
        var sumsName = AssetName(release.Sums);
        string? expected = null;
        for (var attempt = 0; attempt < 2 && expected is null; attempt++)
        {
            var sumsPath = await DownloadAsync(release.Sums, sumsName, ct);
            expected = ExpectedSha256(await File.ReadAllTextAsync(sumsPath, ct), asset);
            if (expected is null) File.Delete(sumsPath); // not a listing, or an older copy: fetch it again
        }
        if (expected is null) throw new InvalidOperationException($"{asset} is not listed in {sumsName}; the update was not installed");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var path = await DownloadAsync(url, localName, ct);
            if (HashMatches(path, expected)) return path;
            File.Delete(path); // never reuse a copy that does not match
            log.LogWarning("{Name} does not match the release checksum; downloading it again", asset);
        }
        throw new InvalidOperationException($"{asset} does not match the release checksum; the update was not installed");
    }
}

/// <summary>Set before stopping the host to make Program start a fresh instance afterwards.</summary>
public static class AppRestart
{
    public static volatile bool Requested;

    /// <summary>
    /// Where to start the new instance, when it has to be read before the running one moves (an app update renames its
    /// bundle); null means <see cref="Environment.ProcessPath"/> as usual.
    /// </summary>
    public static volatile string? Executable;

    /// <summary>Exit code for a restart under a service manager: not 0, so launchd and systemd start the agent again.</summary>
    public const int ServiceRestartCode = 75;

    /// <summary>
    /// True when launchd (the setup.sh login item, label net.oxog.phonebox.agent) or systemd started the agent. A copy
    /// started by the agent itself doesn't survive there: launchd ends the job's whole process group when the agent
    /// exits, and a clean exit counts as "stopped" (KeepAlive SuccessfulExit=false, Restart=on-failure), so the agent
    /// stayed down after Restart or a self-update (#182). It exits with <see cref="ServiceRestartCode"/> instead.
    /// </summary>
    /// <remarks>
    /// INVOCATION_ID alone isn't proof: every process of a unit inherits it, a shell or terminal started from one too.
    /// The agent counts as the service only when systemd started it itself: SYSTEMD_EXEC_PID (systemd 248+) is its own
    /// pid, or on an older systemd its parent process is systemd.
    /// </remarks>
    internal static bool ServiceManaged(Func<string, string?> env, int pid, Func<string?> parentName) =>
        env("XPC_SERVICE_NAME") == "net.oxog.phonebox.agent"
        || (!string.IsNullOrEmpty(env("INVOCATION_ID")) && (env("SYSTEMD_EXEC_PID") is { Length: > 0 } exec
            ? exec == pid.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : parentName() == "systemd"));

    /// <summary>Linux: the parent process's name from /proc; null elsewhere or when it can't be read.</summary>
    internal static string? ParentName()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            // /proc/self/stat: "pid (comm) state ppid ..."; comm may hold spaces and parentheses, so read after the last ')'
            var stat = File.ReadAllText("/proc/self/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return File.ReadAllText($"/proc/{int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture)}/comm").Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException) { return null; }
    }

    internal static ProcessStartInfo StartInfo(string executable, string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            // only a framework-dependent agent runs under dotnet; Assembly.Location is empty in a single-file app
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(AppRestart).Assembly.GetName().Name + ".dll"));
        foreach (var arg in args.Where(a => a != "--restart")) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--restart");
        return start;
    }
}
