using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Talks to a fake Home Assistant: states are filtered to the widgets' entities, switching maps to services.</summary>
public sealed class HomeAssistantTests : IDisposable
{
    private readonly HttpListener _server;
    private readonly string _url;
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"phonebox-test-{Guid.NewGuid():N}.json");
    private readonly List<string> _calls = [];
    private string _doorState = "locked";
    private readonly Task _loop;

    public HomeAssistantTests()
    {
        _server = TestPorts.Listen();
        _url = _server.Prefixes.Single().TrimEnd('/');
        _loop = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (_server.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _server.GetContextAsync(); } catch { return; }
            var path = ctx.Request.Url!.AbsolutePath;
            string body;
            if (ctx.Request.Headers["Authorization"] != "Bearer secret") { ctx.Response.StatusCode = 401; body = "{}"; }
            else if (path == "/api/states")
                body = $$$"""
                [
                  {"entity_id":"light.desk","state":"on","attributes":{"friendly_name":"Desk lamp"},"last_changed":"2026-11-07T10:00:00+00:00"},
                  {"entity_id":"sensor.temp","state":"21.43","attributes":{"friendly_name":"Office","unit_of_measurement":"°C","device_class":"temperature"}},
                  {"entity_id":"lock.door","state":"{{{_doorState}}}","attributes":{"friendly_name":"Door"}},
                  {"entity_id":"switch.secret","state":"off","attributes":{"friendly_name":"Not in any widget"}}
                ]
                """;
            else
            {
                using var r = new StreamReader(ctx.Request.InputStream);
                var call = path + " " + await r.ReadToEndAsync();
                lock (_calls) _calls.Add(call);
                if (path == "/api/services/lock/unlock") _doorState = "unlocked";
                else if (path == "/api/services/lock/lock") _doorState = "locked";
                body = "[]";
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private HomeAssistant Client(bool control, object? items = null, bool pageEnabled = true)
    {
        var cfg = new
        {
            homeAssistant = new { url = _url, token = "secret" },
            pages = new[]
            {
                new { id = "home", title = "Home", enabled = pageEnabled, widgets = new object[]
                {
                    new { id = "ha", type = "ha", x = 0, y = 0, w = 6, h = 3, options = items is null ? (object)new { entities = "light.desk, sensor.temp, lock.door", control } : new { entities = "switch.secret", control, items } },
                } },
                new { id = "other", title = "Other", enabled = true, widgets = Array.Empty<object>() }, // keeps the config valid when "home" is hidden
            },
        };
        File.WriteAllText(_configPath, JsonSerializer.Serialize(cfg));
        return new HomeAssistant(new ConfigStore(_configPath), NullLogger<HomeAssistant>.Instance);
    }

    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task StatesAreFilteredToWidgetEntities()
    {
        var ha = Client(control: false);
        var r = Json(await ha.StatesAsync(["light.desk", "sensor.temp", "switch.secret"]));
        Assert.True(r.GetProperty("configured").GetBoolean());
        var ids = r.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Equal(["light.desk", "sensor.temp"], ids); // switch.secret is not in any widget
        var temp = r.GetProperty("entities")[1];
        Assert.Equal("21.43", temp.GetProperty("state").GetString());
        Assert.Equal("°C", temp.GetProperty("unit").GetString());
        Assert.Equal("temperature", temp.GetProperty("deviceClass").GetString());
    }

    [Fact]
    public async Task SwitchingNeedsTheWidgetsPermission()
    {
        var ha = Client(control: false);
        Assert.NotNull(await ha.ToggleAsync("light.desk"));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task SwitchingCallsTheRightService()
    {
        var ha = Client(control: true);
        await ha.StatesAsync(["lock.door"]); // the lock's current state decides lock / unlock
        Assert.Null(await ha.ToggleAsync("light.desk"));
        Assert.Null(await ha.ToggleAsync("lock.door"));
        Assert.NotNull(await ha.ToggleAsync("sensor.temp"));    // sensors cannot be switched
        Assert.NotNull(await ha.ToggleAsync("switch.secret"));  // not in a widget
        Assert.Equal(["/api/services/homeassistant/toggle {\"entity_id\":\"light.desk\"}", "/api/services/lock/unlock {\"entity_id\":\"lock.door\"}"], _calls);
    }

    [Fact]
    public async Task ItemsReplaceTheLegacyEntityList()
    {
        var ha = Client(control: true, items: new object[] { new { id = "light.desk" }, new { id = "light.desk" }, new { id = "nodot" }, new { name = "no id" }, "junk" });
        var r = Json(await ha.StatesAsync(["light.desk", "switch.secret"]));
        Assert.Equal(["light.desk"], r.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList()); // switch.secret is only in the ignored legacy string
    }

    [Fact]
    public async Task ANonEmptyItemsListIsFinalEvenWhenNoRowIsUsable()
    {
        var ha = Client(control: true, items: new object[] { new { id = "nodot" }, new { name = "no id" } });
        var r = Json(await ha.StatesAsync(["switch.secret"])); // listed only in the legacy string
        Assert.Empty(r.GetProperty("entities").EnumerateArray());
        Assert.NotNull(await ha.ToggleAsync("switch.secret"));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task TheCapCountsUsableRowsNotAllRows()
    {
        // 300 unusable rows in front must not hide the valid one after them
        var junk = Enumerable.Range(0, 300).Select(i => (object)new { id = $"nodot{i}" });
        var ha = Client(control: true, items: junk.Append(new { id = "light.desk" }).ToArray());
        var r = Json(await ha.StatesAsync(["light.desk"]));
        Assert.Equal(["light.desk"], r.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList());
    }

    [Fact]
    public void AtMostTwoHundredUsableRowsAreRead()
    {
        var items = Enumerable.Range(0, 250).Select(i => new { id = $"sensor.s{i}" }).ToArray();
        var w = new WidgetConfig { Type = "ha", Options = new() { ["items"] = JsonSerializer.SerializeToElement(items) } };
        Assert.Equal(200, HomeAssistant.ItemsOf(w).Count());
    }

    [Fact]
    public async Task StatesOfAHiddenPagesWidgetAreNotRead()
    {
        var ha = Client(control: true, items: new object[] { new { id = "light.desk" } }, pageEnabled: false);
        var r = Json(await ha.StatesAsync(["light.desk"]));
        Assert.Empty(r.GetProperty("entities").EnumerateArray());
    }

    [Fact]
    public async Task AnEmptyItemsListFallsBackToTheLegacyEntityString()
    {
        var ha = Client(control: true, items: Array.Empty<object>());
        var r = Json(await ha.StatesAsync(["switch.secret", "light.desk"]));
        Assert.Equal(["switch.secret"], r.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList());
    }

    [Fact]
    public async Task EachItemHasItsOwnSwitchingPermission()
    {
        // light.desk inherits the widget's flag (on), lock.door opts out, sensor.temp is listed but is not switchable
        var ha = Client(control: true, items: new object[] { new { id = "light.desk" }, new { id = "lock.door", control = false }, new { id = "scene.movie", control = true } });
        await ha.StatesAsync(["lock.door"]);
        Assert.Null(await ha.ToggleAsync("light.desk"));
        Assert.NotNull(await ha.ToggleAsync("lock.door"));
        Assert.Equal(["/api/services/homeassistant/toggle {\"entity_id\":\"light.desk\"}"], _calls);
    }

    [Fact]
    public async Task AnItemCanAllowSwitchingWhenTheWidgetDoesNot()
    {
        var ha = Client(control: false, items: new object[] { new { id = "light.desk", control = true }, new { id = "lock.door" } });
        await ha.StatesAsync(["lock.door"]);
        Assert.Null(await ha.ToggleAsync("light.desk"));
        Assert.NotNull(await ha.ToggleAsync("lock.door")); // no flag of its own: the widget's (off) applies
    }

    [Fact]
    public async Task SwitchingStopsWhenThePageIsHidden()
    {
        var ha = Client(control: true, items: new object[] { new { id = "light.desk", control = true } }, pageEnabled: false);
        Assert.NotNull(await ha.ToggleAsync("light.desk"));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task TheEntityPickerListsEverythingHomeAssistantKnows()
    {
        var ha = Client(control: false);
        var r = Json(await ha.ListEntitiesAsync());
        var all = r.GetProperty("entities").EnumerateArray().ToList();
        Assert.Equal(["light.desk", "lock.door", "sensor.temp", "switch.secret"], all.Select(e => e.GetProperty("id").GetString()).ToList());
        Assert.True(all[0].GetProperty("switchable").GetBoolean());
        Assert.False(all[2].GetProperty("switchable").GetBoolean());
        Assert.Equal("Desk lamp", all[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task TheEntityPickerNeedsAConfiguredHomeAssistant()
    {
        File.WriteAllText(_configPath, "{}");
        var ha = new HomeAssistant(new ConfigStore(_configPath), NullLogger<HomeAssistant>.Instance);
        Assert.False(Json(await ha.ListEntitiesAsync()).GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task TestReportsABadToken()
    {
        Assert.Equal("Connected", await HomeAssistant.TestAsync(_url, "secret"));
        Assert.Equal("The token was rejected", await HomeAssistant.TestAsync(_url, "wrong"));
    }

    [Fact]
    public async Task ToggleHandlesUnconfiguredSettingsColdLockCacheAndWidgetEntityEdits()
    {
        File.WriteAllText(_configPath, """{"pages":[{"id":"home","title":"Home","widgets":[{"id":"ha","type":"ha","x":0,"y":0,"w":6,"h":3,"options":{"entities":"light.desk","control":true}}]}]}""");
        var uncfg = new HomeAssistant(new ConfigStore(_configPath), NullLogger<HomeAssistant>.Instance);
        var err = await uncfg.ToggleAsync("light.desk");
        Assert.NotNull(err);
        Assert.DoesNotContain("Object reference", err, StringComparison.OrdinalIgnoreCase);

        _doorState = "locked";
        var ha = Client(control: true);
        Assert.Null(await ha.ToggleAsync("lock.door"));
        Assert.Null(await ha.ToggleAsync("lock.door"));
        Assert.Equal([
            "/api/services/lock/unlock {\"entity_id\":\"lock.door\"}",
            "/api/services/lock/lock {\"entity_id\":\"lock.door\"}",
        ], _calls);

        File.WriteAllText(_configPath, JsonSerializer.Serialize(new
        {
            homeAssistant = new { url = _url, token = "secret" },
            pages = new[] { new { id = "home", title = "Home", widgets = new object[] { new { id = "ha", type = "ha", x = 0, y = 0, w = 6, h = 3, options = new { entities = "light.desk", control = true } } } } },
        }));
        var store = new ConfigStore(_configPath);
        var cacheHa = new HomeAssistant(store, NullLogger<HomeAssistant>.Instance);
        _ = await cacheHa.StatesAsync(["light.desk"]);

        File.WriteAllText(_configPath, JsonSerializer.Serialize(new
        {
            homeAssistant = new { url = _url, token = "secret" },
            pages = new[] { new { id = "home", title = "Home", widgets = new object[] { new { id = "ha", type = "ha", x = 0, y = 0, w = 6, h = 3, options = new { entities = "light.desk, sensor.temp", control = true } } } } },
        }));
        File.SetLastWriteTimeUtc(_configPath, DateTime.UtcNow.AddSeconds(5));
        var updated = Json(await cacheHa.StatesAsync(["light.desk", "sensor.temp"]));
        Assert.Equal("21.43", updated.GetProperty("entities")[1].GetProperty("state").GetString());
    }

    public void Dispose()
    {
        // On Unix, Stop followed by Close can make HttpListener rebind an already removed endpoint during cleanup.
        _server.Close();
        try { File.Delete(_configPath); } catch { }
    }
}
