using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed partial class SessionMonitor
{
    private const int AgyDiscoveryIntervalMs = 30_000;
    private const int AgyCliProjectMapIntervalMs = 60_000;
    private const int AgyMaxSessions = 50;
    private const int AgyMaxHeaderBytes = 1024 * 1024;
    private const int AgyMaxAnnotationBytes = 64 * 1024;
    private const int AgyMaxCliHistoryBytes = 256 * 1024;
    private const int AgyMaxReadBytes = 2 * 1024 * 1024;
    private const int AgyMaxProjectChars = 100;
    private const int AgyMaxMetaChars = 80;
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private sealed record AgyCandidate(string TranscriptPath, string RootPath, string ConvId, bool IsCli);
    private sealed record AgyHeader(bool IsUser, long? StartedAtMs);
    private sealed record AgyAnnotation(DateTime LastWriteUtc, long Length, bool TitlesEnabled, bool Archived, string? Title);
    private sealed record AgyDetails(
        string? Model,
        long? InputTokens,
        long? OutputTokens,
        long? CachedInputTokens,
        long? LastActivityAt,
        long? TurnStartedAt,
        bool InFlight,
        string? Outcome,
        string? LastTool,
        long ReadOffset,
        bool TokensComplete,
        bool HasPriorChunks);

    private string? _agyDiscoveryHome;
    private long _nextAgyDiscovery;
    private AgyCandidate[] _agyCandidates = [];
    private readonly ConcurrentDictionary<string, AgyHeader> _agyHeaders = new();
    private readonly ConcurrentDictionary<string, AgyAnnotation> _agyAnnotations = new();
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, long Length, AgyDetails Details)> _agyDetails = new();
    private string? _agyCliHistoryPath;
    private DateTime _agyCliHistoryWriteUtc;
    private long _agyCliHistoryLength = -1;
    private long _nextAgyCliProjectMap;
    private Dictionary<string, string>? _agyCliProjects;

    internal void ClearAntigravityCache()
    {
        _agyDiscoveryHome = null;
        _nextAgyDiscovery = 0;
        _agyCandidates = [];
        _agyHeaders.Clear();
        _agyAnnotations.Clear();
        _agyDetails.Clear();
        _agyCliHistoryPath = null;
        _agyCliHistoryWriteUtc = default;
        _agyCliHistoryLength = -1;
        _nextAgyCliProjectMap = 0;
        _agyCliProjects = null;
    }

    /// <summary>True when the transcript detail cache holds this path at the given write time and length (for tests).</summary>
    internal bool HasCachedAntigravityDetails(string path, DateTime lastWriteUtc, long length) =>
        _agyDetails.TryGetValue(path, out var cached) && cached.LastWriteUtc == lastWriteUtc && cached.Length == length;

    /// <summary>
    /// Recent Antigravity user conversations from ~/.gemini/antigravity/brain and ~/.gemini/antigravity-cli/brain.
    /// Subagent transcripts (starting with SYSTEM_MESSAGE) and archived conversations (annotations/*.pbtxt) are skipped.
    /// Prompts, replies, thinking, and tool arguments are never retained or sent; titles require <paramref name="titles"/>.
    /// </summary>
    internal IEnumerable<AgentSession> ReadAntigravity(string? homeDir = null, bool titles = false)
    {
        var home = homeDir ?? Home;
        if (!string.Equals(_agyDiscoveryHome, home, StringComparison.Ordinal))
        {
            ClearAntigravityCache();
            _agyDiscoveryHome = home;
        }

        var appRoot = Path.Combine(home, ".gemini", "antigravity");
        var cliRoot = Path.Combine(home, ".gemini", "antigravity-cli");
        if (!Directory.Exists(appRoot) && !Directory.Exists(cliRoot)) yield break;

        var cutoff = DateTime.UtcNow.AddHours(-3);
        if (Environment.TickCount64 >= _nextAgyDiscovery)
        {
            var found = new List<(DateTime LastWriteUtc, AgyCandidate Candidate)>();
            CollectAntigravityCandidates(appRoot, isCli: false, cutoff, found);
            CollectAntigravityCandidates(cliRoot, isCli: true, cutoff, found);
            _agyCandidates = found
                .OrderByDescending(x => x.LastWriteUtc)
                .Select(x => x.Candidate)
                .ToArray();
            _nextAgyDiscovery = Environment.TickCount64 + AgyDiscoveryIntervalMs;
        }

        Dictionary<string, string>? cliProjects = null;
        var activePaths = new HashSet<string>(StringComparer.Ordinal);
        var activeAnnotations = new HashSet<string>(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var emitted = new List<AgentSession>();

        foreach (var cand in _agyCandidates)
        {
            try
            {
                var f = new FileInfo(cand.TranscriptPath);
                if (!f.Exists || f.LastWriteTimeUtc <= cutoff) continue;
                activePaths.Add(f.FullName);

                var chunk0Path = Path.Combine(Path.GetDirectoryName(f.FullName)!, "chunks", "transcript", "00000000.jsonl");
                var hasPriorChunks = File.Exists(chunk0Path);
                var header = ReadAntigravityHeaderCached(f.FullName, hasPriorChunks ? chunk0Path : f.FullName);
                if (header is null || !header.IsUser) continue;

                var annPath = Path.Combine(cand.RootPath, "annotations", cand.ConvId + ".pbtxt");
                if (File.Exists(annPath)) activeAnnotations.Add(annPath);
                var ann = ReadAntigravityAnnotationCached(annPath, titles);
                if (ann.Archived) continue;

                var details = ReadAntigravityDetailsCached(f, hasPriorChunks);
                var project = "Antigravity";
                if (cand.IsCli)
                {
                    cliProjects ??= ReadAntigravityCliProjects(cliRoot);
                    if (cliProjects.TryGetValue(cand.ConvId, out var mapped) && mapped.Length > 0)
                        project = mapped;
                }

                var title = titles && ann.Title is { Length: > 0 } && ann.Title != project ? ann.Title : null;
                var startedAt = header.StartedAtMs ?? new DateTimeOffset(f.CreationTimeUtc).ToUnixTimeMilliseconds();
                var fileWriteMs = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                var since = Math.Max(startedAt, details.LastActivityAt ?? fileWriteMs);
                var age = DateTime.UtcNow - f.LastWriteTimeUtc;
                var status = details.InFlight && age < TimeSpan.FromMinutes(3) ? "busy" : "idle";
                var shortId = cand.ConvId.Length >= 4 ? cand.ConvId[..4] : cand.ConvId;

                var hasTokens = details.TokensComplete &&
                    (details.InputTokens is not null || details.OutputTokens is not null || details.CachedInputTokens is not null);
                long? input = hasTokens ? (details.InputTokens ?? 0) : null;
                long? output = hasTokens ? (details.OutputTokens ?? 0) : null;
                long? cached = hasTokens ? (details.CachedInputTokens ?? 0) : null;
                long? total = hasTokens ? input + output + cached : null;

                var session = new AgentSession(
                    Id: "antigravity:" + cand.ConvId,
                    Tool: "antigravity",
                    Name: $"{project.ToLowerInvariant()}-{shortId}",
                    Project: project,
                    Status: status,
                    WaitingFor: null,
                    Since: since,
                    StartedAt: startedAt,
                    Model: details.Model,
                    TotalTokens: total,
                    InputTokens: input,
                    OutputTokens: output,
                    CachedInputTokens: cached,
                    TurnStartedAt: status == "busy" ? details.TurnStartedAt : null,
                    Outcome: status == "busy" ? null : details.Outcome,
                    LastTool: details.LastTool,
                    Title: title);

                if (seenIds.Add(cand.ConvId))
                    emitted.Add(session);
            }
            catch { }
        }

        foreach (var old in _agyDetails.Keys.Where(p => !activePaths.Contains(p)))
            _agyDetails.TryRemove(old, out _);
        foreach (var old in _agyHeaders.Keys.Where(p => !activePaths.Contains(p)))
            _agyHeaders.TryRemove(old, out _);
        foreach (var old in _agyAnnotations.Keys.Where(p => !activeAnnotations.Contains(p)))
            _agyAnnotations.TryRemove(old, out _);

        foreach (var s in emitted.OrderByDescending(s => s.Since).Take(AgyMaxSessions))
            yield return s;
    }

    private static void CollectAntigravityCandidates(string root, bool isCli, DateTime cutoff, List<(DateTime LastWriteUtc, AgyCandidate Candidate)> dest)
    {
        var brainDir = Path.Combine(root, "brain");
        if (!Directory.Exists(brainDir)) return;
        try
        {
            foreach (var convDir in Directory.EnumerateDirectories(brainDir))
            {
                var convId = Path.GetFileName(convDir);
                if (string.IsNullOrWhiteSpace(convId) || convId.StartsWith('.')) continue;
                var transcriptPath = Path.Combine(convDir, ".system_generated", "logs", "transcript.jsonl");
                var fi = new FileInfo(transcriptPath);
                if (!fi.Exists || fi.LastWriteTimeUtc <= cutoff) continue;
                dest.Add((fi.LastWriteTimeUtc, new AgyCandidate(fi.FullName, root, convId, isCli)));
            }
        }
        catch { }
    }

    private AgyHeader? ReadAntigravityHeaderCached(string cacheKey, string headerFilePath)
    {
        if (_agyHeaders.TryGetValue(cacheKey, out var cached))
            return cached;
        var parsed = ReadAntigravityHeader(headerFilePath);
        if (parsed is not null)
            _agyHeaders[cacheKey] = parsed;
        return parsed;
    }

    private static AgyHeader? ReadAntigravityHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[Math.Min(64 * 1024, (int)Math.Min(fs.Length, AgyMaxHeaderBytes))];
            int len = 0, nl = -1;
            while (nl < 0)
            {
                if (len == buf.Length)
                {
                    if (buf.Length >= AgyMaxHeaderBytes) return null;
                    Array.Resize(ref buf, Math.Min(buf.Length * 2, AgyMaxHeaderBytes));
                }
                var n = fs.Read(buf, len, buf.Length - len);
                if (n <= 0) return null; // line 0 is incomplete: retry on the next poll
                nl = Array.IndexOf(buf, (byte)'\n', len, n);
                len += n;
            }

            var line = buf.AsMemory(0, nl);
            if (line.Span.StartsWith(Utf8Bom)) line = line[3..];
            if (line.IsEmpty) return null;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var isUser = Str(root, "type") == "USER_INPUT" || Str(root, "source") == "USER_EXPLICIT";
            long? startedAt = DateTimeOffset.TryParse(Str(root, "created_at"), out var dt)
                ? dt.ToUnixTimeMilliseconds()
                : null;
            return new AgyHeader(isUser, startedAt);
        }
        catch { return null; }
    }

    private AgyAnnotation ReadAntigravityAnnotationCached(string annPath, bool titles)
    {
        var fi = new FileInfo(annPath);
        if (!fi.Exists)
            return new AgyAnnotation(default, -1, titles, false, null);
        if (_agyAnnotations.TryGetValue(annPath, out var cached)
            && cached.LastWriteUtc == fi.LastWriteTimeUtc
            && cached.Length == fi.Length
            && (cached.TitlesEnabled || !titles))
        {
            return titles ? cached : cached with { Title = null };
        }

        var (archived, title) = ReadAntigravityAnnotation(annPath, titles);
        var entry = new AgyAnnotation(fi.LastWriteTimeUtc, fi.Length, titles, archived, titles ? title : null);
        _agyAnnotations[annPath] = entry;
        return entry;
    }

    internal static (bool Archived, string? Title) ReadAntigravityAnnotation(string path, bool readTitle)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var toRead = (int)Math.Min(fs.Length, AgyMaxAnnotationBytes);
            if (toRead <= 0) return (false, null);
            var buf = new byte[toRead];
            var read = 0;
            while (read < toRead)
            {
                var n = fs.Read(buf, read, toRead - read);
                if (n <= 0) break;
                read += n;
            }
            return ParsePbtxtAnnotation(Encoding.UTF8.GetString(buf, 0, read), readTitle);
        }
        catch { return (false, null); }
    }

    /// <summary>
    /// Scans top-level protobuf text fields while skipping quoted strings and nested messages so text inside a title
    /// such as "archived:true" cannot hide a conversation.
    /// </summary>
    internal static (bool Archived, string? Title) ParsePbtxtAnnotation(string text, bool readTitle)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var archived = false;
        string? title = null;
        var depth = 0;
        var i = 0;

        static void SkipSpaceAndComments(string s, ref int idx)
        {
            while (idx < s.Length)
            {
                if (char.IsWhiteSpace(s[idx])) { idx++; continue; }
                if (s[idx] == '#') { while (idx < s.Length && s[idx] != '\n') idx++; continue; }
                break;
            }
        }

        static string? ReadQuoted(string s, ref int idx, bool decode)
        {
            var quote = s[idx++];
            using var ms = decode ? new MemoryStream() : null;
            while (idx < s.Length)
            {
                var ch = s[idx++];
                if (ch == quote)
                    return decode ? Encoding.UTF8.GetString(ms!.ToArray()) : null;
                if (ch == '\\' && idx < s.Length)
                {
                    var esc = s[idx++];
                    if (!decode)
                    {
                        if (esc is 'x' or 'X') { for (var k = 0; k < 2 && idx < s.Length && Uri.IsHexDigit(s[idx]); k++) idx++; }
                        else if (esc >= '0' && esc <= '7') { for (var k = 0; k < 2 && idx < s.Length && s[idx] >= '0' && s[idx] <= '7'; k++) idx++; }
                        else if (esc == 'u') { for (var k = 0; k < 4 && idx < s.Length && Uri.IsHexDigit(s[idx]); k++) idx++; }
                        else if (esc == 'U') { for (var k = 0; k < 8 && idx < s.Length && Uri.IsHexDigit(s[idx]); k++) idx++; }
                        continue;
                    }
                    switch (esc)
                    {
                        case 'a': ms!.WriteByte((byte)'\a'); break;
                        case 'b': ms!.WriteByte((byte)'\b'); break;
                        case 'f': ms!.WriteByte((byte)'\f'); break;
                        case 'n': ms!.WriteByte((byte)'\n'); break;
                        case 'r': ms!.WriteByte((byte)'\r'); break;
                        case 't': ms!.WriteByte((byte)'\t'); break;
                        case 'v': ms!.WriteByte((byte)'\v'); break;
                        case '\\': ms!.WriteByte((byte)'\\'); break;
                        case '\'': ms!.WriteByte((byte)'\''); break;
                        case '"': ms!.WriteByte((byte)'"'); break;
                        case 'x':
                        case 'X':
                        {
                            var val = 0;
                            for (var k = 0; k < 2 && idx < s.Length && Uri.IsHexDigit(s[idx]); k++, idx++)
                                val = (val << 4) | Uri.FromHex(s[idx]);
                            ms!.WriteByte((byte)val);
                            break;
                        }
                        case >= '0' and <= '7':
                        {
                            var val = esc - '0';
                            for (var k = 0; k < 2 && idx < s.Length && s[idx] >= '0' && s[idx] <= '7'; k++, idx++)
                                val = (val << 3) | (s[idx] - '0');
                            ms!.WriteByte((byte)val);
                            break;
                        }
                        case 'u':
                        case 'U':
                        {
                            var maxDigits = esc == 'u' ? 4 : 8;
                            var val = 0;
                            var count = 0;
                            for (; count < maxDigits && idx < s.Length && Uri.IsHexDigit(s[idx]); count++, idx++)
                                val = (val << 4) | Uri.FromHex(s[idx]);
                            if (count > 0 && Rune.TryCreate(val, out var rune))
                            {
                                Span<byte> utf8 = stackalloc byte[4];
                                var written = rune.EncodeToUtf8(utf8);
                                ms!.Write(utf8[..written]);
                            }
                            break;
                        }
                        default:
                            AppendUtf8Char(ms!, esc);
                            break;
                    }
                    continue;
                }
                if (!decode) continue;
                // an emoji is two chars (a surrogate pair): encode them together, or both halves become U+FFFD
                if (char.IsHighSurrogate(ch) && idx < s.Length && char.IsLowSurrogate(s[idx])) AppendUtf8Pair(ms!, ch, s[idx++]);
                else AppendUtf8Char(ms!, ch);
            }
            return decode ? Encoding.UTF8.GetString(ms!.ToArray()) : null;
        }

        while (i < text.Length)
        {
            SkipSpaceAndComments(text, ref i);
            if (i >= text.Length) break;

            var c = text[i];
            if (c == '{') { depth++; i++; continue; }
            if (c == '}') { if (depth > 0) depth--; i++; continue; }
            if (c is '"' or '\'') { ReadQuoted(text, ref i, decode: false); continue; }

            if (depth == 0 && (char.IsLetter(c) || c == '_'))
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var key = text[start..i];
                SkipSpaceAndComments(text, ref i);
                if (i < text.Length && text[i] == ':')
                {
                    i++;
                    SkipSpaceAndComments(text, ref i);
                    if (i < text.Length && text[i] == '{')
                    {
                        depth++;
                        i++;
                    }
                    else if (key == "archived" && i < text.Length)
                    {
                        var valStart = i;
                        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                        var val = text[valStart..i];
                        if (val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1")
                            archived = true;
                        else if (val.Equals("false", StringComparison.OrdinalIgnoreCase) || val == "0")
                            archived = false;
                    }
                    else if (key == "title" && i < text.Length && text[i] is '"' or '\'')
                    {
                        var sb = readTitle ? new StringBuilder() : null;
                        while (i < text.Length && text[i] is '"' or '\'')
                        {
                            var part = ReadQuoted(text, ref i, readTitle);
                            if (readTitle && part is not null) sb!.Append(part);
                            SkipSpaceAndComments(text, ref i);
                        }
                        if (readTitle)
                            title = SanitizeAntigravityTitle(sb!.ToString()) ?? title;
                    }
                }
                continue;
            }
            i++;
        }

        return (archived, title);
    }

    private static void AppendUtf8Char(MemoryStream ms, char ch)
    {
        Span<char> chars = [ch];
        Span<byte> utf8 = stackalloc byte[4];
        var written = Encoding.UTF8.GetBytes(chars, utf8);
        ms.Write(utf8[..written]);
    }

    private static void AppendUtf8Pair(MemoryStream ms, char high, char low)
    {
        Span<char> chars = [high, low];
        Span<byte> utf8 = stackalloc byte[4];
        var written = Encoding.UTF8.GetBytes(chars, utf8);
        ms.Write(utf8[..written]);
    }

    private Dictionary<string, string> ReadAntigravityCliProjects(string cliRoot)
    {
        var historyPath = Path.Combine(cliRoot, "history.jsonl");
        var fi = new FileInfo(historyPath);
        if (!fi.Exists)
            return [];

        if (_agyCliProjects is not null
            && string.Equals(_agyCliHistoryPath, historyPath, StringComparison.Ordinal)
            && Environment.TickCount64 < _nextAgyCliProjectMap
            && _agyCliHistoryWriteUtc == fi.LastWriteTimeUtc
            && _agyCliHistoryLength == fi.Length)
        {
            return _agyCliProjects;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var fs = new FileStream(historyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = fs.Length;
            var take = (int)Math.Min(len, AgyMaxCliHistoryBytes);
            if (take > 0)
            {
                var start = len - take;
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[take];
                var read = 0;
                while (read < take)
                {
                    var n = fs.Read(buf, read, take - read);
                    if (n <= 0) break;
                    read += n;
                }

                var pos = 0;
                if (start > 0)
                {
                    var firstNl = Array.IndexOf(buf, (byte)'\n', 0, read);
                    pos = firstNl < 0 ? read : firstNl + 1;
                }

                while (pos < read)
                {
                    var nl = Array.IndexOf(buf, (byte)'\n', pos, read - pos);
                    var end = nl < 0 ? read : nl;
                    if (end > pos)
                    {
                        try
                        {
                            var line = buf.AsMemory(pos, end - pos);
                            if (pos == 0 && line.Span.StartsWith(Utf8Bom)) line = line[3..];
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            var convId = Str(root, "conversationId");
                            var workspace = Str(root, "workspace");
                            if (!string.IsNullOrWhiteSpace(convId) && !string.IsNullOrWhiteSpace(workspace))
                            {
                                var folder = SanitizeAntigravityLabel(Path.GetFileName(workspace.Replace('\\', '/').TrimEnd('/')), AgyMaxProjectChars);
                                if (folder is not null)
                                    map[convId] = folder;
                            }
                        }
                        catch (JsonException) { }
                    }
                    if (nl < 0) break;
                    pos = nl + 1;
                }
            }
        }
        catch { }

        _agyCliHistoryPath = historyPath;
        _agyCliHistoryWriteUtc = fi.LastWriteTimeUtc;
        _agyCliHistoryLength = fi.Length;
        _nextAgyCliProjectMap = Environment.TickCount64 + AgyCliProjectMapIntervalMs;
        _agyCliProjects = map;
        return map;
    }

    private AgyDetails ReadAntigravityDetailsCached(FileInfo f, bool hasPriorChunks)
    {
        var hasCached = _agyDetails.TryGetValue(f.FullName, out var cached);
        if (hasCached && cached.LastWriteUtc == f.LastWriteTimeUtc && cached.Length == f.Length
            && cached.Details.HasPriorChunks == hasPriorChunks)
        {
            return cached.Details;
        }

        var appended = hasCached && f.Length > cached.Length && cached.Details.HasPriorChunks == hasPriorChunks;
        var details = ReadAntigravityDetails(f.FullName, appended ? cached.Details : null, hasPriorChunks);
        _agyDetails[f.FullName] = (f.LastWriteTimeUtc, f.Length, details);
        return details;
    }

    private static AgyDetails ReadAntigravityDetails(string path, AgyDetails? previous, bool hasPriorChunks)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = fs.Length;
        var start = previous is not null ? Math.Min(previous.ReadOffset, len) : 0L;
        var remaining = len - start;
        var jumped = false;
        if (remaining > AgyMaxReadBytes)
        {
            start = len - AgyMaxReadBytes;
            remaining = AgyMaxReadBytes;
            jumped = true;
        }

        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[remaining];
        var read = 0;
        while (read < buf.Length)
        {
            var n = fs.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }

        var model = previous?.Model;
        var inputTokens = previous?.InputTokens;
        var outputTokens = previous?.OutputTokens;
        var cachedInputTokens = previous?.CachedInputTokens;
        var lastActivityAt = previous?.LastActivityAt;
        var turnStartedAt = previous?.TurnStartedAt;
        var inFlight = previous?.InFlight ?? false;
        var outcome = previous?.Outcome;
        var lastTool = previous?.LastTool;
        var tokensComplete = !hasPriorChunks && !jumped && (previous is null ? start == 0 : previous.TokensComplete);
        if (!tokensComplete)
        {
            inputTokens = null;
            outputTokens = null;
            cachedInputTokens = null;
        }

        if (read == 0)
        {
            return new AgyDetails(model, inputTokens, outputTokens, cachedInputTokens,
                lastActivityAt, turnStartedAt, inFlight, outcome, lastTool, start, tokensComplete, hasPriorChunks);
        }

        var lastNl = Array.LastIndexOf(buf, (byte)'\n', read - 1);
        if (lastNl < 0)
        {
            // Either an oversized single line or a line still being flushed: keep the offset and treat as in flight.
            return new AgyDetails(model, inputTokens, outputTokens, cachedInputTokens,
                lastActivityAt, turnStartedAt, InFlight: true, Outcome: null, lastTool, start, tokensComplete, hasPriorChunks);
        }

        var parseFrom = 0;
        if (jumped && start > 0)
        {
            var firstNl = Array.IndexOf(buf, (byte)'\n', 0, lastNl + 1);
            parseFrom = firstNl + 1;
        }

        var hasTrailingPartial = lastNl < read - 1;
        var parsedAny = false;

        for (var pos = parseFrom; pos <= lastNl;)
        {
            var nl = Array.IndexOf(buf, (byte)'\n', pos, lastNl - pos + 1);
            var end = nl < 0 ? lastNl + 1 : nl;
            if (end > pos)
            {
                try
                {
                    var slice = buf.AsMemory(pos, end - pos);
                    if (pos == 0 && start == 0 && slice.Span.StartsWith(Utf8Bom)) slice = slice[3..];
                    if (!slice.IsEmpty)
                    {
                        using var doc = JsonDocument.Parse(slice);
                        var rec = doc.RootElement;
                        parsedAny = true;
                        var type = Str(rec, "type");
                        var ts = DateTimeOffset.TryParse(Str(rec, "created_at"), out var dt)
                            ? dt.ToUnixTimeMilliseconds()
                            : (long?)null;

                        if (type == "USER_INPUT")
                        {
                            inFlight = true;
                            outcome = null;
                            turnStartedAt = ts ?? turnStartedAt;
                            lastActivityAt = ts ?? lastActivityAt;
                        }
                        else if (type == "PLANNER_RESPONSE")
                        {
                            model = SanitizeAntigravityLabel(Str(rec, "model"), AgyMaxMetaChars) ?? model;
                            var hasToolCalls = false;
                            if (rec.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array && tcs.GetArrayLength() > 0)
                            {
                                hasToolCalls = true;
                                foreach (var tc in tcs.EnumerateArray())
                                {
                                    var toolName = SanitizeAntigravityLabel(Str(tc, "name"), AgyMaxMetaChars);
                                    if (toolName is not null) lastTool = toolName;
                                }
                            }

                            if (tokensComplete)
                            {
                                var inp = Long(rec, "input_tokens");
                                var outp = Long(rec, "output_tokens");
                                var cache = Long(rec, "cache_read_tokens");
                                if (inp is not null || outp is not null || cache is not null)
                                {
                                    inputTokens = (inputTokens ?? 0) + (inp ?? 0);
                                    outputTokens = (outputTokens ?? 0) + (outp ?? 0);
                                    cachedInputTokens = (cachedInputTokens ?? 0) + (cache ?? 0);
                                }
                            }

                            var stepStatus = Str(rec, "status");
                            if (stepStatus == "ERROR") // a failed response ends the turn, even with tool calls in it
                            {
                                inFlight = false;
                                outcome = "failed";
                            }
                            else if (stepStatus == "RUNNING" || hasToolCalls)
                            {
                                inFlight = true;
                                outcome = null;
                            }
                            else
                            {
                                inFlight = false;
                                outcome = "completed";
                            }
                            lastActivityAt = ts ?? lastActivityAt;
                        }
                        else if (type == "ERROR_MESSAGE")
                        {
                            inFlight = false;
                            outcome = "failed";
                            lastActivityAt = ts ?? lastActivityAt;
                        }
                        else
                        {
                            lastActivityAt = ts ?? lastActivityAt;
                        }
                    }
                }
                catch (JsonException) { }
            }
            if (nl < 0) break;
            pos = nl + 1;
        }

        if (hasTrailingPartial || (jumped && !parsedAny))
        {
            inFlight = true;
            outcome = null;
        }

        return new AgyDetails(model, inputTokens, outputTokens, cachedInputTokens,
            lastActivityAt, turnStartedAt, inFlight, outcome, lastTool, start + lastNl + 1, tokensComplete, hasPriorChunks);
    }

    private static string? SanitizeAntigravityLabel(string? raw, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (var ch in raw)
        {
            if (char.IsControl(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format)
                return null;
        }
        var trimmed = raw.Trim();
        return trimmed.Length > maxChars ? trimmed[..maxChars] : trimmed;
    }

    private static string? SanitizeAntigravityTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new StringBuilder(Math.Min(raw.Length, MaxTitleChars));
        foreach (var ch in raw)
        {
            if (char.IsControl(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format)
                continue;
            sb.Append(ch);
            if (sb.Length >= MaxTitleChars + 32) break;
        }
        return CleanTitle(sb.ToString());
    }
}
