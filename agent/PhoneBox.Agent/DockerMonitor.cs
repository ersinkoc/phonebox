using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record DockerContainer(string Id, string Name, string Image, string State, string Status, string? Health,
    double? Cpu, string? Mem, string? Project);

public sealed record DockerSnapshot(bool Available, string? Error, IReadOnlyList<DockerContainer> Containers, long Updated);

/// <summary>
/// Docker (or Podman) containers for the Docker widget: <c>docker/podman ps -a</c> every few seconds and <c>stats</c>
/// every 15 seconds, only while a phone is asking. Start / stop / restart are only allowed when a Docker widget in the
/// settings has "Allow start / stop" turned on.
/// </summary>
public sealed class DockerMonitor(ConfigStore config, ILogger<DockerMonitor> log) : BackgroundService
{
    private volatile DockerSnapshot _snap = new(false, null, [], 0);
    private long _lastRequest;
    private Dictionary<string, (double Cpu, string Mem)> _stats = [];
    private DateTime _statsAt = DateTime.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly Lazy<IReadOnlyList<string>> Clis = new(FindClis);
    private volatile string? _activeCli;
    internal string? CliOverride { init => _activeCli = value; }
    internal Func<string, CancellationToken, string[], Task<(int Code, string Out, string Err)>>? RunnerOverride { get; init; }

