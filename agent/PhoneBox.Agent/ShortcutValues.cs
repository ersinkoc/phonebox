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
        if (Embedded(s.Args))
            return $"In this shortcut's arguments, {Placeholder} must be an argument of its own, with a space before and after it.";
        if (OpenQuote(s.Args))
            return "This shortcut's arguments leave a quote open, so the number would not be an argument of its own.";
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
    /// The arguments of a key that takes a value: the number in place of each {value} that stands on its own, or else
    /// added as the last argument. Run without a value (a tap on a Shortcuts key, a rule, a macro step), the key leaves
    /// {value} out and adds nothing.
    /// </summary>
    public static string? Apply(string? args, CheckedValue? value)
    {
        if (Embedded(args) || (value is not null && OpenQuote(args))) throw new InvalidOperationException($"{Placeholder} must be an argument of its own");
        var text = args ?? "";
        if (!text.Contains(Placeholder, StringComparison.Ordinal))
            return value is null ? args : text.Length == 0 ? value.Text : text.TrimEnd() + " " + value.Text;
        var replaced = text.Replace(Placeholder, value?.Text ?? "", StringComparison.Ordinal).Trim();
        return replaced.Length == 0 ? null : replaced;
    }
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
