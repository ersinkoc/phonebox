using System.Collections.Concurrent;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <param name="Tool">The tool Claude Code wants to use (Bash, Edit, Write, WebFetch, an MCP tool…).</param>
/// <param name="Detail">What it wants to do: the command, the file path, the URL…</param>
/// <param name="Kind">"permission" (Allow / Deny) or "question" (Claude is asking something; it is answered in the terminal).</param>
public sealed record Approval(string Id, string Source, string Session, string Project, string Tool, string Detail,
    string? Description, long CreatedAt, long ExpiresAt, string Kind = "permission");

/// <summary>
/// Permission prompts waiting for an answer from the phone. A Claude Code hook posts the request and waits; the
/// phone shows Allow / Deny and posts the decision back. If no phone is around, or nobody answers in time, the request
/// ends without a decision and Claude Code asks in the terminal as usual.
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

    public IReadOnlyList<Approval> Snapshot() => _pending.Values.Select(p => p.Approval).OrderBy(a => a.CreatedAt).ToList();

    /// <summary>Shows the request on the phone and waits for "allow" / "deny"; null = no answer (fall back to the terminal).</summary>
    public async Task<string?> AskAsync(Approval a, CancellationToken aborted)
    {
        if (!PhoneConnected || a.ExpiresAt <= _clock.GetUtcNow().ToUnixTimeMilliseconds()) return null;
        var pending = new Pending(a, new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
        _pending[a.Id] = pending;
        events.Emit("approval", JsonSerializer.SerializeToElement(new { id = a.Id }));
        log.LogInformation("approval {Id}: {Tool} in {Project} sent to the phone", a.Id, a.Tool, a.Project);
        try
        {
            var timeout = TimeSpan.FromMilliseconds(Math.Max(1000, a.ExpiresAt - _clock.GetUtcNow().ToUnixTimeMilliseconds()));
            // The hook process goes away when the user answers in the terminal; that cancels the request here.
            var done = await Task.WhenAny(pending.Decision.Task, Task.Delay(timeout, aborted));
            return done == pending.Decision.Task ? await pending.Decision.Task : null;
        }
        catch (OperationCanceledException) { return null; }
        finally
        {
            _pending.TryRemove(a.Id, out _);
            events.Emit("approval-done", JsonSerializer.SerializeToElement(new { id = a.Id }));
        }
    }

    public bool Decide(string id, string decision)
    {
        if (decision is not ("allow" or "deny" or "skip") || !_pending.TryGetValue(id, out var p)) return false;
        if (p.Approval.ExpiresAt <= _clock.GetUtcNow().ToUnixTimeMilliseconds()) return false;
        log.LogInformation("approval {Id}: {Decision} from the phone", id, decision);
        return p.Decision.TrySetResult(decision);
    }

    public static string NewId() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
}
