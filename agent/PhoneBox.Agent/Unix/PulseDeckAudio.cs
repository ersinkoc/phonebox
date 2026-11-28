using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// Control Deck audio on Linux through <c>pactl</c>: every output and microphone, the applications playing, the default
/// devices, and their volume, mute and channel balance. It works the same on PulseAudio and on PipeWire (pipewire-pulse).
/// The plain-text listing is read under <c>LC_ALL=C</c>: pactl's JSON output (<c>-f json</c>, pactl 16/17) replaces any
/// string holding a non-ASCII character with "(null)", so a sink called "Música" or a window title with an accent would
/// lose its name.
/// </summary>
/// <remarks>
/// A listing costs four pactl processes, so the last one is kept for <see cref="CacheMs"/> and shared by the writes
/// (panic and a profile restore change many devices in a row). A write patches the kept listing with what it set
/// instead of dropping it, so the next write in the same burst still finds the device and its current channels.
/// </remarks>
internal sealed partial class PulseDeckAudio(Func<IReadOnlyList<string>, string?> pactl, Func<long>? now = null)
{
    private const int TimeoutMs = 2000;
    /// <summary>How long a listing is reused, in milliseconds.</summary>
    internal const int CacheMs = 300;
    private readonly Func<long> _now = now ?? (() => Environment.TickCount64);
    /// <summary>One write at a time: a channel change reads every channel and writes them all back, so two overlapping
    /// left/right changes would otherwise drop one of them.</summary>
    private readonly Lock _writes = new();
    /// <summary>Guards <see cref="_last"/>, and is held while listing, so callers arriving together share one listing.</summary>
    private readonly Lock _cache = new();
    private (long At, DeckAudio? Audio)? _last;
    private volatile bool _answered;

    /// <summary>Whether pactl has listed the devices at least once. Until then the caller keeps its master-volume fallback:
    /// pactl may be installed with no Pulse server to talk to (PipeWire without pipewire-pulse).</summary>
    public bool HasAnswered => _answered;

    /// <summary>The backend when this is Linux with pactl installed, or null (the caller keeps its master-volume fallback).</summary>
    public static PulseDeckAudio? TryCreate() =>
        OperatingSystem.IsLinux() && Sh.Which("pactl") is { } path ? new(args => Run(path, args)) : null;

    private static string? Run(string path, IReadOnlyList<string> args)
    {
        try
        {
            // No "--" is needed before a device name: pactl parses its options with getopt_long("+s:n:…"), which stops at
            // the command word, so a name that starts with "-" is taken as the name. A "--" after the command would itself
            // be taken as the name.
            var psi = new ProcessStartInfo(path);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["LC_ALL"] = "C"; // field names in English, values (names, titles) still UTF-8
            var result = Task.Run(() => ToolProcess.RunAsync(psi, TimeSpan.FromMilliseconds(TimeoutMs), CancellationToken.None))
                .GetAwaiter().GetResult();
            return result.ExitCode == 0 ? result.Output : null;
        }
        catch { return null; }
    }

    /// <summary>The current devices and application streams, or null when the sound server does not answer. A listing
    /// younger than <see cref="CacheMs"/> (patched by the writes since) is reused.</summary>
    public DeckAudio? Read() => Read(fresh: false);

    /// <summary>The listing; <paramref name="fresh"/> skips the kept one (the new listing is kept in its place).</summary>
    public DeckAudio? Read(bool fresh)
    {
        lock (_cache)
        {
            if (!fresh && _last is { } last && _now() - last.At < CacheMs) return last.Audio;
            var audio = List();
            _last = (_now(), audio);
            if (audio is not null) _answered = true;
            return audio;
        }
    }

