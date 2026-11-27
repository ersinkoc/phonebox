using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// The phone mic stays off until the user presses its Set up button in the settings page; nothing here runs on its
/// own. Set up checks that a playback device to play into exists (a virtual cable cannot be
/// installed for the user: it is a driver), fetches the official scrcpy-server 5.0.1 from the scrcpy release on
/// GitHub when no copy is found (checked against its SHA-256 before it is kept), and turns the mic on.
/// </summary>
public static class PhoneDeviceSetup
{
    public const string ServerUrl = "https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-server-v5.0.1";
    public const string ServerFileName = "scrcpy-server-5.0.1";
    public const string CableUrl = "https://vb-audio.com/Cable/";

    /// <summary>The official file is about 720 KB; a larger answer is not it, and is not read to the end.</summary>
    public const int MaxServerBytes = 2 * 1024 * 1024;

    /// <summary>Fetches <paramref name="url"/>, refusing an answer larger than <see cref="MaxServerBytes"/>.</summary>
    public static async Task<byte[]> DownloadAsync(HttpClient http, string url, CancellationToken ct)
    {
        // the client's timeout ends at the headers (ResponseHeadersRead): the body gets the same minute through this token
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(1));
        ct = limit.Token;
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength > MaxServerBytes) throw new InvalidDataException("the download is larger than scrcpy-server");
        await using var body = await res.Content.ReadAsStreamAsync(ct);
        using var kept = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int n;
        while ((n = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (kept.Length + n > MaxServerBytes) throw new InvalidDataException("the download is larger than scrcpy-server");
            kept.Write(chunk, 0, n);
        }
        return kept.ToArray();
    }

    public sealed record Result(bool Ok, string Message, string? Version = null);

    /// <summary>Where the downloaded server is kept: next to the settings file, so updating the agent keeps it.</summary>
    public static string ManagedServerPath(ConfigStore cfg) => Path.Combine(Path.GetDirectoryName(cfg.Path) ?? AppPaths.NeutralCwd, ServerFileName);

    /// <param name="playbackDevices">Names of the active playback devices.</param>
    /// <param name="download">Fetches a URL's bytes (null in tests that must not reach the network).</param>
    public static Task<Result> SetupMicAsync(ConfigStore cfg, Func<IReadOnlyList<string>> playbackDevices,
        Func<string, CancellationToken, Task<byte[]>> download, CancellationToken ct) =>
        SetupMicAsync(cfg, playbackDevices, download, ScrcpyAudio.ServerSha256, ct);

    /// <param name="sha256">The server's expected hash (tests pass the hash of their own stand-in file).</param>
    internal static async Task<Result> SetupMicAsync(ConfigStore cfg, Func<IReadOnlyList<string>> playbackDevices,
        Func<string, CancellationToken, Task<byte[]>> download, string sha256, CancellationToken ct)
    {
        // the settings as Set up found them: a change made while the download runs (turning the mic off) makes the
        // save below fail with the usual "settings changed" message instead of being overwritten
        var version = cfg.Version;
        var c = cfg.Get().PhoneMic;
        var endpoint = string.IsNullOrWhiteSpace(c.Endpoint) ? new PhoneMicConfig().Endpoint : c.Endpoint;
        if (!playbackDevices().Any(d => d.Contains(endpoint, StringComparison.OrdinalIgnoreCase)))
            return new(false, $"No playback device named like \"{endpoint}\". Install a virtual cable such as VB-CABLE ({CableUrl}), restart the computer, then press Set up again.");

        var have = new[] { c.ServerPath, ManagedServerPath(cfg), Path.Combine(AppContext.BaseDirectory, "scrcpy-server") }
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p) && Sha256Of(p) == sha256);
        string? serverPath = c.ServerPath;
        if (have is null)
        {
            byte[] bytes;
            try { bytes = await download(ServerUrl, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            { return new(false, "Could not download scrcpy-server 5.0.1 from GitHub: " + ex.Message); }
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256)
                return new(false, "The downloaded scrcpy-server is not the official 5.0.1 (SHA-256 differs); nothing was kept.");
            var target = ManagedServerPath(cfg);
            var tmp = target + ".part";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, target, overwrite: true);
            serverPath = null; // found next to the settings file without a path
        }
        else if (have != c.ServerPath) serverPath = null;

        return Save(cfg, version, x => { x.PhoneMic.Enabled = true; x.PhoneMic.ServerPath = serverPath; },
            "Phone mic is on. Connect the phone through adb (USB or Android's wireless debugging); apps pick the cable's other end (for VB-CABLE: CABLE Output) as their microphone.");
    }

    /// <summary>The names of the active playback devices.</summary>
    public static IReadOnlyList<string> PlaybackDevices() =>
#if WINDOWS
        WasapiPcmOutput.PlaybackDeviceNames();
#else
        [];
#endif

    private static string Sha256Of(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    /// <summary>Saves a change made on a copy, only if the settings are still at <paramref name="version"/>.</summary>
    private static Result Save(ConfigStore cfg, long version, Action<AgentConfig> change, string message)
    {
        // a copy: cfg.Get() is the shared, cached config, and the save may still refuse
        var copy = JsonSerializer.Deserialize<AgentConfig>(JsonSerializer.Serialize(cfg.Get(), ConfigStore.WriteOpts), ConfigStore.WriteOpts)!;
        change(copy);
        var err = cfg.Save(JsonSerializer.Serialize(copy, ConfigStore.WriteOpts), version, out var saved);
        return err is null ? new(true, message, saved.ToString(CultureInfo.InvariantCulture)) : new(false, err);
    }
}
