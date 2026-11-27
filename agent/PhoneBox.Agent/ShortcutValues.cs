using System.Globalization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// A number sent with a run (#677): <c>phonebox.run(id, value)</c> on a Custom HTML page, or
/// <c>POST /api/run/{id}?value=</c>. Only a key whose settings give a range takes one. The agent checks the number against
/// that range and writes it out itself, so the text the phone sent never reaches the command line: a value cannot add
/// words, quotes, spaces or an exponent to it.
/// </summary>
public static partial class ShortcutValues
{
    public const string Placeholder = "{value}";

    /// <summary>
    /// A key that may take a value: one that starts a program or opens an address. Not a builtin: key (keys, text,
    /// macros, the phone mic…), and not an on / off key, whose off command would run without it.
    /// </summary>
    public static bool CanTake(Shortcut s) =>
        !s.Toggle && !string.IsNullOrWhiteSpace(s.Cmd) && !s.Cmd.TrimStart().StartsWith("builtin:", StringComparison.OrdinalIgnoreCase);

    /// <summary>The saved range: dropped for a key that cannot take a value; bounds kept finite, ordered and within limits.</summary>
    internal static ShortcutValue? Normalize(Shortcut s)
    {
        if (s.Value is not { } v || !CanTake(s)) return null;
        double min = Bound(v.Min), max = Bound(v.Max);
        if (max < min) (min, max) = (max, min);
        var step = double.IsFinite(v.Step) && v.Step > 0 ? Bound(Math.Max(v.Step, ShortcutValue.MinStep)) : 1;
        return new ShortcutValue { Min = min, Max = max, Step = step };
    }

    // finite, within the limits and at most 6 decimals, saved as the same number the check works with (a double read as
    // a decimal keeps 15 digits, so a bound with more would quietly differ from the one in the file)
    private static double Bound(double x) =>
        double.IsFinite(x) ? (double)Exact(Math.Round((decimal)Math.Clamp(x, -ShortcutValue.Limit, ShortcutValue.Limit), ShortcutValue.Decimals)) : 0;

    private static decimal Exact(decimal d) => (decimal)(double)d;

