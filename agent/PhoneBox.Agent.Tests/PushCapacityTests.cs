using System.Buffers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class PushCapacityTests
{
    [Fact]
    public async Task ConcurrentNewKeysCannotExceedTheLiveCapacity()
    {
        var result = await PushCapacityFixture.Run(initial: 199, writers: 8);
        Assert.Equal(1, result.Accepted);
        Assert.Equal(200, result.Count);
    }

    [Theory]
    [InlineData(198, 8, false, false, 2, 200)]
    [InlineData(0, 8, false, false, 8, 8)]
    [InlineData(200, 4, false, false, 0, 200)]
    [InlineData(200, 4, true, false, 4, 200)]
    [InlineData(199, 8, false, true, 8, 200)]
    public async Task CapacityChecksPreserveFreeSlotsAndExistingKeys(int initial, int writers, bool replace, bool sharedKey, int accepted, int count)
    {
        var result = await PushCapacityFixture.Run(initial, writers, replace, sharedKey);
        Assert.Equal(accepted, result.Accepted);
        Assert.Equal(count, result.Count);
    }
}

public sealed record PushCapacityResult(int Accepted, int Count);

// A controlled input-memory boundary holds every JSON Clone until all writers reach it. Before the fix,
// each writer has already checked the same free slot; the release forces all of them to publish afterward.
public static class PushCapacityFixture
{
    public static async Task<PushCapacityResult> Run(int initial, int writers, bool replace = false, bool sharedKey = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-push-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(dir, "data.json"));
        var timer = (System.Threading.Timer)typeof(PushStore).GetField("_saveTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        using var memory = new GatedJsonMemory(writers);
        using var document = JsonDocument.Parse(memory.Memory);
        var value = JsonSerializer.SerializeToElement(1);
        for (var i = 0; i < initial; i++)
            if (store.Set("seed-" + i, value, null) is not null) throw new Exception("Initial population failed");
        memory.Armed = true;
        var results = new string?[writers];
        var failures = new Exception?[writers];
        var threads = Enumerable.Range(0, writers).Select(i => new Thread(() =>
        {
            try { results[i] = store.Set(replace ? "seed-" + i : sharedKey ? "shared" : "writer-" + i, document.RootElement, null); }
            catch (Exception ex) { failures[i] = ex; }
        }) { IsBackground = true }).ToArray();
        try
        {
            foreach (var thread in threads) thread.Start();
            if (!memory.Ready.Wait(TimeSpan.FromSeconds(10))) throw new Exception("JSON input gate not reached by every writer");
            memory.Release.Set();
            foreach (var thread in threads)
                if (!thread.Join(TimeSpan.FromSeconds(10))) throw new Exception("Writer did not finish");
            if (failures.FirstOrDefault(e => e is not null) is { } failure) throw failure;
            return new(results.Count(r => r is null), store.Snapshot().Count);
        }
        finally
        {
            memory.Release.Set();
            foreach (var thread in threads) if (thread.IsAlive) thread.Join(TimeSpan.FromSeconds(10));
            await timer.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class GatedJsonMemory(int writers) : MemoryManager<byte>
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes("1");
        private readonly ThreadLocal<bool> _reached = new(() => false);
        public readonly CountdownEvent Ready = new(writers);
        public readonly ManualResetEventSlim Release = new(false);
        public volatile bool Armed;

        public override Span<byte> GetSpan()
        {
            if (Armed && !_reached.Value)
            {
                _reached.Value = true;
                Ready.Signal();
                if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new Exception("JSON input gate not released");
            }
            return _bytes;
        }

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { _reached.Dispose(); Ready.Dispose(); Release.Dispose(); }
    }
}
