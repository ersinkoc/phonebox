using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class PushEventTests
{
    [Fact]
    public void SetPublishesTheChangedValueWithItsVersionChain()
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-push-event-");
        var store = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(dir.FullName, "data.json"));
        try
        {
            var initial = store.Version;
            Assert.Null(store.Set("fps", PushStore.ParseValue("61"), null));

            var e = Assert.Single(store.Since(0), x => x.Kind == "push");
            var d = e.Data!.Value;
            Assert.Equal(initial, d.GetProperty("baseVersion").GetInt64());
            Assert.Equal(store.Version, d.GetProperty("version").GetInt64());
            Assert.True(d.GetProperty("version").GetInt64() > initial);
            Assert.Equal("fps", d.GetProperty("key").GetString());
            Assert.Equal(61, d.GetProperty("item").GetProperty("data").GetInt32());
            Assert.True(d.GetProperty("item").GetProperty("at").GetInt64() > 0);
        }
        finally
        {
            store.Dispose();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ConcurrentUpdatesKeepTheirEventsInVersionOrder()
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-push-event-");
        var store = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(dir.FullName, "data.json"));
        try
        {
            const int writes = 40;
            Parallel.For(0, writes, new ParallelOptions { MaxDegreeOfParallelism = 8 },
                i => Assert.Null(store.Set("sample", JsonSerializer.SerializeToElement(i), null)));

            var events = store.Since(0).Where(x => x.Kind == "push").ToList();
            Assert.Equal(writes, events.Count);
            for (var i = 1; i < events.Count; i++)
                Assert.Equal(
                    events[i - 1].Data!.Value.GetProperty("version").GetInt64(),
                    events[i].Data!.Value.GetProperty("baseVersion").GetInt64());
            var last = events[^1].Data!.Value.GetProperty("item").GetProperty("data").GetRawText();
            Assert.Equal(last, store.Get("sample")!.Data.GetRawText());
        }
        finally
        {
            store.Dispose();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RemoveAndClearPublishTheirOwnChanges()
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-push-event-");
        var store = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(dir.FullName, "data.json"));
        try
        {
            Assert.Null(store.Set("one", PushStore.ParseValue("1"), null));
            Assert.Null(store.Set("two", PushStore.ParseValue("2"), null));

            var removeBase = store.Version;
            Assert.True(store.Remove("one"));
            var removal = store.Since(0).Last(x => x.Kind == "push").Data!.Value;
            Assert.Equal(removeBase, removal.GetProperty("baseVersion").GetInt64());
            Assert.Equal("one", removal.GetProperty("key").GetString());
            Assert.Equal(JsonValueKind.Null, removal.GetProperty("item").ValueKind);

            var clearBase = store.Version;
            store.Clear();
            var clear = store.Since(0).Last(x => x.Kind == "push").Data!.Value;
            Assert.Equal(clearBase, clear.GetProperty("baseVersion").GetInt64());
            Assert.True(clear.GetProperty("clear").GetBoolean());
        }
        finally
        {
            store.Dispose();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void APushFloodDoesNotEvictNotificationsAndPageSwitches()
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-push-event-");
        var store = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(dir.FullName, "data.json"));
        try
        {
            for (var i = 0; i < 49; i++) store.Emit("notify");
            for (var i = 0; i < 40; i++) Assert.Null(store.Set("sample", JsonSerializer.SerializeToElement(i), null));

            var events = store.Since(0);
            Assert.Equal(50, events.Count);
            Assert.Equal(49, events.Count(x => x.Kind == "notify"));
            Assert.Single(events, x => x.Kind == "push");
        }
        finally
        {
            store.Dispose();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ExpiredValuesArePublishedAsOneRemovalBatch()
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-push-event-");
        var path = Path.Combine(dir.FullName, "data.json");
        File.WriteAllText(path, """{"old":{"Data":"gone","At":1,"ExpiresAt":1},"fresh":{"Data":"stay","At":1}}""");
        var store = new PushStore(NullLogger<PushStore>.Instance, path);
        try
        {
            Assert.DoesNotContain("old", store.Snapshot().Keys);
            var e = Assert.Single(store.Since(0), x => x.Kind == "push");
            var d = e.Data!.Value;
            var removed = Assert.Single(d.GetProperty("remove").EnumerateArray());
            Assert.Equal("old", removed.GetString());
        }
        finally
        {
            store.Dispose();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ATooDeeplyNestedValueIsRefusedWithoutChangingTheStore()
    {
        static JsonElement Nested(int depth) => JsonDocument.Parse(new string('[', depth) + "1" + new string(']', depth)).RootElement.Clone();
        var dir = Directory.CreateTempSubdirectory("phonebox-push-event-");
        var store = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(dir.FullName, "data.json"));
        try
        {
            // 63 levels pass the body parser (64), but the event envelope around the value would not
            var version = store.Version;
            var seq = store.Seq;
            Assert.NotNull(store.Set("deep", Nested(63), null));
            Assert.NotNull(store.Set("deep", Nested(PushStore.MaxDepth + 1), null));
            Assert.Null(store.Get("deep"));
            Assert.Equal(version, store.Version);
            Assert.Equal(seq, store.Seq);
            Assert.Empty(store.Since(0));

            // the deepest accepted value still fits in every answer that carries it: /api/state, the stream, /api/push
            Assert.Null(store.Set("deep", Nested(PushStore.MaxDepth), null));
            var e = Assert.Single(store.Since(0), x => x.Kind == "push");
            Assert.Equal("deep", e.Data!.Value.GetProperty("key").GetString());
            var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            JsonSerializer.Serialize(new { pushVersion = store.Version, events = new[] { e } }, web);
            JsonSerializer.Serialize(new { version = store.Version, items = store.Snapshot() }, web);
        }
        finally
        {
            store.Dispose();
            dir.Delete(recursive: true);
        }
    }
}
