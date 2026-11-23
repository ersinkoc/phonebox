using System.Collections.Concurrent;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <param name="Tool">The tool Claude Code wants to use (Bash, Edit, Write, WebFetch, an MCP tool…).</param>
/// <param name="Detail">What it wants to do: the command, the file path, the URL…</param>
/// <param name="Tool">The tool the coding agent wants to use (Bash, Edit, Write, WebFetch, an MCP tool…).</param>
/// <param name="Detail">What it wants to do: the command, the file path, the URL…</param>
/// <param name="Kind">"permission" (Allow / Deny) or "question" (an agent question; OpenCode's can be answered on the phone).</param>
/// <param name="Options">For a question: the answers to pick from, in order.</param>
/// <param name="Custom">For a question: a typed answer is accepted too (OpenCode's "custom" field flag).</param>
/// <param name="Always">Offer an Allow always button: true for OpenCode's permission prompts (OpenCode saves the pattern).</param>
/// <param name="AlwaysNotBefore">Earliest moment Allow always may be taken: the countdown, checked here too — the
/// phone's own countdown is a guard rail, not the security check.</param>
/// <param name="AlwaysPattern">What Allow always would save for the project (OpenCode's proposed pattern), shown on
/// the card so the user can see what they would allow from then on.</param>
public sealed record Approval(string Id, string Source, string Session, string Project, string Cwd, string Tool, string Detail,
    string? Description, long CreatedAt, long ExpiresAt, string Kind = "permission",
    ApprovalOption[]? Options = null, bool Custom = false, bool Always = false,
    long AlwaysNotBefore = 0, string? AlwaysPattern = null);

/// <summary>One answer an agent question can be answered with.</summary>
public sealed record ApprovalOption(string Value, string Label, string? Description);

/// <summary>
/// Permission prompts waiting for an answer from the phone. An AI coding-agent hook posts the request and waits; the
/// phone shows Allow / Deny and posts the decision back. If no phone is around, or nobody answers in time, the request
/// ends without a decision and the coding agent asks in its normal UI as usual.
/// </summary>
public sealed class ApprovalStore(PushStore events, ILogger<ApprovalStore> log)
{
    private readonly TimeProvider _clock = TimeProvider.System;
    internal ApprovalStore(PushStore events, ILogger<ApprovalStore> log, TimeProvider clock) : this(events, log) => _clock = clock;

    private sealed record Pending(Approval Approval, TaskCompletionSource<string?> Decision);
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    /// <summary>When the phone last polled the agent; approvals are only offered to a phone that is actually there.</summary>
    public static long PhoneLastSeen;

    public static bool PhoneConnected => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Interlocked.Read(ref PhoneLastSeen) < 5000;

    /// <summary>The requests the phone can still answer; one past its expiry is left out even before it is cleaned up.</summary>
    public IReadOnlyList<Approval> Snapshot()
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        return _pending.Values.Select(p => p.Approval).Where(a => a.ExpiresAt > now).OrderBy(a => a.CreatedAt).ToList();
    }

    /// <summary>Shows the request on the phone and waits for "allow" / "deny"; null = no answer (fall back to the terminal).</summary>
    /// <summary>Notes in the log why a coding agent's prompt was left to its own window instead of the phone.</summary>
    public void Skipped(string source, string reason) =>
        log.LogInformation("{Source} permission prompt left to the computer: {Reason}", source, reason);

    public async Task<string?> AskAsync(Approval a, CancellationToken aborted)
    {
        if (!PhoneConnected)
        {
            // said in the log: a prompt that never reaches the phone is otherwise invisible (#497)
            var seen = Interlocked.Read(ref PhoneLastSeen);
            Skipped(a.Source, seen == 0 ? "the phone has not been seen since the agent started"
                : $"the phone was last seen {Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - seen) / 1000} s ago");
            return null;
        }
        if (a.ExpiresAt <= _clock.GetUtcNow().ToUnixTimeMilliseconds()) return null;
        var pending = new Pending(a, new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
        _pending[a.Id] = pending;
        events.Emit("approval", JsonSerializer.SerializeToElement(new { id = a.Id }));
        log.LogInformation("approval {Id}: {Tool} in {Project} sent to the phone", a.Id, a.Tool, a.Project);
        try
        {
            // exactly the time left: Decide refuses an answer from the expiry on, so waiting longer only delays the terminal
            var timeout = TimeSpan.FromMilliseconds(Math.Max(0, a.ExpiresAt - _clock.GetUtcNow().ToUnixTimeMilliseconds()));
            // The hook process goes away when the user answers in the terminal; that cancels the request here.
            await Task.WhenAny(pending.Decision.Task, Task.Delay(timeout, aborted));
            // The decision task is the one outcome: a timeout or cancel closes it with null, so a Decide that loses
            // the race reports false; a Decide that won before this line is returned even though the wait ended.
            pending.Decision.TrySetResult(null);
            return await pending.Decision.Task;
        }
        catch (OperationCanceledException) { pending.Decision.TrySetResult(null); return null; }
        finally
        {
            // Only this ask's own pending entry goes: a withdrawn offer whose id was re-offered in the meantime
            // must not drop the newer card's pending (same id, newer approval)
            if (_pending.TryGetValue(a.Id, out var current) && ReferenceEquals(current, pending))
                _pending.TryRemove(new KeyValuePair<string, Pending>(a.Id, current));
            events.Emit("approval-done", JsonSerializer.SerializeToElement(new { id = a.Id }));
        }
    }

    public bool Decide(string id, string decision, string? value = null)
    {
        if (!_pending.TryGetValue(id, out var p)) return false;
        var a = p.Approval;
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (a.ExpiresAt <= now) return false;
        // An answer is tied to the card it answers — the phone's countdown is a guard rail, not the security check.
        // A permission card takes allow / deny, and "always" only when the card offers it (OpenCode saves the
        // pattern) and not before the countdown. A question card takes an answer: one of the card's choices, or a
        // typed answer when the card allows one. "skip" ("Got it") ends a question the phone cannot answer, without
        // a decision. Anything else is not this card's answer and is refused.
        string? result;
        if (a.Kind == "question")
        {
            var raw = value ?? "";
            // the card's choices are matched exactly as they were offered (no trimming or capping first); a typed
            // answer is trimmed and capped for the reply
            string Typed(string s) { var t = s.Trim(); return t.Length > 500 ? t[..500] : t; }
            if (decision == "skip" && a.Options is not { Length: > 0 } && !a.Custom) result = "skip";
            else if (decision == "answer" && raw.Length > 0
                && a.Options is { Length: > 0 } opts && Array.Find(opts, o => o.Value == raw) is { } hit)
                result = hit.Value;
            else if (decision == "answer" && a.Custom && Typed(raw) is { Length: > 0 } typed)
                result = typed; // a typed answer — also one that is not among the card's choices; blank is no answer
            else result = null;
        }
        else
        {
            result = decision switch
            {
                "allow" or "deny" => decision,
                "always" when a.Always && now >= a.AlwaysNotBefore => "always",
                _ => null,
            };
        }
        if (result is null)
        {
            log.LogDebug("approval {Id}: refused {Decision} (not an answer this card takes)", id, decision);
            return false;
        }
        // accepted only if it wins against the timeout and the cancel (see AskAsync), and only then logged as such
        if (!p.Decision.TrySetResult(result)) return false;
        log.LogInformation("approval {Id}: {Decision} from the phone", id, decision);
        return true;
    }

    public static string NewId() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
}
