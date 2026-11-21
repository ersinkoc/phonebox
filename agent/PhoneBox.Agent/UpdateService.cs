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
///    restarts itself. Only when running from the installed location, never from a build folder.
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

    /// <summary>Phone app version, reported by the app on every request (X-PhoneBox-App header).</summary>
    public static volatile string? AppVersion;

    public static bool RunningInstalled => SameFolder(AppContext.BaseDirectory, InstallDir);

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
    private bool PhoneRetryPending => _latest?.Version is { } latest && _phoneRetryFor == latest && _phoneRetry.Length > 0;

    public UpdateStatus Status()
    {
        var latest = _latest?.Version;
        var retry = PhoneRetryPending;
        return new UpdateStatus(AppPaths.Version, AppVersion, latest, _latest?.Url,
            latest is not null && Newer(latest, AppPaths.Version) && _latest!.AgentZip is not null,
            latest is not null && _latest!.Apk is not null && (AppVersion is not null && Newer(latest, AppVersion) || retry),
            _lastCheck, _state, _error);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        CleanupOldFiles();
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
            if (cfg.Get().Updates.Auto) await ApplyAsync(ct);
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
    public async Task ApplyAsync(CancellationToken ct = default)
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
            if (s.AgentUpdate && RunningInstalled)
            {
                attempted = true;
                await UpdateAgentAsync(ct);
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
        var targets = adb.MissingApp;
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
            foreach (var serial in targets)
            {
                try
                {
                    var output = await InstallAsync(adbExe, serial, apk, replace: false, ct);
                    if (!output.Contains("Success")) failed.Add($"installing on {serial} failed: {output.Trim().Split('\n').LastOrDefault()}");
                    else log.LogInformation("phone app {Version} installed on {Serial}", _latest.Version, serial);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    failed.Add($"installing on {serial} failed: {ex.Message}");
                }
            }
            return failed.Count > 0 ? string.Join("; ", failed) : null;
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
        var failed = new List<(string Serial, string Why)>();
        foreach (var serial in targets)
        {
            string output;
            try
            {
                // updated another way meanwhile (by hand, or from a store): nothing to install, and no retry left (#353)
                if (AppCurrentOn(await InstalledDumpAsync(adbExe, serial, ct), _latest.Version))
                {
                    log.LogInformation("phone app on {Serial} is already {Version}", serial, _latest.Version);
                    continue;
                }
                output = await InstallAsync(adbExe, serial, apk, replace: true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
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
        _phoneRetry = NextPhoneRetry(retry, targets, failed.Select(f => f.Serial).ToArray());
        _phoneRetryFor = _latest.Version;
        // a phone that failed keeps the update pending (Status), whatever version the other phones report
        if (failed.Count > 0) throw new InvalidOperationException("phone update failed on " + string.Join("; ", failed.Select(f => $"{f.Serial}: {f.Why}")));
        if (targets.Length > 0) AppVersion = _latest.Version;
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

    internal sealed record Release(string Version, string? Url, string? Apk, string? AgentZip, string? Sums);

    /// <summary>
    /// The phone app, this machine's agent zip and the checksum file of a GitHub release
    /// (PhoneBox-&lt;v&gt;.apk, PhoneBoxAgent-&lt;v&gt;-&lt;rid&gt;.zip, SHA256SUMS-&lt;v&gt;.txt, as release.yml names them).
    /// </summary>
    internal static Release SelectAssets(JsonElement release, string rid)
    {
        string? apk = null, zip = null, sums = null;
        foreach (var a in release.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString();
            if (name.StartsWith("PhoneBox-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) apk = url;
            if (name.StartsWith("PhoneBoxAgent-", StringComparison.OrdinalIgnoreCase) && name.EndsWith($"-{rid}.zip", StringComparison.OrdinalIgnoreCase)) zip = url;
            if (name.StartsWith("SHA256SUMS-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) sums = url;
        }
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        return new Release(tag.TrimStart('v', 'V'), release.GetProperty("html_url").GetString(), apk, zip, sums);
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
    private async Task<string> VerifiedDownloadAsync(string url, string localName, CancellationToken ct)
    {
        var release = _latest!;
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
