using Windows.Media.Control;

namespace PhoneBox.Agent;

/// <summary>Watches and controls the current Windows media session (Spotify, browsers, ...).</summary>
public sealed class MediaMonitor(ILogger<MediaMonitor> log) : BackgroundService
{
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private volatile MediaSnapshot? _snap;
    private readonly MediaArtworkCache _art = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private GlobalSystemMediaTransportControlsSession? _observed;
    private long _revision;

    public AlbumArt? Art => _art.Current;
    public MediaSnapshot? Snapshot() => _snap;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_mgr is null)
                    {
                        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                        _mgr.CurrentSessionChanged += SessionChanged;
                    }
                    await Poll(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    log.LogDebug("media: {Msg}", ex.Message);
                    _snap = null;
                }
                await Task.Delay(1000, ct);
            }
        }
        finally
        {
            if (_mgr is not null) _mgr.CurrentSessionChanged -= SessionChanged;
            Observe(null);
            _art.Clear();
        }
    }

    private void SessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => Interlocked.Increment(ref _revision);
    private void PropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => Interlocked.Increment(ref _revision);
    private void Observe(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(session, _observed)) return;
        if (_observed is not null) _observed.MediaPropertiesChanged -= PropertiesChanged;
        _observed = session;
        if (_observed is not null) _observed.MediaPropertiesChanged += PropertiesChanged;
    }

    private async Task Poll(CancellationToken ct = default)
    {
        await _pollGate.WaitAsync(ct);
        try { await PollCore(ct); }
        finally { _pollGate.Release(); }
    }

    private async Task PollCore(CancellationToken ct)
    {
        var s = _mgr!.GetCurrentSession();
        Observe(s);
        if (s is null) { _snap = null; _art.Clear(); return; }
        var revision = Interlocked.Read(ref _revision);

        var props = await s.TryGetMediaPropertiesAsync();
        var pb = s.GetPlaybackInfo();
        var tl = s.GetTimelineProperties();
        var playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        var key = $"{s.SourceAppUserModelId}|{props.Title}|{props.Artist}";
        // Refresh after metadata events and while a new track's thumbnail is still settling.
        try
        {
            await _art.UpdateAsync(key, revision, props.Thumbnail is { } thumb ? async token =>
            {
                using var stream = await thumb.OpenReadAsync();
                if (stream.Size > 8 * 1024 * 1024) return null;
                using var ms = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(ms, token);
                return ms.Length > 0 ? new AlbumArt(ms.ToArray(), string.IsNullOrEmpty(stream.ContentType) ? "image/png" : stream.ContentType) : null;
            } : null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { log.LogDebug("cover art: {Msg}", ex.Message); }

        var pos = tl.Position;
        if (playing && tl.LastUpdatedTime.Year > 2000)
            pos += DateTimeOffset.Now - tl.LastUpdatedTime;
        var dur = tl.EndTime - tl.StartTime;
        if (dur > TimeSpan.Zero && pos > dur) pos = dur;
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;

        _snap = new MediaSnapshot(
            App: FriendlyApp(s.SourceAppUserModelId),
            Title: props.Title ?? "",
            Artist: props.Artist ?? "",
            Album: props.AlbumTitle ?? "",
            Playing: playing,
            PosMs: (long)pos.TotalMilliseconds,
            DurMs: (long)Math.Max(0, dur.TotalMilliseconds),
            ArtId: Art is null ? 0 : _art.Id,
            CanSeek: pb.Controls.IsPlaybackPositionEnabled);
    }

    public async Task<bool> Control(string action, long? ms)
    {
        var s = _mgr?.GetCurrentSession();
        if (s is null)
        {
            // No session: send the media key instead (wakes up Spotify, for example).
            if (action == "playpause") { Keys.Tap(Keys.VK_MEDIA_PLAY_PAUSE); return true; }
            return false;
        }
        var ok = action switch
        {
            "playpause" => await s.TryTogglePlayPauseAsync(),
            "next" => await s.TrySkipNextAsync(),
            "prev" => await s.TrySkipPreviousAsync(),
            "seek" when ms is { } m => await s.TryChangePlaybackPositionAsync(TimeSpan.FromMilliseconds(m).Ticks),
            _ => false,
        };
        try { await Poll(); } catch { }
        return ok;
    }

    public async Task<bool> PauseAsync()
    {
        var s = _mgr?.GetCurrentSession();
        if (s is null || s.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            return false;
        var ok = await s.TryPauseAsync();
        try { await Poll(); } catch { }
        return ok;
    }

    private static string FriendlyApp(string id)
    {
        var l = id.ToLowerInvariant();
        if (l.Contains("spotify")) return "Spotify";
        if (l.Contains("chrome")) return "Chrome";
        if (l.Contains("firefox") || l.Contains("308046b0af4a39cb")) return "Firefox";
        if (l.Contains("msedge")) return "Edge";
        if (l.Contains("zune") || l.Contains("media")) return "Media Player";
        if (l.Contains("vlc")) return "VLC";
        return Path.GetFileNameWithoutExtension(id.Split('!')[0]);
    }
}
