using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
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
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Release? _latest;
    private long? _lastCheck;
    private string _state = "idle";
    private string? _error;

    /// <summary>Phone app version, reported by the app on every request (X-PhoneBox-App header).</summary>
    public static volatile string? AppVersion;

    public static bool RunningInstalled =>
        string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\', '/'), Path.GetFullPath(InstallDir).TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public UpdateStatus Status()
    {
        var latest = _latest?.Version;
        return new UpdateStatus(AppPaths.Version, AppVersion, latest, _latest?.Url,
            latest is not null && Newer(latest, AppPaths.Version) && _latest!.AgentZip is not null,
            latest is not null && AppVersion is not null && Newer(latest, AppVersion) && _latest!.Apk is not null,
            _lastCheck, _state, _error);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        CleanupOldFiles();
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
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            string? apk = null, zip = null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString();
                if (url is not null && a.TryGetProperty("url", out var api) && api.GetString() is { } apiUrl) _apiUrls[url] = apiUrl;
                if (name.StartsWith("PhoneBox-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) apk = url;
                if (name.StartsWith("PhoneBoxAgent-", StringComparison.OrdinalIgnoreCase) && name.EndsWith($"-{AppPaths.Rid}.zip", StringComparison.OrdinalIgnoreCase)) zip = url;
            }
            _latest = new Release(tag.TrimStart('v', 'V'), r.GetProperty("html_url").GetString(), apk, zip);
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
            if (s.AppUpdate && adb.Connected) await UpdatePhoneAsync(ct);
            if (s.AgentUpdate && RunningInstalled) await UpdateAgentAsync(ct);
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
            var apk = await DownloadAsync(_latest.Apk, $"PhoneBox-{_latest.Version}.apk", ct);
            var adbExe = AdbReverse.FindAdb(cfg.Get().AdbPath) ?? throw new InvalidOperationException("adb not found");
            foreach (var serial in targets)
            {
                var output = await Run(adbExe, $"-s {serial} install \"{apk}\"", ct);
                if (!output.Contains("Success")) return $"installing on {serial} failed: {output.Trim().Split('\n').LastOrDefault()}";
                log.LogInformation("phone app {Version} installed on {Serial}", _latest.Version, serial);
            }
            return null;
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
        var apk = await DownloadAsync(_latest!.Apk!, $"PhoneBox-{_latest.Version}.apk", ct);
        var adbExe = AdbReverse.FindAdb(cfg.Get().AdbPath) ?? throw new InvalidOperationException("adb not found");
        foreach (var serial in adb.Devices.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var output = await Run(adbExe, $"-s {serial} install -r \"{apk}\"", ct);
            if (!output.Contains("Success"))
                throw new InvalidOperationException($"phone update on {serial} failed: {output.Trim().Split('\n').LastOrDefault()}");
            await Run(adbExe, $"-s {serial} shell am start -n {AdbReverse.PanelActivity}", ct);
            log.LogInformation("phone app on {Serial} updated to {Version}", serial, _latest.Version);
        }
        AppVersion = _latest.Version;
    }

    private async Task UpdateAgentAsync(CancellationToken ct)
    {
        _state = "updating agent";
        var zip = await DownloadAsync(_latest!.AgentZip!, $"PhoneBoxAgent-{_latest.Version}.zip", ct);
        var staging = Path.Combine(Downloads, "agent-" + _latest.Version);
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
        Directory.CreateDirectory(Downloads);
        var path = Path.Combine(Downloads, name);
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

    private static bool Newer(string candidate, string current) =>
        Version.TryParse(candidate, out var a) && Version.TryParse(current.Split('-')[0], out var b) && a > b;

    private static async Task<string> Run(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = AppPaths.NeutralCwd,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return await stdout + await stderr;
    }

    private sealed record Release(string Version, string? Url, string? Apk, string? AgentZip);
}

/// <summary>Set before stopping the host to make Program start a fresh instance afterwards.</summary>
public static class AppRestart
{
    public static volatile bool Requested;

    /// <summary>
    /// What to start again: the running program, or inside an AppImage the .AppImage file itself (its mount point, where
    /// the program runs from, goes away when this process exits).
    /// </summary>
    internal static string Executable(string processPath, string? appImage = null)
    {
        appImage ??= Environment.GetEnvironmentVariable("APPIMAGE");
        return !string.IsNullOrEmpty(appImage) && File.Exists(appImage) ? appImage : processPath;
    }

    internal static ProcessStartInfo StartInfo(string executable, string[] args)
    {
        var appImage = executable == Environment.GetEnvironmentVariable("APPIMAGE");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = appImage ? AppPaths.NeutralCwd : AppContext.BaseDirectory };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(AppRestart).Assembly.Location);
        foreach (var arg in args.Where(a => a != "--restart")) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--restart");
        return start;
    }
}
