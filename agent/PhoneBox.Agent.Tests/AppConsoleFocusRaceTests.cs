using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// A macro checks the focused app and the saved binding together: an edit saved while the focus is looked up, or one
/// that hands the profile to another app mid-macro, must not send keys the user no longer means.
/// </summary>
public sealed class AppConsoleFocusRaceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-console-race-{Guid.NewGuid():N}.json");
    private readonly List<string> _sent = [];

    private const string Profiles = """
        [{ "id": "ps", "label": "Photoshop", "apps": "photoshop.exe",
           "keys": [{ "label": "Undo", "keys": "ctrl+z" }, { "label": "Save both", "keys": "ctrl+s, ctrl+shift+s" }] }]
        """;

    public void Dispose() { try { File.Delete(_path); File.Delete(_path + ".bak"); } catch { } }

    private AppConsole Make()
    {
        File.WriteAllText(_path, $$"""
            { "pages": [ { "id": "p", "title": "P", "enabled": true,
                "widgets": [ { "id": "c", "type": "app-console", "x": 0, "y": 0, "w": 6, "h": 4, "options": { "control": true } } ] } ],
              "console": { "profiles": {{Profiles}} } }
            """);
        File.SetLastWriteTimeUtc(_path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return new AppConsole(new ConfigStore(_path), NullLogger<AppConsole>.Instance)
        {
            Supported = true,
            AgentElevated = false,
            SendCombo = combo => { _sent.Add(string.Join("+", combo)); return true; },
        };
    }

    private void SaveEdit(string from, string to) => Assert.Null(new ConfigStore(_path).Save(File.ReadAllText(_path).Replace(from, to)));

    [Theory]
    [InlineData("binding", 409, 0)]
    [InlineData("rename", 200, 1)]
    public async Task AnEditSavedWhileTheFocusIsLookedUpIsHonoured(string edit, int status, int count)
    {
        var console = Make();
        var saved = false;
        console.Focused = () =>
        {
            if (!saved)
            {
                saved = true;
                if (edit == "binding") SaveEdit("ctrl+z", "F5");
                else SaveEdit("\"label\": \"Photoshop\"", "\"label\": \"Editor\"");
            }
            return new FocusedApp("photoshop", false);
        };
        var actual = await console.Act(null, "p/c", "ps", "k0", 1, default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(actual.Status == status && _sent.Count == count,
            $"EXPECTED: status={status}, sent={count} | ACTUAL: status={actual.Status}, sent={string.Join(",", _sent)}");
    }

    [Fact]
    public async Task AMacroIsNotRedirectedToAnAppTheProfileWasMovedTo()
    {
        var console = Make();
        var focused = new FocusedApp("photoshop", false);
        console.Focused = () => focused;
        console.SendCombo = combo =>
        {
            _sent.Add(string.Join("+", combo));
            if (_sent.Count == 1)
            {
                // the profile now names Notepad, and the user clicks into Notepad
                SaveEdit("photoshop.exe", "notepad");
                focused = new FocusedApp("notepad", false);
            }
            return true;
        };
        var actual = await console.Act(null, "p/c", "ps", "k1", 1, default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(409, actual.Status);
        Assert.Equal(["ctrl+s"], _sent);
    }
}
