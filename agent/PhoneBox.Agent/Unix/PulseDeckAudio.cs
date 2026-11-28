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
internal sealed partial class PulseDeckAudio(Func<IReadOnlyList<string>, string?> pactl)
{
    private const int TimeoutMs = 2000;
    /// <summary>One write at a time: a channel change reads every channel and writes them all back, so two overlapping
    /// left/right changes would otherwise drop one of them.</summary>
    private readonly Lock _writes = new();

    /// <summary>The backend when this is Linux with pactl installed, or null (the caller keeps its master-volume fallback).</summary>
    public static PulseDeckAudio? TryCreate() =>
        OperatingSystem.IsLinux() && Sh.Which("pactl") is { } path ? new(args => Run(path, args)) : null;

    private static string? Run(string path, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(path);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["LC_ALL"] = "C"; // field names in English, values (names, titles) still UTF-8
            var result = Task.Run(() => ToolProcess.RunAsync(psi, TimeSpan.FromMilliseconds(TimeoutMs), CancellationToken.None))
                .GetAwaiter().GetResult();
            return result.ExitCode == 0 ? result.Output : null;
        }
        catch { return null; }
    }

    /// <summary>The current devices and application streams, or null when the sound server does not answer.</summary>
    public DeckAudio? Read()
    {
        if (pactl(["list", "sinks"]) is not { } sinksText || pactl(["list", "sources"]) is not { } sourcesText
            || pactl(["list", "sink-inputs"]) is not { } inputsText) return null;
        var defaultSink = pactl(["get-default-sink"])?.Trim();
        var defaultSource = pactl(["get-default-source"])?.Trim();
        return Build(Parse(sinksText), Parse(sourcesText), Parse(inputsText), defaultSink, defaultSource);
    }

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

    private static bool IsMonitor(Block b, string name) =>
        b.Property("device.class") == "monitor" || name.EndsWith(".monitor", StringComparison.Ordinal)
        || b.Field("Monitor of Sink") is { } of && of != "n/a";

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
            string[] levels;
            if (channel is { } c)
            {
                if (c < 0 || c >= current.Count) return false;
                levels = [.. current.Select((level, i) => Percent(i == c ? v : level))];
            }
            else levels = [.. Scale(current, v).Select(Percent)];
            if (levels.Length == 0) levels = [Percent(v)];
            if (pactl([$"set-{kind}-volume", id, .. levels]) is null) return false;
        }
        return muted is not { } m || pactl([$"set-{kind}-mute", id, m ? "1" : "0"]) is not null;
    }

    public bool Session(string id, int? volume, bool? muted)
    {
        if (volume is < 0 or > 100 || id.Length == 0 || !id.All(char.IsAsciiDigit)) return false;
        lock (_writes)
        {
            if (volume is { } v && pactl(["set-sink-input-volume", id, Percent(v)]) is null) return false;
            return muted is not { } m || pactl(["set-sink-input-mute", id, m ? "1" : "0"]) is not null;
        }
    }

    /// <summary>Makes a device the default for its direction. Linux has no separate roles, so every role follows.</summary>
    public bool DefaultDevice(string id, int? role = null)
    {
        if (role is < 0 or > 2) return false;
        lock (_writes)
        {
            var device = Read()?.Devices.FirstOrDefault(d => d.Id == id);
            if (device is null) return false;
            var kind = device.Direction == "output" ? "sink" : "source";
            if (pactl([$"set-default-{kind}", id]) is null) return false;
            return pactl([$"get-default-{kind}"])?.Trim() == id;
        }
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