    public DockerSnapshot Snapshot()
    {
        Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);
        return _snap;
    }

    public async Task<DockerSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);
        if (_snap.Updated == 0)
            await Task.WhenAny(RefreshColdCacheAsync(), Task.Delay(2000, ct));
        return _snap;
    }

    private async Task RefreshColdCacheAsync(bool withStats = true)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2))) return;
        try
        {
            if (_snap.Updated == 0)
            {
                try { await RefreshCore(CancellationToken.None, withStats); }
                catch (Exception ex) { _snap = _snap with { Error = ex.Message }; }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastRequest) < 30_000)
            {
                try { await Refresh(ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { _snap = _snap with { Error = ex.Message }; }
                await Task.Delay(4000, ct);
            }
            else await Task.Delay(500, ct);
        }
    }

    private async Task Refresh(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try { await RefreshCore(ct); }
        finally { _gate.Release(); }
    }

    private async Task RefreshCore(CancellationToken ct, bool withStats = true)
    {
        var clis = Clis.Value;
        var cli = _activeCli ?? clis.FirstOrDefault();
        if (cli is null)
        {
            _snap = new(false, "Docker is not installed (no docker or podman command found)", [], Now());
            return;
        }

        var fmt = FormatArg(cli);
        var (code, output, error) = await Exec(cli, ct, "ps", "-a", "--format", fmt);
        if (code != 0 && clis.Count > 1)
        {
            foreach (var alt in clis.Where(c => c != cli))
            {
                var altFmt = FormatArg(alt);
                var res = await Exec(alt, ct, "ps", "-a", "--format", altFmt);
                if (res.Code == 0)
                {
                    cli = _activeCli = alt;
                    fmt = altFmt;
                    (code, output, error) = res;
                    break;
                }
            }
        }
        else if (code == 0) _activeCli = cli;

        if (code != 0)
        {
            _snap = new(true, FirstLine(error) ?? "docker ps failed", _snap.Containers, Now());
            return;
        }

        var list = ParseContainers(output, _stats);
        _snap = new(true, null, list, Now());

        if (withStats && DateTime.UtcNow - _statsAt > TimeSpan.FromSeconds(15))
        {
            // Stamp before the call so a stats timeout does not make every later poll wait for it again.
            _statsAt = DateTime.UtcNow;
            try
            {
                var (sc, so, _) = await Exec(cli, ct, "stats", "--no-stream", "--format", fmt);
                if (sc == 0)
                {
                    _stats = ParseStats(so);
                    _snap = new(true, null, ParseContainers(output, _stats), Now());
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
    }

    public static Dictionary<string, (double Cpu, string Mem)> ParseStats(string raw)
    {
        var stats = new Dictionary<string, (double, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var j in EnumerateJson(raw))
        {
            var id = Str(j, "ID") ?? Str(j, "id") ?? Str(j, "ContainerID") ?? Str(j, "Container") ?? "";
            if (string.IsNullOrEmpty(id)) continue;
            var shortId = id.Length > 12 ? id[..12] : id;

            double cpu = 0;
            var cpuStr = Str(j, "CPUPerc") ?? Str(j, "cpu_percent") ?? Str(j, "avg_cpu");
            if (cpuStr is not null)
                double.TryParse(cpuStr.TrimEnd('%').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out cpu);
            else if (Num(j, "CPU") is { } cpuNum) cpu = cpuNum;
            else if (Num(j, "AvgCPU") is { } avgNum) cpu = avgNum;

            var mem = "";
            var memStr = Str(j, "MemUsage") ?? Str(j, "mem_usage");
            if (memStr is not null)
            {
                var part = memStr.Split('/')[0].Trim();
                mem = part is "--" or "0B" ? "" : part;
            }
            else if (Num(j, "MemUsage") is { } memBytes && memBytes > 0)
            {
                mem = FormatBytes(memBytes);
            }

            stats[shortId] = (cpu, mem);
        }
        return stats;
    }

    public static IReadOnlyList<DockerContainer> ParseContainers(
        string raw,
        IReadOnlyDictionary<string, (double Cpu, string Mem)>? stats = null)
    {
        var list = new List<DockerContainer>();
        foreach (var j in EnumerateJson(raw))
        {
            if (j.TryGetProperty("IsInfra", out var infra) && infra.ValueKind == JsonValueKind.True) continue;

            var id = Str(j, "ID") ?? Str(j, "Id") ?? Str(j, "id") ?? "";
            if (string.IsNullOrEmpty(id)) continue;
            var shortId = id.Length > 12 ? id[..12] : id;

            var name = (FirstStr(j, "Names") ?? Str(j, "Name") ?? shortId).TrimStart('/');
            var image = CleanImage(Str(j, "Image") ?? "");
            var state = (Str(j, "State") ?? "").ToLowerInvariant();
            var status = Str(j, "Status") ?? "";
            if (string.IsNullOrEmpty(status)) status = FallbackStatus(j, state);

            var project = ExtractProject(j);
            var hs = Str(j, "HealthStatus") ?? Str(j, "Health") ?? Str(j, "Healthcheck");
            var health = !string.IsNullOrEmpty(hs) && !hs.Equals("none", StringComparison.OrdinalIgnoreCase) ? hs : null;

            (double Cpu, string Mem)? stat = stats is not null && stats.TryGetValue(shortId, out var s) ? s : null;
            list.Add(new DockerContainer(shortId, name, image, state, status, health, stat?.Cpu, stat?.Mem, project));
        }
        return list.OrderBy(c => c.State == "running" ? 0 : 1).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Whether some Docker widget allows start / stop / restart from the phone.</summary>
    public bool ControlAllowed() =>
        config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == "docker" && w.Bool("control") == true);

    public async Task<string?> ActAsync(string id, string action)
    {
        if (!ControlAllowed()) return "starting and stopping containers is turned off in the widget's settings";
        if (action is not ("start" or "stop" or "restart")) return "unknown action";
        // A cold action needs the container list only; metrics come with the next poll, so a slow stats call
        // cannot hold the action back.
        if (_snap.Updated == 0) await RefreshColdCacheAsync(withStats: false);
        if (!_snap.Containers.Any(c => c.Id == id)) return "unknown container";
        var cli = _activeCli ?? Clis.Value.FirstOrDefault();
        if (cli is null) return "no docker or podman command found";
        // the cold refresh awaited docker: the widget may have been hidden or its control turned off meanwhile
        if (!ControlAllowed()) return "starting and stopping containers is turned off in the widget's settings";
        var (code, _, error) = await Exec(cli, CancellationToken.None, action, id);
        log.LogInformation("docker {Action} {Id}: {Result}", action, id, code == 0 ? "ok" : error);
        _statsAt = DateTime.MinValue;
        Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);
        // Wait for an in-flight poll instead of skipping, so the phone sees the new state right after the action.
        // The bound is longer than Run's 20 s timeout, so a stuck poll cannot hang the request forever.
        if (await _gate.WaitAsync(TimeSpan.FromSeconds(25)))
        {
            try { await RefreshCore(CancellationToken.None); }
            catch { }
            finally { _gate.Release(); }
        }
        return code == 0 ? null : FirstLine(error) ?? $"docker {action} failed";
    }

    private static string FormatArg(string cli) =>
        Path.GetFileNameWithoutExtension(cli).Equals("podman", StringComparison.OrdinalIgnoreCase) ? "json" : "{{json .}}";

    private Task<(int Code, string Out, string Err)> Exec(string cli, CancellationToken ct, params string[] args) =>
        RunnerOverride is { } fn ? fn(cli, ct, args) : Run(cli, ct, args);

    private static async Task<(int Code, string Out, string Err)> Run(string cli, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(cli)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = AppPaths.NeutralCwd,
        };
        if (!OperatingSystem.IsWindows())
        {
            // GUI-launched agents often lack Homebrew in PATH, which podman/docker helpers need.
            // Append only missing directories so the user's own lookup order is preserved.
            var path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
            var parts = path.Split(':', StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in new[] { "/opt/homebrew/bin", "/usr/local/bin" })
                if (!parts.Contains(dir)) path += ":" + dir;
            psi.Environment["PATH"] = path;
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await p.WaitForExitAsync(timeout.Token);
            return (p.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
    }

    private static IReadOnlyList<string> FindClis()
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (OperatingSystem.IsWindows()) dirs.Add(@"C:\Program Files\Docker\Docker\resources\bin");
        else dirs.AddRange(["/usr/local/bin", "/opt/homebrew/bin", "/usr/bin", "/Applications/Docker.app/Contents/Resources/bin"]);
        var found = new List<string>();
        foreach (var name in new[] { "docker", "podman" })
            foreach (var d in dirs)
            {
                var f = Path.Combine(d.Trim(), name + exe);
                if (File.Exists(f)) { found.Add(f); break; }
            }
        return found;
    }

    private static IEnumerable<JsonElement> EnumerateJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(trimmed); } catch { }
            if (doc is not null)
            {
                using (doc)
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var item in doc.RootElement.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.Object)
                                yield return item.Clone();
                }
                yield break;
            }
        }

        foreach (var line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(line); } catch { }
            if (doc is not null)
            {
                using (doc)
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        yield return doc.RootElement.Clone();
                }
            }
        }
    }

    private static string? ExtractProject(JsonElement j)
    {
        if (j.TryGetProperty("Labels", out var labels))
        {
            if (labels.ValueKind == JsonValueKind.String && labels.GetString() is { } s)
            {
                foreach (var l in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if ((l.StartsWith("com.docker.compose.project=", StringComparison.Ordinal) ||
                         l.StartsWith("io.podman.compose.project=", StringComparison.Ordinal)) &&
                        l.Split('=', 2)[1].Trim() is { Length: > 0 } p)
                        return p;
                }
            }
            else if (labels.ValueKind == JsonValueKind.Object)
            {
                var p = Str(labels, "com.docker.compose.project") ?? Str(labels, "io.podman.compose.project");
                if (!string.IsNullOrWhiteSpace(p)) return p;
            }
        }
        return Str(j, "PodName") is { Length: > 0 } pod ? pod : null;
    }

    private static string CleanImage(string image)
    {
        if (image.StartsWith("docker.io/library/", StringComparison.Ordinal)) return image["docker.io/library/".Length..];
        if (image.StartsWith("docker.io/", StringComparison.Ordinal)) return image["docker.io/".Length..];
        return image;
    }

    private static string FallbackStatus(JsonElement j, string state)
    {
        if (state == "running" && TryStartedUnixSeconds(j) is { } started && started > 0)
        {
            var sec = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - started);
            if (sec >= 86400) return $"Up {sec / 86400} days";
            if (sec >= 3600) return $"Up {sec / 3600} hours";
            if (sec >= 60) return $"Up {sec / 60} minutes";
            return $"Up {sec} seconds";
        }
        return Str(j, "CreatedAt") is { Length: > 0 } ca ? ca : state;
    }

    private static long? TryStartedUnixSeconds(JsonElement j)
    {
        if (Num(j, "StartedAt") is { } n && n > 0) return (long)n;
        if (Str(j, "StartedAt") is { Length: > 0 } s &&
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto) &&
            dto.Year > 1970)
            return dto.ToUnixTimeSeconds();
        return null;
    }

    private static string FormatBytes(double bytes) => bytes switch
    {
        >= 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000_000:0.##}GB"),
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000:0.#}MB"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000:0}kB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes:0}B"),
    };

    private static string? Str(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? FirstStr(JsonElement j, string name)
    {
        if (!j.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                    return s;
        return null;
    }

    private static double? Num(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static string? FirstLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
