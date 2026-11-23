using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// Claude Code and Cursor inside WSL, watched from Windows through the \\wsl.localhost shares. A temporary folder plays
/// the share root: &lt;root&gt;\&lt;distro&gt; holds the distro's /etc, /home and /proc.
/// </summary>
public sealed class WslWatchTests : IDisposable
{
    private readonly string _share = Directory.CreateTempSubdirectory("phonebox-wsl-").FullName;
    public void Dispose() { try { Directory.Delete(_share, true); } catch { } }

    private string Distro(string name, string user = "dev")
    {
        var root = Path.Combine(_share, name);
        Directory.CreateDirectory(Path.Combine(root, "etc"));
        File.WriteAllText(Path.Combine(root, "etc", "wsl.conf"), $"[user]\ndefault={user}\n");
        File.WriteAllText(Path.Combine(root, "etc", "passwd"), $"root:x:0:0:root:/root:/bin/bash\n{user}:x:1000:1000::/home/{user}:/bin/bash\n");
        Directory.CreateDirectory(Path.Combine(root, "home", user));
        return root;
    }

    // a /proc/<pid>/stat line as Linux writes it, with the start time in field 22
    private static void Process(string root, int pid, long start, string comm = "claude")
    {
        var dir = Path.Combine(root, "proc", pid.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stat"),
            $"{pid} ({comm}) S 1 {pid} 1 34820 {pid} 4194304 52591 0 0 0 100 20 0 0 20 0 12 0 {start} 1234567 890 18446744073709551615\n");
    }