    private DeckAudio? List()
    {
        // "info" first: without a sound server it is the only process started. It names the defaults on every pactl
        // version; get-default-sink / get-default-source only exist since PulseAudio 15.
        if (pactl(["info"]) is not { } info || pactl(["list", "sinks"]) is not { } sinksText
            || pactl(["list", "sources"]) is not { } sourcesText || pactl(["list", "sink-inputs"]) is not { } inputsText) return null;
        var (defaultSink, defaultSource) = Defaults(info);
        return Build(Parse(sinksText), Parse(sourcesText), Parse(inputsText), defaultSink, defaultSource);
    }

    /// <summary>The default sink and source from <c>LC_ALL=C pactl info</c> ("Default Sink: name"), null when none is set.</summary>
    internal static (string? Sink, string? Source) Defaults(string? info)
    {
        string? sink = null, source = null;
        foreach (var raw in (info ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Default Sink:", StringComparison.Ordinal)) sink = DefaultName(line["Default Sink:".Length..]);
            else if (line.StartsWith("Default Source:", StringComparison.Ordinal)) source = DefaultName(line["Default Source:".Length..]);
        }
        return (sink, source);
    }

    private static string? DefaultName(string value) => value.Trim() is { Length: > 0 } name && name != "(null)" && name != "n/a" ? name : null;

    internal static DeckAudio Build(IReadOnlyList<Block> sinks, IReadOnlyList<Block> sources, IReadOnlyList<Block> inputs,
        string? defaultSink, string? defaultSource)
    {
        var devices = new List<DeckDevice>();
        var sinkNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in sinks)
        {
            if (s.Field("Name") is not { Length: > 0 } name) continue;
            sinkNames[s.Index] = name;
            devices.Add(Device(s, name, "output", name == defaultSink));
        }
        foreach (var s in sources)
        {
            // a monitor is the loopback of an output, not a microphone
            if (s.Field("Name") is not { Length: > 0 } name || IsMonitor(s, name)) continue;
            devices.Add(Device(s, name, "input", name == defaultSource));
        }
        var sessions = new List<DeckSession>();
        foreach (var i in inputs)
        {
            // streams a module made on its own (loopbacks of a virtual mixer, combine-sink) have no client: plumbing, not
            // applications. "Owner Module" can't tell: PulseAudio gives client streams its native-protocol module there.
            if (i.Field("Client") == "n/a") continue;
            var volumes = Volumes(i.Field("Volume"));
            var binary = i.Property("application.process.binary");
            var name = FirstUseful(i.Property("application.name"), binary, i.Property("media.name")) ?? "Application";
            _ = int.TryParse(i.Property("application.process.id"), NumberStyles.None, CultureInfo.InvariantCulture, out var pid);
            sessions.Add(new(i.Index, name, pid, sinkNames.GetValueOrDefault(i.Field("Sink") ?? "", ""),
                Master(volumes), i.Field("Mute") == "yes", 0, binary, i.Field("Corked") != "yes"));
        }
        var defaults = new Dictionary<string, string>();
        // Linux has one default per direction; report it for each of the Windows roles (console, media, communications)
        for (var role = 0; role < 3; role++)
        {
            if (defaultSink is { Length: > 0 }) defaults[$"output:{role}"] = defaultSink;
            if (defaultSource is { Length: > 0 }) defaults[$"input:{role}"] = defaultSource;
        }
        return new(true, null, devices, sessions, defaults);
    }

    private static DeckDevice Device(Block b, string name, string direction, bool isDefault)
    {
        var volumes = Volumes(b.Field("Volume"));
        return new(name, FirstUseful(b.Field("Description"), b.Property("device.description")) ?? name, direction, isDefault,
            Master(volumes), b.Field("Mute") == "yes", 0, Math.Max(1, volumes.Count), true, volumes.Count > 0 ? volumes : null);
    }

    // The metadata decides when pactl gives it; the ".monitor" name suffix only when it gives neither, so a real
    // microphone that happens to be named "conference.monitor" stays listed.
    private static bool IsMonitor(Block b, string name)
    {
        var cls = b.Property("device.class");
        var of = b.Field("Monitor of Sink");
        if (cls == "monitor" || of is { } sink && sink != "n/a") return true;
        if (cls is not null || of is not null) return false;
        return name.EndsWith(".monitor", StringComparison.Ordinal);
    }