    /// <summary>
    /// Checks a value sent for a key. On success <paramref name="value"/> holds the number as the agent writes it
    /// (invariant culture, no exponent, no trailing zeros).
    /// </summary>
    /// <returns>Why the value is refused, in the agent's own words (no configured text, so the phone may show it), or null.</returns>
    public static string? Check(Shortcut s, string? raw, out CheckedValue? value)
    {
        value = null;
        if (s.Value is not { } range || !CanTake(s))
            return "This shortcut takes no value. Only a key that starts a program or opens an address, with a value range in its settings, takes one.";
        // a program gets the number as an argument of its own; an address or a file opens through the system, which
        // passes no arguments on, so there the number can only go in place of {value} in the address itself
        if (Program(s.Cmd, s.Cwd) is not null)
        {
            if (s.Cmd.Contains(Placeholder, StringComparison.Ordinal))
                return $"{Placeholder} goes in the arguments of a program, not in its name.";
            // where the number goes in the arguments: only a program gets them
            if (Embedded(s.Args))
                return $"In this shortcut's arguments, {Placeholder} must be an unquoted argument of its own, with a space or a tab between it and the next argument.";
            if (OpenQuote(s.Args))
                return "This shortcut's arguments leave a quote open, so the number would not be an argument of its own.";
        }
        else if (IsAddress(s.Cmd))
        {
            if (!s.Cmd.Contains(Placeholder, StringComparison.Ordinal))
                return $"This shortcut opens an address, which takes no arguments. Put {Placeholder} in the address to send it the number.";
        }
        else
            return $"This shortcut takes a number only when it starts a program found on this computer, or opens an address with {Placeholder} in it.";
        if (raw is null || !PlainNumber().IsMatch(raw)
            || !decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
            return "The value must be a plain number, such as 37 or -2.5.";
        decimal min = (decimal)range.Min, max = (decimal)range.Max, step = (decimal)range.Step;
        if (n < min || n > max) return $"The value must be from {Text(min)} to {Text(max)}.";
        if ((n - min) % step != 0) return $"The value must be {Text(min)} plus a multiple of {Text(step)}.";
        value = new CheckedValue(Text(n));
        return null;
    }

    // digits with an optional minus and decimal part: no "+", exponent, spaces, thousands separators, "NaN" or "Infinity".
    // Few enough digits that a decimal holds the number exactly, so a longer one cannot round into the range.
    [GeneratedRegex(@"^-?[0-9]{1,15}(\.[0-9]{1,12})?$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainNumber();

    internal static string Text(decimal n) => n == 0 ? "0" : n.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>
    /// A {value} that would not be an argument of its own: part of a longer word ("--seek={value}"), next to a quote, or
    /// inside a quoted argument ("--title "at {value}""). Only a space or a tab separates arguments; a quote opens or
    /// closes a quoted argument unless an odd number of backslashes escapes it (how Windows and .NET split arguments).
    /// </summary>
    internal static bool Embedded(string? args) => Scan(args).Embedded;

    /// <summary>A quote that is never closed: a number added at the end would land inside that quoted argument.</summary>
    internal static bool OpenQuote(string? args) => Scan(args).Open;

    private static (bool Embedded, bool Open) Scan(string? args)
    {
        if (args is null) return (false, false);
        var quoted = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == '"')
            {
                var slashes = 0;
                for (var j = i - 1; j >= 0 && args[j] == '\\'; j--) slashes++;
                if (slashes % 2 == 0) quoted = !quoted;
            }
            else if (string.CompareOrdinal(args, i, Placeholder, 0, Placeholder.Length) == 0)
            {
                var end = i + Placeholder.Length;
                if (quoted || (i > 0 && !Gap(args[i - 1])) || (end < args.Length && !Gap(args[end]))) return (true, quoted);
                i = end - 1;
            }
        }
        return (false, quoted);
    }

    private static bool Gap(char c) => c is ' ' or '\t';

    /// <summary>
    /// The arguments of a program key that takes a value, one entry each: the configured text split as Windows and .NET
    /// split a command line (the rules <see cref="Scan"/> checks against), then the number in place of each {value}
    /// that stands on its own, or else added as the last argument. The number is put in after splitting, so it is
    /// always an argument of its own. Run without a number (a tap, a rule, a macro step), {value} is left out.
    /// </summary>
    public static List<string> Arguments(string? args, CheckedValue? value)
    {
        // an open quote matters only when a number is added: it would land inside the quoted argument
        if (Embedded(args) || (value is not null && OpenQuote(args))) throw new InvalidOperationException($"{Placeholder} must be an argument of its own");
        var list = new List<string>();
        var placed = false;
        foreach (var arg in Split(args ?? ""))
        {
            if (arg.Placeholder) { placed = true; if (value is not null) list.Add(value.Text); }
            else list.Add(arg.Text);
        }
        if (!placed && value is not null) list.Add(value.Text);
        return list;
    }

    /// <summary>
    /// Splits a command line as .NET and Windows do: spaces and tabs part arguments outside quotes; a quote opens or
    /// closes a quoted part, and inside one "" is a literal quote; 2n backslashes before a quote give n backslashes,
    /// 2n+1 give n and a literal quote; other backslashes stay. An argument counts as the placeholder only when it is
    /// exactly {value} with no quotes in it.
    /// </summary>
    internal static List<(string Text, bool Placeholder)> Split(string args)
    {
        var list = new List<(string, bool)>();
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false, hadQuote = false;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (c == '\\')
            {
                var n = 0;
                while (i + n < args.Length && args[i + n] == '\\') n++;
                if (i + n < args.Length && args[i + n] == '"')
                {
                    current.Append('\\', n / 2);
                    i += n; // at the quote
                    if (n % 2 == 1) current.Append('"');
                    // even: the quote is a quote of its own, so "" inside quotes is still a literal quote (as .NET does)
                    else if (quoted && i + 1 < args.Length && args[i + 1] == '"') { current.Append('"'); i++; }
                    else quoted = !quoted;
                    hadQuote = true;
                }
                else { current.Append('\\', n); i += n - 1; }
                any = true;
            }
            else if (c == '"')
            {
                if (quoted && i + 1 < args.Length && args[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
                hadQuote = any = true;
            }
            else if (!quoted && Gap(c))
            {
                if (any) list.Add(Done());
            }
            else { current.Append(c); any = true; }
        }
        if (any) list.Add(Done());
        return list;

        (string, bool) Done()
        {
            var text = current.ToString();
            var item = (text, text == Placeholder && !hadQuote);
            current.Clear();
            any = hadQuote = false;
            return item;
        }
    }

    /// <summary>
    /// The arguments of an address key without a number: the configured text with {value} left out (an address takes
    /// its number in the address, never in its arguments).
    /// </summary>
    internal static string? WithoutPlaceholder(string? args)
    {
        if (args is null || !args.Contains(Placeholder, StringComparison.Ordinal)) return args;
        var text = args.Replace(Placeholder, "", StringComparison.Ordinal).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// The program a key with a range starts directly, without the shell, as a full path: a file the agent can run,
    /// given by its path (a relative one from the key's working folder) or found on the PATH (Windows: an .exe or
    /// .com). Null for an address (https:, shell:, ms-settings:…), a document, a folder or an app bundle, which the
    /// system opens and which take no arguments.
    /// </summary>
    internal static string? Program(string? cmd, string? cwd = null)
    {
        // not trimmed: a space at the end can be part of a file's name, and the launch uses the command as written
        var c = Shortcuts.Home(cmd ?? "");
        if (string.IsNullOrWhiteSpace(c) || IsAddress(c)) return null;
        var hasDir = c.Contains('/') || c.Contains(Path.DirectorySeparatorChar);
        if (hasDir && !Path.IsPathRooted(c) && !string.IsNullOrEmpty(cwd)) c = Path.Combine(Shortcuts.Home(cwd), c);
        // Windows PATH entries may be quoted or padded; on Unix a space or a quote is part of the folder's name
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => OperatingSystem.IsWindows() ? d.Trim().Trim('"') : d).Where(d => d.Length > 0);
        if (OperatingSystem.IsWindows())
        {
            string[] runnable = [".exe", ".com"];
            var ext = Path.GetExtension(c).ToLowerInvariant();
            if (ext.Length > 0 && !runnable.Contains(ext)) return null;
            var names = ext.Length > 0 ? [c] : runnable.Select(e => c + e).ToArray();
            foreach (var name in names)
            {
                if (hasDir || Path.IsPathRooted(name)) { if (File.Exists(name)) return Path.GetFullPath(name); }
                else if (dirs.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists) is { } found) return Path.GetFullPath(found);
            }
            return null;
        }
        if (hasDir) return CanRun(c) ? Path.GetFullPath(c) : null;
        // a full path: a relative PATH entry would otherwise be resolved again at the start, maybe to another file
        return dirs.Select(d => Path.Combine(d, c)).FirstOrDefault(CanRun) is { } hit ? Path.GetFullPath(hit) : null;
    }

    // the agent itself may run it (access(2) with X_OK): a mode bit for another user is not enough
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static bool CanRun(string path) => File.Exists(path) && access(path, 1 /* X_OK */) == 0;

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int access(string path, int mode);

    /// <summary>
    /// An address the system opens: a scheme of two letters or more ("https:", "shell:", "ms-settings:"), so a drive
    /// letter ("C:\") is not one. Not "file:": .NET starts a file URI as the program it names, which would drop the
    /// number.
    /// </summary>
    internal static bool IsAddress(string? cmd) =>
        Address().IsMatch(cmd ?? "") && !(cmd ?? "").StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]+:", RegexOptions.CultureInvariant)]
    private static partial Regex Address();
}

