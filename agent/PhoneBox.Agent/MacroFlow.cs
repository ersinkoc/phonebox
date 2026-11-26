using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>
/// The "if" part of a macro: <c>if:running:spotify</c> … <c>else</c> … <c>endif</c>. The steps after an <c>if:</c> run only
/// when its condition holds (and, after <c>else</c>, only when it does not). Blocks nest.
///   running:name   a program with that process name is running (without .exe)
///   muted          the computer's sound is muted
///   on:id          the on / off key with that shortcut id is on
///   push:key       the value pushed to /api/push/key is on (true, 1, "on"); push:key=value, it holds that value
/// A leading ! turns a condition around: <c>if:!running:obs64</c>.
/// </summary>
internal static class MacroFlow
{
    internal enum Word { None, If, Else, EndIf }

    /// <summary>Tests decide which programs "run" instead of asking the computer.</summary>
    internal static Func<string, bool>? TestRunning;

    /// <summary>
    /// What a step is for the flow. "else" and "endif" stand alone (the colon is optional); a bare "end" is not one,
    /// since End is a key a macro can press.
    /// </summary>
    internal static Word Classify(string step, out string condition)
    {
        condition = "";
        var text = step.Trim();
        var word = text.TrimEnd(':').Trim().ToLowerInvariant();
        if (word == "else") return Word.Else;
        if (word == "endif") return Word.EndIf;
        if (text.StartsWith("if:", StringComparison.OrdinalIgnoreCase)) { condition = text[3..].Trim(); return Word.If; }
        return Word.None;
    }

    /// <summary>
    /// Checks this macro's if / else / endif structure and conditions before any of its own steps run, so a typo cannot
    /// leave it half done. A macro that a <c>shortcut:</c> step runs is checked when that step runs, so a typo in it
    /// shows only after the steps of the macro that called it.
    /// </summary>
    internal static void Check(IEnumerable<string> steps, AgentConfig? cfg = null)
    {
        var open = new Stack<bool>(); // one entry per open if: has its else been seen
        var n = 0;
        foreach (var step in steps)
        {
            n++;
            switch (Classify(step, out var condition))
            {
                case Word.If:
                    CheckCondition(condition, n, cfg);
                    open.Push(false);
                    break;
                case Word.Else:
                    if (open.Count == 0) throw new InvalidOperationException($"step {n}: else without a matching if:");
                    if (open.Pop()) throw new InvalidOperationException($"step {n}: a second else for the same if:");
                    open.Push(true);
                    break;
                case Word.EndIf:
                    if (open.Count == 0) throw new InvalidOperationException($"step {n}: endif without a matching if:");
                    open.Pop();
                    break;
            }
        }
        if (open.Count > 0) throw new InvalidOperationException("an if: has no endif");
    }

    private static (bool Negate, string Name, string Arg) Split(string condition)
    {
        var text = condition.Trim();
        var negate = text.StartsWith('!');
        if (negate) text = text[1..].Trim();
        var i = text.IndexOf(':');
        return i < 0 ? (negate, text.ToLowerInvariant(), "") : (negate, text[..i].Trim().ToLowerInvariant(), text[(i + 1)..].Trim());
    }

    private static void CheckCondition(string condition, int step, AgentConfig? cfg)
    {
        var (_, name, arg) = Split(condition);
        var known = name is "muted" or "running" or "on" or "push";
        if (!known) throw new InvalidOperationException($"step {step}: unknown condition \"{condition}\" (use running:, muted, on: or push:, with ! in front to turn it around)");
        if (name == "muted" && arg.Length > 0) throw new InvalidOperationException($"step {step}: \"muted\" takes no value");
        if (name != "muted" && arg.Length == 0) throw new InvalidOperationException($"step {step}: \"{name}:\" needs a value");
        // ".exe" alone is no program name: an empty name matches every process on Linux
        if (name == "running" && ProgramName(arg).Length == 0) throw new InvalidOperationException($"step {step}: \"running:\" needs a program name");
        // only that the key exists, not its state; a condition in a block that will be skipped is checked too
        if (name == "on" && cfg is not null && !cfg.Shortcuts.Any(s => s.Id == arg)) throw new InvalidOperationException($"step {step}: no such shortcut: {arg}");
    }

    internal static bool Evaluate(string condition, AgentConfig? cfg, PushStore? push, AudioControl? audio)
    {
        var (negate, name, arg) = Split(condition);
        var holds = name switch
        {
            "running" => Running(arg),
            "muted" => audio?.Snapshot()?.Muted == true,
            "on" => cfg?.Shortcuts.FirstOrDefault(s => s.Id == arg) is { } other
                ? Shortcuts.State(other, push, audio) == true
                : throw new InvalidOperationException($"no such shortcut: {arg}"),
            "push" => Shortcuts.PushIsOn(push, arg),
            _ => throw new InvalidOperationException($"unknown condition \"{condition}\""),
        };
        return holds != negate;
    }

    private static string ProgramName(string program) => program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? program[..^4].Trim() : program;

    private static bool Running(string program)
    {
        if (TestRunning is { } t) return t(program);
        var found = Process.GetProcessesByName(ProgramName(program));
        try { return found.Length > 0; }
        finally { foreach (var p in found) p.Dispose(); }
    }
}
