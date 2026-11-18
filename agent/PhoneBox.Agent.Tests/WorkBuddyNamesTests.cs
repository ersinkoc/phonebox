using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public sealed class WorkBuddyNamesTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "pb-workbuddy-" + Guid.NewGuid());

    private void Workspaces(params (string Path, string Name)[] entries)
    {
        var dir = Path.Combine(_home, ".workbuddy");
        Directory.CreateDirectory(dir);
        var workspaces = entries.ToDictionary(e => e.Path.Replace('\\', '/').ToLowerInvariant(),
            e => (object)new { id = e.Path.Replace('\\', '/').ToLowerInvariant(), path = e.Path, displayName = e.Name, updatedAt = 1L });
        File.WriteAllText(Path.Combine(dir, "workspace-display-names.json"),
            JsonSerializer.Serialize(new { version = 1, workspaces }));
    }

    [Fact]
    public void ReadsTheWorkspaceName()
    {
        Workspaces((@"C:\Users\me\WorkBuddy\2026-11-16-08-55-00", "季度报告整理"));
        Assert.Equal("季度报告整理", WorkBuddyNames.For(@"C:\Users\me\WorkBuddy\2026-11-16-08-55-00", _home));
    }

    [Fact]
    public void IgnoresCaseSlashesAndATrailingSlash()
    {
        Workspaces((@"C:\Users\me\WorkBuddy\2026-11-16-08-55-00", "季度报告整理"));
        Assert.Equal("季度报告整理", WorkBuddyNames.For("c:/users/me/workbuddy/2026-11-16-08-55-00/", _home));
    }

    [Fact]
    public void NullWhenWorkBuddyHasNoNameForTheDirectory()
    {
        Workspaces((@"C:\Users\me\WorkBuddy\other", "Another workspace"));
        Assert.Null(WorkBuddyNames.For(@"C:\Users\me\WorkBuddy\2026-11-16-08-55-00", _home));
    }

    [Fact]
    public void NullWhenTheFileIsMissingOrBroken()
    {
        Assert.Null(WorkBuddyNames.For(@"C:\work\project", _home));      // nothing written yet
        var dir = Path.Combine(_home, ".workbuddy");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workspace-display-names.json"), "{ not json");
        Assert.Null(WorkBuddyNames.For(@"C:\work\project", _home));
    }

    [Fact]
    public void PicksUpANewNameWithoutARestart()
    {
        Workspaces((@"C:\work\project", "First name"));
        Assert.Equal("First name", WorkBuddyNames.For(@"C:\work\project", _home));
        var file = Path.Combine(_home, ".workbuddy", "workspace-display-names.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("First name", "Renamed"));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal("Renamed", WorkBuddyNames.For(@"C:\work\project", _home));
    }

    [Fact]
    public void BrokenWorkspaceFileNeverUsesAnotherHomesNames()
    {
        const string project = @"C:\work\project";
        Workspaces((project, "First workspace"));
        Assert.Equal("First workspace", WorkBuddyNames.For(project, _home));

        var other = Path.Combine(_home, "other");
        var directory = Path.Combine(other, ".workbuddy");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "workspace-display-names.json");
        File.WriteAllText(file, "{ not json");
        var actual = WorkBuddyNames.For(project, other);
        Assert.True(actual is null, $"EXPECTED: no workspace name | ACTUAL: {actual}");
        Assert.Equal("First workspace", WorkBuddyNames.For(project, _home));

        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            workspaces = new Dictionary<string, object> { [project] = new { displayName = "Second name" } },
        }));
        Assert.Equal("Second name", WorkBuddyNames.For(project, other));
        File.WriteAllText(file, "{ half-written");
        Assert.Equal("Second name", WorkBuddyNames.For(project, other));
    }

    [Fact]
    public void AReadThatFailedIsRetriedInsteadOfRemembered()
    {
        Workspaces((@"C:\work\project", "First name"));
        var file = Path.Combine(_home, ".workbuddy", "workspace-display-names.json");
        FileStream held;
        try { held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (IOException) { return; }   // this filesystem allows the second open: nothing to observe
        using (held)
        {
            // This read fails. Remembering that failure as the current state is what would leave the card on the
            // folder name until something happened to touch the file again.
            WorkBuddyNames.For(@"C:\work\project", _home);
        }
        Assert.Equal("First name", WorkBuddyNames.For(@"C:\work\project", _home));
    }

    [Fact]
    public void ThePermissionCardPrefersTheWorkspaceNameOverTheFolder()
    {
        Workspaces((@"C:\Users\me\WorkBuddy\2026-11-16-08-55-00", "季度报告整理"));
        Assert.Equal("季度报告整理", ClaudeHook.ProjectName(@"C:\Users\me\WorkBuddy\2026-11-16-08-55-00", _home));
        // a plain folder without a WorkBuddy entry still shows its own name, as before; built with Path.Combine so
        // the assertion means the same thing on Windows, where a backslash separates, and on Linux, where it does not
        Assert.Equal("project", ClaudeHook.ProjectName(Path.Combine("work", "project"), _home));
    }

    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }
}