    private static void ClaudeSession(string home, int pid, long procStart, string sessionId, string cwd, string status = "busy")
    {
        var dir = Path.Combine(home, ".claude", "sessions");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, pid + ".json"), JsonSerializer.Serialize(new
        {
            pid, sessionId, cwd, startedAt = 1_000L, procStart = procStart.ToString(), name = "dev-1", nameSource = "derived",
            status, updatedAt = 2_000L, statusUpdatedAt = 2_000L,
        }));
    }

    [Fact]
    public void RunningFindsTheDefaultUsersHomeUnderTheShare()
    {
        Distro("Ubuntu-26.04", "kaiut");
        var wsl = Assert.Single(WslHomes.Running(["Ubuntu-26.04"], _share + Path.DirectorySeparatorChar));
        Assert.Equal("Ubuntu-26.04", wsl.Distro);
        Assert.Equal(Path.Combine(_share, "Ubuntu-26.04", "home", "kaiut"), wsl.Home);
    }

    [Fact]
    public void ADistroWhoseUserCannotBeFoundIsSkipped()
    {
        Directory.CreateDirectory(Path.Combine(_share, "Bare"));
        Assert.Empty(WslHomes.Running(["Bare"], _share + Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("15119 (claude) S 14855 15119 14855 34820 15119 4194304 52591 0 0 0 1 2 0 0 20 0 12 0 883762 1 2", 883762L)]
    [InlineData("42 (tmux: server) S 1 42 42 0 -1 4194560 1 0 0 0 0 0 0 0 20 0 1 0 777 1 2", 777L)] // a name with a space
    [InlineData("43 (a) b) S 1 43 43 0 -1 4194560 1 0 0 0 0 0 0 0 20 0 1 0 999 1 2", 999L)]          // and with ")"
    [InlineData("garbage", null)]
    public void StartTimeIsField22(string stat, long? expected) => Assert.Equal(expected, WslHomes.StartTime(stat));

    [Fact]
    public void AliveNeedsTheProcessAndItsStartTime()
    {
        var root = Distro("U");
        Process(root, 15119, 883762);
        Assert.True(WslHomes.Alive(root, 15119, 883762));
        Assert.True(WslHomes.Alive(root, 15119, null));
        Assert.False(WslHomes.Alive(root, 15119, 111)); // a killed session's PID, now another process
        Assert.False(WslHomes.Alive(root, 4242, null)); // no such process
        Assert.False(WslHomes.Alive(root, 0, null));
    }

    [Fact]
    public void ClaudeSessionsInsideWslAreReadWithLinuxProcessChecksAndNoWindowsPid()
    {
        var root = Distro("U");
        var home = Path.Combine(root, "home", "dev");
        ClaudeSession(home, 15119, 883762, "aaaa-live", "/home/dev/phonebox");
        ClaudeSession(home, 15120, 5, "bbbb-reused", "/home/dev/other");  // its PID now belongs to another process
        ClaudeSession(home, 15121, 6, "cccc-gone", "/home/dev/gone");     // no process at all
        Process(root, 15119, 883762);
        Process(root, 15120, 999, "bash");

        var s = Assert.Single(SessionMonitor.ReadClaude(home, linuxAlive: (pid, start) => WslHomes.Alive(root, pid, start)));
        Assert.Equal("claude:aaaa-live", s.Id); // the id the permission hook sends, so prompts still find their session
        Assert.Equal("phonebox", s.Project);
        Assert.Equal("busy", s.Status);
        Assert.Null(s.Pid); // a Linux PID must not be matched against Windows processes
    }

    [Fact]
    public void ClaudeTitlesComeFromTheTranscriptInsideWsl()
    {
        var root = Distro("U");
        var home = Path.Combine(root, "home", "dev");
        ClaudeSession(home, 7, 1, "sess-1", "/home/dev/phonebox");
        Process(root, 7, 1);
        var transcripts = Path.Combine(home, ".claude", "projects", "-home-dev-phonebox");
        Directory.CreateDirectory(transcripts);
        File.WriteAllText(Path.Combine(transcripts, "sess-1.jsonl"), """{"type":"ai-title","aiTitle":"Fix the WSL sessions"}""" + "\n");

        var s = Assert.Single(SessionMonitor.ReadClaude(home, titles: true, linuxAlive: (pid, start) => WslHomes.Alive(root, pid, start)));
        Assert.Equal("Fix the WSL sessions", s.Title);
    }

    [Fact]
    public void AFailedHomeLookupIsTriedAgainOnTheNextRefresh()
    {
        var root = Path.Combine(_share, "Late");
        Directory.CreateDirectory(root); // the share answers, but /etc is not readable yet
        Assert.Empty(WslHomes.Running(["Late"], _share + Path.DirectorySeparatorChar));
        Distro("Late", "kaiut");
        Assert.Single(WslHomes.Running(["Late"], _share + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void TurningASourceOffHidesItsRowsAtOnceAndWslSessionsKeepTheirHome()
    {
        var root = Distro("U");
        var home = Path.Combine(root, "home", "dev");
        ClaudeSession(home, 15119, 883762, "aaaa-live", "/home/dev/phonebox");
        Process(root, 15119, 883762);
        CursorChat(Path.Combine(home, ".cursor", "projects"), "home-dev-site", "44444444-dddd", "assistant");
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        IReadOnlyList<WslHomes.Entry> Running() => [new WslHomes.Entry("U", root, home)];

        Assert.Equal(["claude", "cursor"], monitor.ReadWsl(true, true, Running).Select(s => s.Tool).Order());
        // within the 5-second refresh: the Cursor rows must not linger once Cursor is turned off
        Assert.Equal(["claude"], monitor.ReadWsl(true, false, Running).Select(s => s.Tool));
        // its conversation is read from the distro while it runs, and not at all once it stopped
        Assert.Equal(home, monitor.ConversationHome("claude:aaaa-live", out var usable, _ => true));
        Assert.True(usable);
        Assert.Null(monitor.ConversationHome("claude:aaaa-live", out usable, _ => false));
        Assert.False(usable);
        Assert.Null(monitor.ConversationHome("claude:native", out usable, _ => throw new InvalidOperationException("native: no WSL query")));
        Assert.True(usable);
        Assert.Empty(monitor.ReadWsl(false, false, Running));
        Assert.Null(monitor.WslOf("claude:aaaa-live"));
    }

    private static string CursorChat(string projectsRoot, string slug, string id, string lastRole)
    {
        var dir = Path.Combine(projectsRoot, slug, "agent-transcripts", id);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, id + ".jsonl");
        File.WriteAllText(file, $$"""{"role":"{{lastRole}}","message":"PRIVATE"}""" + "\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
        return file;
    }

    [Fact]
    public void CursorReadsSeveralProjectRootsWithoutForgettingEachOthersCache()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var windows = Path.Combine(_share, "win", "projects");
        var wsl = Path.Combine(Distro("U"), "home", "dev", ".cursor", "projects");
        var winFile = CursorChat(windows, "C-Users-dev-site", "11111111-aaaa", "assistant");
        CursorChat(wsl, "home-dev-nox-tasks", "22222222-bbbb", "user");
        var storage = Path.Combine(_share, "no-storage");

        Assert.Equal("cursor:11111111-aaaa", Assert.Single(monitor.ReadCursor(windows, storage)).Id);
        var inWsl = Assert.Single(monitor.ReadCursor(wsl, storage));
        Assert.Equal("cursor:22222222-bbbb", inWsl.Id);
        Assert.Equal("busy", inWsl.Status);
        // reading the WSL root kept the Windows transcript's parsed role
        var info = new FileInfo(winFile);
        Assert.True(monitor.HasCachedCursorRole(info.FullName, info.LastWriteTimeUtc, info.Length));
    }

    [Fact]
    public void CursorNamesARemoteWslFolderAfterItsLinuxPath()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var wsl = Path.Combine(Distro("U"), "home", "dev", ".cursor", "projects");
        CursorChat(wsl, "home-dev-nox-tasks", "33333333-cccc", "assistant");
        var ws = Path.Combine(_share, "workspaceStorage", "abc");
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, "workspace.json"), """{"folder":"vscode-remote://wsl%2Bubuntu-26.04/home/dev/nox-tasks"}""");

        // without the mapping the name would be the slug's last part, "tasks"
        Assert.Equal("nox-tasks", Assert.Single(monitor.ReadCursor(wsl, Path.Combine(_share, "workspaceStorage"))).Project);
    }
}
