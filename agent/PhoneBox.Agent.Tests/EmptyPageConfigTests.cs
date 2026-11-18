using PhoneBox.Agent;
using Xunit;

public sealed class EmptyPageConfigTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[null,null]")]
    public void AnEffectivelyEmptyPageListRestoresUsableDefaults(string pages)
    {
        WithStore(store =>
        {
            Assert.Null(store.Save("{\"pages\":" + pages + "}"));
            var config = store.Get();
            Assert.Equal(ConfigStore.DefaultPages, config.Pages.Select(p => p.Id));
            Assert.Contains(config.Pages, p => p.Enabled);
            Assert.All(config.Pages, p => Assert.NotEmpty(p.Widgets!));
            Assert.Equal(config.Pages.Select(p => p.Id), new ConfigStore(store.Path).Get().Pages.Select(p => p.Id));
        });
    }

    [Fact]
    public void NullEntriesDoNotReplaceAValidCustomPage()
    {
        WithStore(store =>
        {
            Assert.Null(store.Save("""{"pages":[null,{"id":"mine","widgets":[{"type":"clock"}]},null]}"""));
            Assert.Equal("mine", Assert.Single(store.Get().Pages).Id);
        });
    }

    private static void WithStore(Action<ConfigStore> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pb-empty-pages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try { File.WriteAllText(path, "{}"); check(new ConfigStore(path)); }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
