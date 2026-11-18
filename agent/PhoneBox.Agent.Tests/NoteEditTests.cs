using System.Globalization;
using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public class NoteEditTests
{
    private static (ConfigStore Store, string Path) Make()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"phonebox-note-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"pages":[{"id":"p","title":"P","widgets":[{"id":"n","type":"note","options":{"text":"old"}},{"id":"c","type":"clock"}]}]}""");
        return (new ConfigStore(path), path);
    }

    private static void Cleanup(string path)
    {
        File.Delete(path);
        File.Delete(path + ".bak");
    }

    [Fact]
    public void SetsTheTextOfANote()
    {
        var (cfg, path) = Make();
        try
        {
            var r = NoteEdit.Apply(cfg, "p", "n", "hello");
            Assert.Equal(200, r.Status);
            var w = cfg.Get().Pages!.First(p => p.Id == "p").Widgets!.First(w => w.Id == "n");
            Assert.Equal("hello", w.Options!["text"].GetString());
            Assert.Equal(cfg.Version.ToString(CultureInfo.InvariantCulture), r.Version);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void AnEmptyTextClearsTheNote()
    {
        var (cfg, path) = Make();
        try
        {
            var r = NoteEdit.Apply(cfg, "p", "n", "");
            Assert.Equal(200, r.Status);
            var w = cfg.Get().Pages!.First(p => p.Id == "p").Widgets!.First(w => w.Id == "n");
            Assert.Equal("", w.Options!["text"].GetString());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void AMissingTextIsRefused()
    {
        var (cfg, path) = Make();
        try { Assert.Equal(400, NoteEdit.Apply(cfg, "p", "n", null).Status); }
        finally { Cleanup(path); }
    }

    [Fact]
    public void RefusesTextOverTheLimit()
    {
        var (cfg, path) = Make();
        try { Assert.Equal(400, NoteEdit.Apply(cfg, "p", "n", new string('x', NoteEdit.MaxLength + 1)).Status); }
        finally { Cleanup(path); }
    }

    [Fact]
    public void UnknownWidgetIsNotFound()
    {
        var (cfg, path) = Make();
        try { Assert.Equal(404, NoteEdit.Apply(cfg, "p", "missing", "hello").Status); }
        finally { Cleanup(path); }
    }

    [Fact]
    public void OtherWidgetTypesAreRefused()
    {
        var (cfg, path) = Make();
        try { Assert.Equal(400, NoteEdit.Apply(cfg, "p", "c", "hello").Status); }
        finally { Cleanup(path); }
    }

    [Fact]
    public void AStaleVersionIsAConflict()
    {
        var (cfg, path) = Make();
        try { Assert.Equal(409, NoteEdit.Apply(cfg, "p", "n", "hello", expectedVersion: -1).Status); }
        finally { Cleanup(path); }
    }
}