    private static string? FirstUseful(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && v != "(null)")?.Trim();

    /// <summary>The level a slider shows: the loudest channel, as Windows' master volume does, capped at 100.</summary>
    private static int Master(IReadOnlyList<int> volumes) => volumes.Count == 0 ? 0 : Math.Min(100, volumes.Max());

    /// <summary>"front-left: 26214 /  40% / -23.88 dB,   front-right: 32768 /  50% / -18.06 dB" → [40, 50].</summary>
    internal static IReadOnlyList<int> Volumes(string? text) => text is null ? []
        : [.. PercentRegex().Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];

    // ---- writes -----------------------------------------------------------------------------------------------------

    public bool Device(string id, int? volume, bool? muted, int? channel = null)
    {
        lock (_writes) return DeviceLocked(id, volume, muted, channel);
    }

    private bool DeviceLocked(string id, int? volume, bool? muted, int? channel)
    {
        if (volume is < 0 or > 100) return false;
        var device = Read()?.Devices.FirstOrDefault(d => d.Id == id);
        if (device is null) return false;
        var kind = device.Direction == "output" ? "sink" : "source";
        if (volume is { } v)
        {
            var current = device.ChannelVolumes ?? [];
            IReadOnlyList<int> levels;
            if (channel is { } c)
            {
                if (c < 0 || c >= current.Count) return false;
                levels = [.. current.Select((level, i) => i == c ? v : level)];
            }
            else levels = Scale(current, v);
            if (levels.Count == 0) levels = [v];
            if (pactl([$"set-{kind}-volume", id, .. levels.Select(Percent)]) is null) { Forget(); return false; }
            Patch(a => a with { Devices = [.. a.Devices.Select(d => d.Id != id ? d
                : d with { Volume = Master(levels), ChannelVolumes = d.ChannelVolumes is null ? null : levels })] });
        }
        if (muted is not { } m) return true;
        if (pactl([$"set-{kind}-mute", id, m ? "1" : "0"]) is null) { Forget(); return false; }
        Patch(a => a with { Devices = [.. a.Devices.Select(d => d.Id == id ? d with { Muted = m } : d)] });
        return true;
    }

    public bool Session(string id, int? volume, bool? muted)
    {
        if (volume is < 0 or > 100 || id.Length == 0 || !id.All(char.IsAsciiDigit)) return false;
        lock (_writes)
        {
            if (volume is { } v)
            {
                if (pactl(["set-sink-input-volume", id, Percent(v)]) is null) { Forget(); return false; }
                Patch(a => a with { Sessions = [.. a.Sessions.Select(s => s.Id == id ? s with { Volume = v } : s)] });
            }
            if (muted is not { } m) return true;
            if (pactl(["set-sink-input-mute", id, m ? "1" : "0"]) is null) { Forget(); return false; }
            Patch(a => a with { Sessions = [.. a.Sessions.Select(s => s.Id == id ? s with { Muted = m } : s)] });
            return true;
        }
    }

    /// <summary>Makes a device the default for its direction. Linux has no separate roles, so every role follows: a
    /// device the listing already shows as the default is left alone (a profile restore sets it once per role).</summary>
    public bool DefaultDevice(string id, int? role = null)
    {
        if (role is < 0 or > 2) return false;
        lock (_writes)
        {
            var device = Read()?.Devices.FirstOrDefault(d => d.Id == id);
            if (device is null) return false;
            if (device.Default) return true;
            var kind = device.Direction == "output" ? "sink" : "source";
            if (pactl([$"set-default-{kind}", id]) is null) { Forget(); return false; }
            var (sink, source) = Defaults(pactl(["info"]));
            if ((kind == "sink" ? sink : source) != id) { Forget(); return false; }
            Patch(a => a with
            {
                Devices = [.. a.Devices.Select(d => d.Direction == device.Direction ? d with { Default = d.Id == id } : d)],
                Defaults = new Dictionary<string, string>(a.Defaults ?? new Dictionary<string, string>())
                {
                    [$"{device.Direction}:0"] = id, [$"{device.Direction}:1"] = id, [$"{device.Direction}:2"] = id,
                },
            });
            return true;
        }
    }

