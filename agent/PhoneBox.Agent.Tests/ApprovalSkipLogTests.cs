using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>A permission prompt the phone never gets says why in the log (#497): it was invisible before.</summary>
[Collection("Approval request expiry")] // shares the static PhoneLastSeen with the other approval tests
public sealed class ApprovalSkipLogTests : IDisposable
{
    private sealed class Capture : ILogger<ApprovalStore>
    {
        public readonly List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception));
        }
    }

    private readonly long _phone = Interlocked.Read(ref ApprovalStore.PhoneLastSeen);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-approval-skip-" + Guid.NewGuid().ToString("N"));
    private readonly Capture _log = new();
    private readonly PushStore _push;
    private readonly ApprovalStore _store;

    public ApprovalSkipLogTests()
    {
        Directory.CreateDirectory(_dir);
        _push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "events.json"));
        _store = new ApprovalStore(_push, _log);
    }

    private async Task<object?> Ask(string config)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, config);
        var input = JsonDocument.Parse("""{"session_id":"s","cwd":"/work/p","tool_name":"Bash","tool_input":{"command":"echo test"}}""").RootElement.Clone();
        return await ClaudeHook.HandleAsync(input, new ConfigStore(path), _store, new SessionMonitor(NullLogger<SessionMonitor>.Instance), default);
    }

    [Fact]
    public async Task APhoneThatWasNotSeenIsTheReasonGiven()
    {
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000);
        Assert.Null(await Ask("""{"claude":{"phoneApprovals":true}}"""));
        var line = Assert.Single(_log.Lines);
        Assert.Contains("Claude Code permission prompt left to the computer", line);
        Assert.Matches(@"last seen (59|60|61) s ago", line);
    }

    [Fact]
    public async Task ThePhoneNeverSeenSinceTheStartIsSaidSo()
    {
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, 0);
        Assert.Null(await Ask("""{"claude":{"phoneApprovals":true}}"""));
        Assert.Contains("has not been seen since the agent started", Assert.Single(_log.Lines));
    }

    [Fact]
    public async Task AskOnThePhoneTurnedOffIsTheReasonGiven()
    {
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, long.MaxValue);
        Assert.Null(await Ask("""{"claude":{"phoneApprovals":false}}"""));
        Assert.Contains("Ask on the phone is off", Assert.Single(_log.Lines));
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, _phone);
        _push.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
