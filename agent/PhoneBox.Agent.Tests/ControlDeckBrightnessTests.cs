using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// #360: the Display brightness slider must keep the level it was set to. The deck re-reads monitors at most every 30 s,
/// so a set has to show in the next state right away, also when a re-read that started before the set ends after it.
/// </summary>
public sealed class ControlDeckBrightnessTests
{
    private const string Display = @"\\.\DISPLAY1";

    private sealed class Native : IDeckNative
    {
        public volatile int Level = 30;
        /// <summary>When set, Monitors() reads the level, signals Reading and waits for Release before it returns.</summary>
        public ManualResetEventSlim? Release;
        public readonly ManualResetEventSlim Reading = new();
        public DeckAudio Audio() => new(true, null, [], [], null);
        public bool Device(string id, int? volume, bool? muted, int? channel = null) => true;
        public bool Session(string id, int? volume, bool? muted) => true;
        public bool DefaultDevice(string id, int? role = null) => true;
        public IReadOnlyList<DeckMonitor> Monitors()
        {
            var level = Level;
            Reading.Set();
            Release?.Wait(TimeSpan.FromSeconds(10));
            return [new DeckMonitor(Display, Display + " (primary)", level, 1920, 1080, 60)];
        }
        /// <summary>When set, Brightness(HoldValue) applies the level, signals Setting and waits for HoldSet before it returns.</summary>
        public ManualResetEventSlim? HoldSet;
        public int HoldValue = -1;
        public readonly ManualResetEventSlim Setting = new();
        public bool Brightness(string id, int value)
        {
            if (id != Display) return false;
            Level = value;
            if (value == HoldValue) { Setting.Set(); HoldSet?.Wait(TimeSpan.FromSeconds(10)); }
            return true;
        }
        public IReadOnlyList<DeckItem> PowerPlans() => [];
        public Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeckMedia>>([]);
        public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct) => Task.FromResult(true);
        public void Play(string id, string file, string? device, int volume) { }
        public void StopSound(string? id) { }
        public IReadOnlyList<DeckItem> MidiPorts() => [];
        public void Midi(int port, string message, int value) { }
        public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
        public void Dispose() { }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "phonebox-brightness-" + Guid.NewGuid().ToString("N"));
        public readonly Native Native = new();
        public readonly ControlDeck Deck;
        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "config.json");
            File.WriteAllText(path, "{}");
            Deck = new(new ConfigStore(path), Native, NullLogger<ControlDeck>.Instance);
        }
        private static readonly FieldInfo Job = typeof(ControlDeck).GetField("_hardwareJob", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo At = typeof(ControlDeck).GetField("_hardwareAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        public Task HardwareRead => (Task?)Job.GetValue(Deck) ?? Task.CompletedTask;
        /// <summary>Makes the next state ask the computer again, as it does once 30 s have passed.</summary>
        public void Age() => At.SetValue(Deck, Environment.TickCount64 - 31_000);
        public async Task<int?> ShownLevel()
        {
            var state = await Deck.Snapshot(default);
            var monitors = (IReadOnlyList<DeckMonitor>)state.GetType().GetProperty("monitors")!.GetValue(state)!;
            return monitors.Single(m => m.Id == Display).Brightness;
        }
        public void Dispose() { Native.Release?.Set(); Native.HoldSet?.Set(); Deck.Dispose(); Directory.Delete(_directory, true); }
    }

    [Fact]
    public async Task A_set_shows_in_the_next_state_without_waiting_for_the_next_read()
    {
        using var f = new Fixture();
        await f.Deck.Snapshot(default);
        await f.HardwareRead.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(30, await f.ShownLevel());

        Assert.True((await f.Deck.Act("brightness", Display, null, 70, null, null, default)).Ok);

        Assert.Equal(70, await f.ShownLevel());
    }

    [Fact]
    public async Task A_read_that_started_before_the_set_does_not_bring_the_old_level_back()
    {
        using var f = new Fixture();
        await f.Deck.Snapshot(default);
        await f.HardwareRead.WaitAsync(TimeSpan.FromSeconds(10));

        // a re-read starts and reads 30 from the monitor, then the slider sets 70 before the re-read is published
        f.Native.Release = new ManualResetEventSlim();
        f.Native.Reading.Reset();
        f.Age();
        await f.Deck.Snapshot(default);
        Assert.True(f.Native.Reading.Wait(TimeSpan.FromSeconds(10)));
        Assert.True((await f.Deck.Act("brightness", Display, null, 70, null, null, default)).Ok);
        f.Native.Release.Set();
        await f.HardwareRead.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(70, await f.ShownLevel());
    }

    [Fact]
    public async Task Two_sets_at_once_show_the_level_the_monitor_ended_on()
    {
        using var f = new Fixture();
        await f.Deck.Snapshot(default);
        await f.HardwareRead.WaitAsync(TimeSpan.FromSeconds(10));

        // a drag sends 50 and then 70; 50 is already on the monitor but its call has not returned yet
        f.Native.HoldValue = 50;
        f.Native.HoldSet = new ManualResetEventSlim();
        var first = Task.Run(() => f.Deck.Act("brightness", Display, null, 50, null, null, default));
        Assert.True(f.Native.Setting.Wait(TimeSpan.FromSeconds(10)));
        var second = Task.Run(() => f.Deck.Act("brightness", Display, null, 70, null, null, default));
        // Gives the second set time to finish if it can run past the first one. The result does not depend on it:
        // a set that waits for the one before it passes however long this is.
        await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1)));
        f.Native.HoldSet.Set();
        Assert.True((await first).Ok);
        Assert.True((await second).Ok);

        Assert.Equal(70, f.Native.Level);
        Assert.Equal(70, await f.ShownLevel());
    }

    [Fact]
    public async Task A_refused_set_keeps_the_level_read_from_the_monitor()
    {
        using var f = new Fixture();
        await f.Deck.Snapshot(default);
        await f.HardwareRead.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False((await f.Deck.Act("brightness", @"\\.\DISPLAY9", null, 70, null, null, default)).Ok);
        Assert.False((await f.Deck.Act("brightness", Display, null, 101, null, null, default)).Ok);

        Assert.Equal(30, await f.ShownLevel());
    }

    [Fact]
    public async Task A_later_read_shows_a_change_made_on_the_monitor_itself()
    {
        using var f = new Fixture();
        await f.Deck.Snapshot(default);
        await f.HardwareRead.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True((await f.Deck.Act("brightness", Display, null, 70, null, null, default)).Ok);

        f.Native.Level = 45; // changed with the monitor's own buttons
        f.Age();
        await f.Deck.Snapshot(default);
        await f.HardwareRead.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(45, await f.ShownLevel());
    }
}
