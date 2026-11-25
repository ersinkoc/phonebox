using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <param name="Role">user | assistant</param>
public sealed record ConversationMessage(string Role, string Text, long At);

/// <param name="Detail">A file name, the program a command starts with, or a web host: never a command line or a pattern.</param>
public sealed record ConversationStep(string Tool, string? Detail, long At, bool Running);

public sealed record ConversationView(IReadOnlyList<ConversationMessage> Messages, IReadOnlyList<ConversationStep> Steps);

/// <summary>
/// Reads the end of a Claude Code chat from its transcript, ~/.claude/projects/&lt;folder&gt;/&lt;sessionId&gt;.jsonl, for
/// the session details on the phone: the last messages and the last tool calls. Read-only, local, and only used when
/// the user has switched on Settings › Claude Code › "Show the conversation on the phone".
/// </summary>
public static partial class ClaudeTranscript
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    internal const int TailBytes = 4 * 1024 * 1024;
    internal const int MaxLineChars = 2 * 1024 * 1024;
    internal const int MaxMessages = 30;
    internal const int MaxSteps = 12;
    internal const int MaxTextChars = 4000;

    private sealed record CacheEntry(long Length, DateTime Written, ConversationView View, DateTime Used);
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();
    // The cache holds chat text, so it is kept small and short-lived: at most this many files, none for longer than
    // CacheIdleSeconds without being asked for, and (see Trim) none for a session that has ended or with the setting off.
    internal const int MaxCachedFiles = 8;
    internal const int CacheIdleSeconds = 60;
    internal static bool IsCached(string path) => Cache.ContainsKey(path);

    /// <summary>
    /// Drops cached chat text that can no longer be asked for. SessionMonitor calls this on every pass, so the text does
    /// not outlive the dialog that wanted it: not when the session ends, not when the setting goes off, not when nobody
    /// has asked for it for a minute.
    /// </summary>
    public static void Trim(IEnumerable<string> runningSessionIds, bool enabled, DateTime? now = null)
    {
        if (Cache.IsEmpty) return;
        var ids = enabled ? runningSessionIds.ToHashSet() : [];
        var t = now ?? DateTime.UtcNow;
        foreach (var (key, entry) in Cache)
            if (!ids.Contains(Path.GetFileNameWithoutExtension(key)) || (t - entry.Used).TotalSeconds > CacheIdleSeconds)
                Cache.TryRemove(key, out _);
    }

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex SessionIdPattern();
    [GeneratedRegex("<system-reminder>.*?</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex SystemReminder();

    /// <summary>The transcript of a session id, or null: the id is checked, so it cannot point outside the projects folder.</summary>
    public static string? FindFile(string sessionId, string? homeDir = null, string? claudeDir = null)
    {
        if (!SessionIdPattern().IsMatch(sessionId)) return null;
        var projects = Path.Combine(claudeDir ?? Path.Combine(homeDir ?? Home, ".claude"), "projects");
        try
        {
            if (!Directory.Exists(projects)) return null;
            foreach (var dir in Directory.EnumerateDirectories(projects))
            {
                var path = Path.Combine(dir, sessionId + ".jsonl");
                if (File.Exists(path)) return path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // a folder it may not list: no conversation
        return null;
    }

    /// <summary>
    /// The answer to the phone's request for one session. With the setting off it carries no text at all; otherwise only a
    /// Claude Code session the monitor sees running is read, so an id cannot be used to open some other chat file.
    /// </summary>
    public static object ForPhone(bool enabled, IReadOnlyList<AgentSession> running, string id, string? homeDir = null, string? claudeDir = null)
    {
        Trim(running.Where(s => s.Tool == "claude").Select(s => s.Id["claude:".Length..]), enabled);
        if (!enabled) return new { enabled = false };
        var live = running.FirstOrDefault(s => s.Id == id && s.Tool == "claude");
        var file = live is null ? null : FindFile(id["claude:".Length..], homeDir, claudeDir);
        var view = file is null ? Empty : Read(file);
        return new { enabled = true, messages = view.Messages, steps = view.Steps };
    }

    public static ConversationView Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Empty;
            if (Cache.TryGetValue(path, out var hit) && hit.Length == info.Length && hit.Written == info.LastWriteTimeUtc)
            {
                Cache[path] = hit with { Used = DateTime.UtcNow };
                return hit.View;
            }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = fs.Length;
            var start = Math.Max(0, length - TailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[length - start];
            var read = 0;
            while (read < buf.Length) { var n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
            var view = Parse(Encoding.UTF8.GetString(buf, 0, read).Split('\n').Skip(start > 0 ? 1 : 0)); // a seek cuts the first line
            Cache[path] = new CacheEntry(length, info.LastWriteTimeUtc, view, DateTime.UtcNow);
            foreach (var other in Cache.Keys.Where(k => k != path).Take(Math.Max(0, Cache.Count - MaxCachedFiles))) Cache.TryRemove(other, out _);
            return view;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Empty; }
    }

    private static ConversationView Empty => new([], []);

    internal static ConversationView Parse(IEnumerable<string> lines)
    {
        var messages = new List<ConversationMessage>();
        var steps = new List<(string Id, string Tool, string? Detail, long At)>();
        var pending = new HashSet<string>(); // tool calls that have not come back yet
        var running = new HashSet<string>();
        foreach (var line in lines)
        {
            if (line.Length is 0 or > MaxLineChars) continue;
            var isUser = line.Contains("\"user\"", StringComparison.Ordinal);
            if (!isUser && !line.Contains("\"assistant\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var rec = doc.RootElement;
                var type = Str(rec, "type");
                if (type is not ("user" or "assistant")) continue;
                if (rec.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) continue; // a sub-agent's own chat
                if (!rec.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                if (!msg.TryGetProperty("content", out var content)) continue;
                var at = Millis(Str(rec, "timestamp"));
                var meta = rec.TryGetProperty("isMeta", out var m) && m.ValueKind == JsonValueKind.True;

                if (content.ValueKind == JsonValueKind.String) { if (type == "user" && !meta) AddUser(content.GetString(), at); continue; }
                if (content.ValueKind != JsonValueKind.Array) continue;
                foreach (var block in content.EnumerateArray())
                {
                    switch (Str(block, "type"))
                    {
                        case "text" when type == "user" && !meta: AddUser(Str(block, "text"), at); break;
                        case "text" when type == "assistant": AddAssistant(Str(block, "text"), at); break;
                        case "image" when type == "user" && !meta: AddUser("[image]", at); break;
                        case "tool_use" when type == "assistant" && Str(block, "name") is { Length: > 0 } tool:
                            var id = Str(block, "id") ?? Guid.NewGuid().ToString("N");
                            steps.Add((id, tool, Detail(tool, block.TryGetProperty("input", out var input) ? input : default), at));
                            pending.Add(id);
                            break;
                        case "tool_result" when type == "user":
                            if (Str(block, "tool_use_id") is { } done) pending.Remove(done);
                            break;
                    }
                }
            }
            catch (JsonException) { /* a line still being written */ }
        }
        foreach (var id in pending) running.Add(id);
        return new ConversationView(
            messages.Skip(Math.Max(0, messages.Count - MaxMessages)).ToList(),
            steps.Skip(Math.Max(0, steps.Count - MaxSteps)).Select(s => new ConversationStep(s.Tool, s.Detail, s.At, running.Contains(s.Id))).ToList());

        void AddUser(string? text, long at)
        {
            text = Clean(text);
            if (text is null) return;
            messages.Add(new ConversationMessage("user", text, at));
            pending.Clear(); // a new message means the turn before it is over, whatever it left unanswered
        }
        void AddAssistant(string? text, long at)
        {
            text = Clean(text);
            if (text is null) return;
            // Claude Code writes each block of one reply as its own record: show them as one message
            if (messages.Count > 0 && messages[^1].Role == "assistant")
                messages[^1] = messages[^1] with { Text = Limit(messages[^1].Text + "\n\n" + text) };
            else messages.Add(new ConversationMessage("assistant", Limit(text), at));
        }
    }

    /// <summary>Drops what Claude Code adds around a message (system reminders, slash-command and background-task markers).</summary>
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = SystemReminder().Replace(text, "").Trim();
        if (text.Length == 0 || text.StartsWith("<local-command", StringComparison.Ordinal) || text.StartsWith("<command-", StringComparison.Ordinal)
            || text.StartsWith("<task-notification", StringComparison.Ordinal) || text.StartsWith("[Request interrupted", StringComparison.Ordinal)) return null;
        return Limit(text);
    }

    private static string Limit(string text) => text.Length > MaxTextChars ? text[..MaxTextChars] + "…" : text;

    /// <summary>Only what says what a step touched: a file name, the program a command starts with, a web host.</summary>
    internal static string? Detail(string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        switch (tool)
        {
            case "Edit" or "MultiEdit" or "Write" or "Read" or "NotebookEdit":
                var file = Str(input, "file_path") ?? Str(input, "notebook_path");
                return file is null ? null : Path.GetFileName(file.TrimEnd('\\', '/')) is { Length: > 0 } f ? f : null;
            case "Bash":
                return Program(Str(input, "command"));
            case "WebFetch":
                return Uri.TryCreate(Str(input, "url"), UriKind.Absolute, out var uri) ? uri.Host : null;
            default:
                return null;
        }
    }

    // NAME=value, where the name and the '=' were written as they are (a quoted "NAME=value" is a command's name)
    private static bool IsAssignment(string word, int plainPrefix)
    {
        var eq = word.IndexOf('=');
        return eq > 0 && eq < plainPrefix && !char.IsDigit(word[0]) && word[..eq].All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    // words that start a command line without saying what it runs
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
        { "cd", "export", "set", "unset", "source", "for", "if", "while", "do", "then", "else", "time", "sudo", "env", "rtk" };
    // words after which the rest of the segment is an argument list or a loop header, never the program
    private static readonly HashSet<string> SkipSegment = new(StringComparer.Ordinal)
        { "cd", "export", "set", "unset", "source", "for", "if", "while" };

    /// <summary>
    /// The program a command line starts, past a leading cd, subshell, assignment or wrapper; never its arguments. The
    /// line is read with quotes in mind, so a quoted ';' or '&amp;' does not start a new command and nothing inside quotes
    /// can come out. When it cannot tell (an unclosed quote, a $( ) or backtick, an escape) the answer is null.
    /// </summary>
    internal static string? Program(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var segments = new List<List<(string Text, bool Plain, int Prefix)>> { new() };
        var text = new StringBuilder();
        var plain = true;
        var prefix = 0; // how much of the word was written without any quote or escape
        var inWord = false;
        var quote = '\0';
        void Taint() { if (plain) { prefix = text.Length; plain = false; } }
        void EndWord()
        {
            if (inWord) segments[^1].Add((text.ToString(), plain, plain ? text.Length : prefix));
            text.Clear(); plain = true; prefix = 0; inWord = false;
        }
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else if (c == '\\' && quote == '"' && i + 1 < command.Length) text.Append(command[++i]);
                // inside double quotes a substitution still runs, and its own quotes would end this one: no telling what is what
                else if (quote == '"' && (c == '`' || (c == '$' && i + 1 < command.Length && command[i + 1] is '(' or '{'))) return null;
                else text.Append(c);
                continue;
            }
            switch (c)
            {
                case '\'' or '"': Taint(); quote = c; inWord = true; break;
                case '\\': Taint(); inWord = true; if (i + 1 < command.Length) text.Append(command[++i]); break;
                case '`': return null; // command substitution: what it holds cannot be told apart from a program
                case '$' when i + 1 < command.Length && command[i + 1] is '(' or '{': return null; // $( ) and ${ }, also $(( ))
                case '$': Taint(); inWord = true; text.Append(c); break;
                case '(' when inWord && text.Length > 0 && text[^1] == '=': return null; // an array assignment, NAME=(a b): its values are not programs
                case ' ' or '\t' or '(' or ')' or '{' or '}': EndWord(); break;
                case ';' or '&' or '|' or '\n': EndWord(); if (segments[^1].Count > 0) segments.Add([]); break;
                default: text.Append(c); inWord = true; break;
            }
        }
        if (quote != '\0') return null; // an unclosed quote: there is no telling what is a command
        EndWord();
        foreach (var segment in segments)
            foreach (var (word, isPlain, plainPrefix) in segment)
            {
                if (IsAssignment(word, plainPrefix)) continue; // VAR=value before the command
                if (Skipped.Contains(word)) { if (SkipSegment.Contains(word)) break; continue; }
                if (word.StartsWith('-')) return null; // an option of a wrapper (time -f.., sudo -u ..): not something to name
                if (!isPlain || word.Length is 0 or > 40 || !word.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or '/')) return null;
                return Path.GetFileName(word) is { Length: > 0 } f ? f : word;
            }
        return null;
    }

    private static long Millis(string? iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUnixTimeMilliseconds() : 0;

    private static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
