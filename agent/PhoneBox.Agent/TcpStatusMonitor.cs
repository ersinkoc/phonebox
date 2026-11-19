using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;

namespace PhoneBox.Agent;

public sealed record TcpStatusSnapshot(bool Online, double? LatencyMs, long CheckedAt, string? Error);

/// <summary>TCP connection probes shared by TCP status widgets. Results are cached briefly to avoid overlapping polls.</summary>
public sealed class TcpStatusMonitor
{
    private readonly ConcurrentDictionary<string, (TcpStatusSnapshot Snapshot, long ExpiresAt)> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<TcpStatusSnapshot> ProbeAsync(string host, int port, int timeoutMs, int cacheIntervalMs, CancellationToken ct)
    {
        host = host?.Trim() ?? "";
        if (host.Length == 0 || host.Length > 253 || host.Any(ch => ch <= ' ' || ch > '~'))
            return new TcpStatusSnapshot(false, null, Now(), "Invalid host");
        port = Math.Clamp(port, 1, 65535);
        timeoutMs = Math.Clamp(timeoutMs, 250, 10_000);
        cacheIntervalMs = Math.Clamp(cacheIntervalMs, 1000, 86_400_000);

        var key = $"{host.ToLowerInvariant()}:{port}";
        var now = Now();
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
            return cached.Snapshot;

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            now = Now();
            if (_cache.TryGetValue(key, out cached) && cached.ExpiresAt > now)
                return cached.Snapshot;

            using var client = new TcpClient();
            var watch = Stopwatch.StartNew();
            TcpStatusSnapshot snapshot;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(timeoutMs);
                await client.ConnectAsync(host, port, timeout.Token);
                snapshot = new TcpStatusSnapshot(true, Math.Round(watch.Elapsed.TotalMilliseconds, 1), Now(), null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                snapshot = new TcpStatusSnapshot(false, null, Now(), "Timed out");
            }
            catch (SocketException ex)
            {
                snapshot = new TcpStatusSnapshot(false, null, Now(), ex.SocketErrorCode == SocketError.HostNotFound ? "Host not found" : "Offline");
            }
            catch
            {
                snapshot = new TcpStatusSnapshot(false, null, Now(), "Offline");
            }

            _cache[key] = (snapshot, Now() + cacheIntervalMs);
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public readonly record struct TcpTarget(string Name, string Icon, string Host, int Port);

    public static IReadOnlyList<TcpTarget> ParseConnections(string raw)
    {
        var result = new List<TcpTarget>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var line in raw.Replace("\r", "").Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0) continue;

            // Format: Name | icon | host | port
            var parts = text.Split('|').Select(p => p.Trim()).ToArray();
            if (parts.Length < 3) continue;

            var name = parts[0];
            var icon = parts.Length >= 4 ? parts[1] : "🌐";
            var host = parts.Length >= 4 ? parts[2] : parts[1];
            var portText = parts.Length >= 4 ? parts[3] : parts[2];
            if (!int.TryParse(portText, out var port)) continue;
            port = Math.Clamp(port, 1, 65535);
            if (name.Length > 80 || host.Length > 253) continue;

            result.Add(new TcpTarget(name, icon.Length == 0 ? "🌐" : icon, host, port));
            if (result.Count >= 24) break;
        }
        return result;
    }
}
