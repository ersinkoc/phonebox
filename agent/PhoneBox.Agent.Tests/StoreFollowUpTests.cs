using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Pushed-data saving, icon format changes, page references, feed addresses and the Claude settings file.</summary>
public sealed class StoreFollowUpTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-store-followup-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // ---- PushStore ----

    // A Set that has to wait for a save in progress must not hold up every other Set behind it.
    [Fact]
    public async Task ASetWaitingForASaveDoesNotBlockTheOthers()
    {
        using var store = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "data.json"));
        var persistLock = (Lock)typeof(PushStore).GetField("_persistLock", Private)!.GetValue(store)!;
        var pending = typeof(PushStore).GetField("_savePending", Private)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // a save in progress: the timer callback holds the persist lock (a Lock is released by the thread that took it)
        var saver = new Thread(() => { lock (persistLock) { held.Set(); release.Wait(TimeSpan.FromSeconds(30)); } });
        saver.Start();
        Task? first = null;
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            first = Task.Run(() => store.Set("first", PushStore.ParseValue("1"), null)); // waits for the save to arm its timer
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((int)pending.GetValue(store)! == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
            Assert.Equal(1, (int)pending.GetValue(store)!);
            var second = Task.Run(() => store.Set("second", PushStore.ParseValue("2"), null));
            Assert.True(await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5))) == second, "the second Set waited behind the first one's save");
            Assert.Null(await second);
        }
        finally
        {
            release.Set();
            saver.Join();
            if (first is not null) await first.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal("2", store.Get("second")?.Data.ToString());
    }

    private sealed class FailingLog : ILogger<PushStore>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format)
        {
            if (level >= LogLevel.Warning) throw new IOException("the log disk is full too");
        }
    }

    // On the timer thread an exception ends the process; a logger that fails with the save must not stop the retry.
    [Fact]
    public async Task AFailedSaveIsRetriedEvenWhenItsWarningCannotBeWritten()
    {
        var path = Path.Combine(_dir, "data.json");
        var blocked = Directory.CreateDirectory(path); // the save cannot replace a folder
        using var store = new PushStore(new FailingLog(), path);
        var pending = typeof(PushStore).GetField("_savePending", Private)!;
        pending.SetValue(store, 1); // a change waiting to be saved, without arming the timer from a test thread
        var persist = typeof(PushStore).GetMethod("Persist", Private)!;

        var error = Record.Exception(() => persist.Invoke(store, null)); // the timer callback, run here
        Assert.Null(error);
        Assert.Equal(1, (int)pending.GetValue(store)!);

        blocked.Delete();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path) && !timeout.IsCancellationRequested) await Task.Delay(50);
        Assert.True(File.Exists(path), "the save was not retried");
    }

    // ---- shortcut icon uploads ----

    private static readonly byte[] Png = Convert.FromHexString(
        "89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000a49444154789c63000100000500010d0a2db40000000049454e44ae426082");
    private static readonly byte[] Webp = [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P'];

    // Windows: an open handle can block deleting the old PNG while the new WEBP is moved in. Reads prefer the PNG, so
    // the change must be undone instead of reporting the WEBP's revision while the PNG is served.
    [Fact]
    public void AFormatChangeThatCannotRetireTheOldFileKeepsTheOldIcon()
    {
        if (!OperatingSystem.IsWindows()) return; // elsewhere an open file can be deleted
        var icons = new ShortcutIconUploads(Path.Combine(_dir, "icons"));
        Assert.Null(icons.Save("sc", "image/png", Png));
        var revision = icons.Revision("sc");
        using (new FileStream(Path.Combine(_dir, "icons", "sc.png"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.NotNull(icons.Save("sc", "image/webp", Webp));
        Assert.Equal("image/png", icons.Read("sc")?.ContentType);
        Assert.Equal(revision, icons.Revision("sc"));
        Assert.False(File.Exists(Path.Combine(_dir, "icons", "sc.webp")));

        Assert.Null(icons.Save("sc", "image/webp", Webp)); // nothing holds it open now
        Assert.Equal("image/webp", icons.Read("sc")?.ContentType);
    }

    // ---- page references ----

    [Fact]
    public void APageReferenceWithStraySpacesFindsItsPage()
    {
        var path = Path.Combine(_dir, "config.json");
        var reference = " Desk + Live ";
        var store = new ConfigStore(path);
        Assert.Null(store.Save(JsonSerializer.Serialize(new
        {
            pages = new[] { new { id = "Desk / Live" }, new { id = "Desk + Live" }, new { id = "kitchen" } },
            display = new { homePage = reference },
            phones = new[] { new { id = "desk", pages = new[] { reference } } },
            rules = new[] { new { when = "time", then = "goto", page = reference } },
        })));
        foreach (var config in new[] { store.Get(), new ConfigStore(path).Get() })
        {
            Assert.Equal(["desk-live", "desk-live-2", "kitchen"], config.Pages.Select(p => p.Id));
            Assert.Equal("desk-live-2", config.Display.HomePage);
            Assert.Equal("desk-live-2", string.Join(",", config.PagesFor("desk").Select(p => p.Id)));
            Assert.Equal("desk-live-2", Assert.Single(config.Rules).Page);
        }
    }

    // ---- feed proxy addresses ----

    [Theory]
    [InlineData("3fff::1", false)]            // 3fff::/20 documentation
    [InlineData("3fff:0fff:ffff::1", false)]
    [InlineData("3fff:1000::1", true)]       // just past the documentation block
    [InlineData("2001:db8::1", false)]
    [InlineData("2606:4700::1111", true)]
    public void DocumentationAddressesAreNotPublic(string address, bool expected) =>
        Assert.Equal(expected, WidgetFeedProxy.IsPublicAddress(IPAddress.Parse(address)));

    // ---- Claude Code settings file ----

    // The settings file can hold API keys: its replacement and backup are owner-only, whatever the umask.
    [Fact]
    public void ReplacingClaudeSettingsKeepsThemOwnerOnly()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "env": { "ANTHROPIC_API_KEY": "not-a-real-key" } }""");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Null(ClaudeHook.Configure(true, 18765, 30, path));
        Assert.True(ClaudeHook.Installed(path));
        Assert.Contains("not-a-real-key", File.ReadAllText(path));
        if (OperatingSystem.IsWindows()) return; // Windows keeps the profile's ACLs
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path + ".phonebox.bak"));
    }

    // A hooks value of another shape is the user's to fix: neither installing nor removing may replace or drop it.
    [Theory]
    [InlineData("""{ "hooks": { "PermissionRequest": { "matcher": "*" } } }""")]
    [InlineData("""{ "hooks": ["not", "an", "object"] }""")]
    public void AMalformedHooksValueIsLeftAsItIs(string json)
    {
        var path = Path.Combine(_dir, "settings.json");
        foreach (var install in new[] { false, true })
        {
            File.WriteAllText(path, json);
            Assert.NotNull(ClaudeHook.Configure(install, 18765, 30, path));
            Assert.Equal(json, File.ReadAllText(path));
        }
    }
}