    /// <summary>Applies a write that worked to the kept listing; its age stays, so it still expires on time.</summary>
    private void Patch(Func<DeckAudio, DeckAudio> change)
    {
        lock (_cache) if (_last is { Audio: { } audio } last) _last = (last.At, change(audio));
    }

    /// <summary>Drops the kept listing after a write that failed: what pactl holds now is unknown.</summary>
    private void Forget()
    {
        lock (_cache) _last = null;
    }

    /// <summary>A new master level that keeps the balance: the loudest channel gets <paramref name="level"/>, the others
    /// their share of it (a 40 / 20 device set to 80 becomes 80 / 40).</summary>
    internal static IReadOnlyList<int> Scale(IReadOnlyList<int> channels, int level)
    {
        if (channels.Count == 0) return [];
        var loudest = channels.Max();
        return loudest == 0 ? [.. channels.Select(_ => level)]
            : [.. channels.Select(c => (int)Math.Round(c * (double)level / loudest, MidpointRounding.AwayFromZero))];
    }

    private static string Percent(int level) => level.ToString(CultureInfo.InvariantCulture) + "%";

    // ---- parsing ----------------------------------------------------------------------------------------------------

    /// <summary>One object of a <c>pactl list</c> listing: its index, its fields ("Name", "Volume"…) and its properties.</summary>
    internal sealed record Block(string Index, IReadOnlyDictionary<string, string> Fields, IReadOnlyDictionary<string, string> Properties)
    {
        public string? Field(string name) => Fields.GetValueOrDefault(name);
        public string? Property(string name) => Properties.GetValueOrDefault(name);
    }

    /// <summary>
    /// Splits a <c>LC_ALL=C pactl list …</c> listing into objects. Each starts with an unindented "Sink #60" header;
    /// its fields are "\tKey: value" lines, a field may continue on a deeper-indented line ("\t        balance 0.00"),
    /// and its properties are "\t\tkey = "value"" lines under "\tProperties:".
    /// </summary>
    internal static IReadOnlyList<Block> Parse(string text)
    {
        var blocks = new List<Block>();
        string? index = null;
        Dictionary<string, string> fields = [], props = [];
        var inProps = false;
        void Flush()
        {
            if (index is not null) blocks.Add(new(index, fields, props));
            fields = []; props = []; inProps = false;
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line[0] != '\t' && line[0] != ' ')
            {
                Flush();
                var hash = line.LastIndexOf('#');
                index = hash >= 0 ? line[(hash + 1)..].Trim() : null;
                continue;
            }
            if (index is null) continue;
            if (line.StartsWith("\t\t", StringComparison.Ordinal))
            {
                if (inProps && PropertyRegex().Match(line) is { Success: true } p) props[p.Groups[1].Value] = p.Groups[2].Value;
                continue;
            }
            var content = line.TrimStart('\t');
            if (content.StartsWith(' ')) continue; // continuation of the previous field ("balance 0.00")
            var colon = content.IndexOf(':');
            if (colon <= 0) continue;
            var key = content[..colon];
            inProps = key == "Properties";
            if (!inProps && key != "Formats" && key != "Ports") fields[key] = content[(colon + 1)..].Trim();
        }
        Flush();
        return blocks;
    }

    [GeneratedRegex(@"(\d+)%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex("^\t\t([^ =]+) = \"(.*)\"$")]
    private static partial Regex PropertyRegex();
}
