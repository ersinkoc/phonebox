using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Regression tests for the monitor defects found in the 0.9.4 audit.</summary>
public sealed class MonitorAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-audit094-" + Guid.NewGuid().ToString("N"));
    public MonitorAuditTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ConfigStore Config(object cfg)
    {
        var path = Path.Combine(_dir, $"config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(cfg));
        return new ConfigStore(path);
    }

    // ------------------------------------------------------------------ 1. calendars sharing a name
    [Fact]
    public async Task CalendarsWithTheSameNameDoNotMultiplyEventsWhileAFeedFails()
    {
        using var server = TestPorts.Listen();
        var url = server.Prefixes.Single();
        var healthy = true;
        var day = DateTime.UtcNow.Date.AddDays(1);
        string Feed(string title) => "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:" + title + $"\r\nDTSTART:{day:yyyyMMdd}T090000Z\r\nDTEND:{day:yyyyMMdd}T093000Z\r\nSUMMARY:" + title + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var loop = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch { return; }
                if (!healthy) ctx.Response.StatusCode = 503;
                else await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(Feed(ctx.Request.Url!.AbsolutePath.Trim('/'))));
                ctx.Response.Close();
            }
        });

        // "Add calendar" in the settings creates entries with an empty name, so two unnamed feeds are the normal case
        var cfg = Config(new { calendars = new[] { new { name = "", url = url + "work" }, new { name = "", url = url + "home" } } });
        var monitor = new CalendarMonitor(cfg, NullLogger<CalendarMonitor>.Instance);
        var refresh = typeof(CalendarMonitor).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task Refresh() => (Task)refresh.Invoke(monitor, [cfg.Get().Calendars, CancellationToken.None])!;

        await Refresh();
        Assert.Equal(["home", "work"], monitor.Snapshot().Events.Select(e => e.Title).Order());

        healthy = false;                       // the network drops (laptop asleep, Wi-Fi down) for half an hour
        for (var i = 0; i < 3; i++) await Refresh();
        server.Close(); // not Stop(): Stop() then Dispose() re-binds the port on macOS and can hit "Address already in use"

        Assert.Equal(2, monitor.Snapshot().Errors.Count);
        Assert.Equal(2, monitor.Snapshot().Events.Count);
        Assert.Equal(["home", "work"], monitor.Snapshot().Events.Select(e => e.Title).Order());
    }

    // ------------------------------------------------------------------ 2. charge guard
    [Fact]
    public async Task ChargeGuardKeepsTryingToRestoreThePowerAfterAFailedSwitchOn()
    {
        using var server = TestPorts.Listen();
        var plugReachable = true;
        var calls = new List<string>();
        var loop = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch { return; }
                lock (calls) calls.Add(ctx.Request.Url!.AbsolutePath);
                if (!plugReachable) ctx.Response.StatusCode = 503;
                ctx.Response.Close();
            }
        });
        var cfg = Config(new
        {
            homeAssistant = new { url = server.Prefixes.Single().TrimEnd('/'), token = "secret" },
            charge = new { enabled = true, min = 50, max = 80, method = "ha", entity = "switch.phone_plug" },
        });
        var guard = new ChargeGuard(cfg, new HomeAssistant(cfg, NullLogger<HomeAssistant>.Instance),
            new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "data.json")), new AudioControl(), NullLogger<ChargeGuard>.Instance);
        var charge = cfg.Get().Charge;

        Assert.Null(await guard.Switch(charge, "off"));        // 80 % reached: the plug is switched off
        plugReachable = false;                                  // Home Assistant restarts / the network drops
        Assert.NotNull(await guard.Switch(charge, "on"));      // the phone went silent: "on" is attempted and fails
        server.Close(); // not Stop(): Stop() then Dispose() re-binds the port on macOS and can hit "Address already in use"

        var status = JsonSerializer.SerializeToElement(guard.Status());
        var power = status.GetProperty("power").GetString();
        var switchedAt = status.GetProperty("switchedAt").GetInt64();
        // the plug is still off, so the guard has to keep asking for "on" (phone silent: r == null)
        Assert.Equal("on", ChargeGuard.Decide(charge, null, switchedAt + 30_000, power, switchedAt));
    }

    // ------------------------------------------------------------------ 3. CodexBar texts under a comma-decimal culture
    [Fact]
    public void CodexBarTextsDoNotDependOnTheWindowsLocale()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var p = CodexBar.Parse("""
                [{"provider":"codex","usage":{"primary":{"usedPercent":12,"windowMinutes":90},"updatedAt":"2026-11-11T10:00:00Z"},"credits":{"remaining":12.5}}]
                """).Single();
            Assert.Equal("Credits: 12.5", p.Extra);
            Assert.Equal("1.5-hour", p.Limits.Single().Label);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    // ------------------------------------------------------------------ 4. a Claude session file whose PID now belongs to another process
    [Fact]
    public void ClaudeSessionFileOfAReusedPidIsNotShownAsALiveSession()
    {
        if (!OperatingSystem.IsWindows()) return;   // procStart is a FILETIME string on Windows ("pidDomain":"win32:...")
        var sessions = Directory.CreateDirectory(Path.Combine(_dir, ".claude", "sessions")).FullName;
        var pid = Environment.ProcessId;            // a live process that is not the Claude Code session that wrote the file
        // Claude Code records the start time of its own process: this session's process started a week before the one that has the PID now
        var started = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().AddDays(-7);
        File.WriteAllText(Path.Combine(sessions, pid + ".json"), JsonSerializer.Serialize(new
        {
            pid, sessionId = "s1", cwd = @"D:\work\demo", startedAt = new DateTimeOffset(started).ToUnixTimeMilliseconds() + 2000,
            procStart = started.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture), pidDomain = "win32:white", status = "waiting", waitingFor = "permission",
        }));
        Assert.Empty(SessionMonitor.ReadClaude(_dir));
    }

    // ------------------------------------------------------------------ 6. a quota window that has reset since the last reading
    [Fact]
    public void QuotaLimitWhoseWindowHasResetIsNotReportedAsUsedUp()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Codex: the last rollout record said 100 % of the 5-hour window, resetting two hours ago; nothing was written since
        // (nobody runs Codex while the panel says the limit is reached)
        var codex = new QuotaProvider("codex", "Codex", "Plus",
            [new QuotaLimit("5-hour", 100, now - 2 * 3600_000, 18000, "critical"), new QuotaLimit("Weekly", 40, now + 3 * 86400_000L, 604800, "normal")],
            "Limit reached", now - 7 * 3600_000, null);
        var monitor = new QuotaMonitor(NullLogger<QuotaMonitor>.Instance);
        typeof(QuotaMonitor).GetField("_codex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(monitor, codex);

        var shown = monitor.Snapshot().Single(p => p.Id == "codex");
        var session = shown.Limits.Single(l => l.Label == "5-hour");
        Assert.Equal(0, session.Percent);                         // the window started over at its reset time
        Assert.DoesNotContain("Limit reached", shown.Extra ?? "");
        Assert.Equal(40, shown.Limits.Single(l => l.Label == "Weekly").Percent);
    }

    [Fact]
    public void CodexRolloutsUnderOneMegabyteDoNotDropFirstLineTurnContextOrRateLimits()
    {
        var readUsage = typeof(QuotaMonitor).GetMethod("ReadCodexUsage", BindingFlags.Static | BindingFlags.NonPublic)!;
        var lastLineWith = typeof(QuotaMonitor).GetMethod("LastLineWith", BindingFlags.Static | BindingFlags.NonPublic)!;

        var usageFile = Path.Combine(_dir, "rollout-line0.jsonl");
        File.WriteAllText(usageFile, string.Join('\n',
            """{"type":"turn_context","payload":{"model":"gpt-5","effort":"high"}}""",
            """{"timestamp":"2026-11-17T10:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":1200}}}}""") + "\n", Encoding.UTF8);

        var usage = (CodexUsage?)readUsage.Invoke(null, [usageFile]);
        Assert.NotNull(usage);
        Assert.Equal("gpt-5", usage!.Model);
        Assert.Equal("high", usage.Effort);

        var rateFile = Path.Combine(_dir, "rollout-rate0.jsonl");
        File.WriteAllText(rateFile, """{"payload":{"rate_limits":{"primary":{"used_percent":42,"window_minutes":300}}}}""" + "\n", Encoding.UTF8);
        Assert.NotNull(lastLineWith.Invoke(null, [rateFile, "\"rate_limits\""]));
    }

    [Fact]
    public void ProcessDetailToleratesDuplicateSessionPidsAndLocksAgentProcess()
    {
        var selfProc = System.Diagnostics.Process.GetCurrentProcess();
        var pid = selfProc.Id;
        var sessionMon = new SessionMonitor(NullLogger<SessionMonitor>.Instance)
        {
            Sessions =
            [
            new AgentSession("claude:s1", "claude", "older", "demo", "idle", null, 1000, 500, Pid: pid),
            new AgentSession("claude:s2", "claude", "newest", "demo", "busy", null, 5000, 500, Pid: pid),
            ],
        };
        var procMon = new ProcessMonitor(NullLogger<ProcessMonitor>.Instance, sessionMon);
        var detail = procMon.Detail(selfProc.ProcessName);
        var inst = Assert.Single(detail.Instances, i => i.Pid == pid);
        Assert.Equal("newest", inst.Session);
        Assert.True(inst.Locked);
        // the group lock depends on every same-name process (another test host may be running), so only check that it
        // follows the instances it was computed from
        if (detail.Instances.All(i => i.Locked)) Assert.True(detail.Locked);
    }

    [Fact]
    public void CodexProjectComesFromALongOrBomPrefixedFirstRecordAndWaitsForAnIncompleteOne()
    {
        var readCwd = typeof(SessionMonitor).GetMethod("ReadCodexCwd", BindingFlags.Static | BindingFlags.NonPublic)!;
        string Cwd(byte[] content)
        {
            var file = Path.Combine(_dir, $"rollout-{Guid.NewGuid():N}.jsonl");
            File.WriteAllBytes(file, content);
            return (string)readCwd.Invoke(null, [file])!;
        }
        static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
        const string Meta = """{"type":"session_meta","payload":{"cwd":"/work/demo","instructions":"__PAD__"}}""";

        // base instructions and tool definitions can make the first record longer than 64 KiB
        var longMeta = Meta.Replace("__PAD__", new string('x', 70 * 1024));
        Assert.Equal("/work/demo", Cwd(Utf8(longMeta + "\n{\"type\":\"event_msg\"}\n")));
        Assert.Equal("/work/demo", Cwd([0xEF, 0xBB, 0xBF, .. Utf8(Meta.Replace("__PAD__", "") + "\n")]));
        // still being written: nothing yet, so the next poll reads it again
        Assert.Equal("", Cwd(Utf8(longMeta)));
    }

    [Fact]
    public void ColdProcessIconDoesNotCacheNullAndShortenArgsHandlesTabSeparators()
    {
        var procMon = new ProcessMonitor(NullLogger<ProcessMonitor>.Instance, new SessionMonitor(NullLogger<SessionMonitor>.Instance));
        const string missing = "missing-proc-f390";
        Assert.Null(procMon.Icon(missing));
        var iconsField = typeof(ProcessMonitor).GetField("_icons", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var iconsDict = (System.Collections.Concurrent.ConcurrentDictionary<string, byte[]?>)iconsField.GetValue(procMon)!;
        Assert.False(iconsDict.ContainsKey(missing));

        Assert.Equal("--daemon", ProcessMonitor.ShortenArgs("tool.exe\t--daemon"));
        Assert.Equal("--port 9000", ProcessMonitor.ShortenArgs("\"C:\\Program Files\\My App\\tool.exe\"\t--port 9000"));
        Assert.Null(ProcessMonitor.ShortenArgs("tool.exe"));
    }

    // ------------------------------------------------------------------ 5. Windows time zone names without ICU (run with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1)
}
