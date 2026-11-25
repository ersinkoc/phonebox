using PhoneBox.Agent;
using Xunit;

/// <summary>The approvals hook goes into ~/.claude and into each extra Claude Code config folder (other accounts).</summary>
public sealed class ClaudeHookAccountsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-hook-accounts-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void EveryExistingAccountFolderGetsTheHookOnce()
    {
        var main = Path.Combine(_dir, "main-settings.json");
        var work = Directory.CreateDirectory(Path.Combine(_dir, "work")).FullName;
        ClaudeCredentialDir[] extra =
        [
            new() { Label = "work", Dir = work },
            new() { Label = "again", Dir = work },
            new() { Label = "typo", Dir = Path.Combine(_dir, "missing") }, // never created by the hook
        ];

        var paths = ClaudeHook.SettingsPaths(extra, main);
        Assert.Equal([main, Path.Combine(work, "settings.json")], paths);

        foreach (var p in paths) Assert.Null(ClaudeHook.Configure(true, 18765, 30, p));
        Assert.All(paths, p => Assert.True(ClaudeHook.Installed(p)));
        Assert.False(Directory.Exists(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void TheTargetsListEachFileOnceWithWhatInstallWouldDoThere()
    {
        var main = Path.Combine(_dir, "main", "settings.json");
        var work = Directory.CreateDirectory(Path.Combine(_dir, "work")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(_dir, "home")).FullName;
        Assert.Null(ClaudeHook.Configure(true, 18765, 30, Path.Combine(work, "settings.json")));
        ClaudeCredentialDir[] extra =
        [
            new() { Label = "work", Dir = work },
            new() { Label = "again", Dir = Path.Combine(_dir, ".", "work") + Path.DirectorySeparatorChar },
            new() { Label = "home", Dir = home },
            new() { Label = "typo", Dir = Path.Combine(_dir, "missing") },
        ];

        var t = ClaudeHook.Targets(extra, main);

        Assert.Equal([null, "work", "home", "typo"], t.Select(x => x.Label));
        Assert.True(t[1].Installed);
        Assert.False(t[2].Installed);
        Assert.False(t[3].Exists);
        Assert.Equal([main, Path.Combine(work, "settings.json"), Path.Combine(home, "settings.json")], ClaudeHook.SettingsPaths(extra, main));
    }
}