/// <summary>A value that passed <see cref="ShortcutValues.Check"/>: only the agent's own text for a number in range.</summary>
public sealed class CheckedValue
{
    internal CheckedValue(string text) => Text = text;
    public string Text { get; }
    public override string ToString() => Text;
}

/// <summary>
/// At most <see cref="PerSecond"/> values a second for each key, so a slider dragged across a page cannot start hundreds
/// of programs. Runs without a value are not limited here (they come from a tap).
/// </summary>
public sealed class ValueRateGate(Func<long>? nowMs = null)
{
    public const int PerSecond = 4;
    public static ValueRateGate Shared { get; } = new();

    private readonly Func<long> _now = nowMs ?? (() => Environment.TickCount64);
    // only ids of configured keys that passed the check get here; keys quiet for a second are dropped once there are
    // more than a few, so ids of keys renamed or deleted since don't stay
    private readonly Dictionary<string, Queue<long>> _runs = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private const int SweepAbove = 32;

    internal int Tracked { get { lock (_lock) return _runs.Count; } }

    /// <summary>Takes a slot for this key; false when it already ran <see cref="PerSecond"/> times in the last second.</summary>
    public bool TryTake(string id)
    {
        lock (_lock)
        {
            // read inside the lock: a time read before it could be queued after a later one and open an extra slot
            var now = _now();
            if (_runs.Count > SweepAbove)
                foreach (var quiet in _runs.Where(r => now - r.Value.Last() >= 1000).Select(r => r.Key).ToList()) _runs.Remove(quiet);
            if (!_runs.TryGetValue(id, out var times)) _runs[id] = times = new Queue<long>(PerSecond);
            while (times.Count > 0 && now - times.Peek() >= 1000) times.Dequeue();
            if (times.Count >= PerSecond) return false;
            times.Enqueue(now);
            return true;
        }
    }
}
