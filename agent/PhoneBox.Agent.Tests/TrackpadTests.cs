using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public class TrackpadTests : IDisposable
{
    private readonly List<KeySender.MouseCall> _calls = [];

    public TrackpadTests()
    {
        KeySender.TestMouseSink = call => _calls.Add(call);
    }

    public void Dispose()
    {
        KeySender.TestMouseSink = null;
    }

    [Fact]
    public void AFailureToActIsReportedToTheCaller()
    {
        // the macOS code reports through the callback it is given, which is how the handler logs it once (no Accessibility, say)
        Exception? seen = null;
        TrackpadHandler.Execute(new TrackpadPayload("swipe3up", null, null, null, null), ex => seen = ex);
        Assert.Null(seen); // the seam stands in for macOS: nothing failed
        Assert.Contains(_calls, c => c.Action == "swipe3up");
    }

    [Fact]
    public void GestureMessagesReachTheGestureSeam()
    {
        TrackpadHandler.Execute(new TrackpadPayload("swipe3up", null, null, null, null));
        TrackpadHandler.Execute(new TrackpadPayload("spread", null, null, null, null));
        TrackpadHandler.Execute(new TrackpadPayload("nonsense", null, null, null, null));
        // only the gesture names get through; what a gesture does is up to the Mac's settings
        Assert.Equal(["swipe3up", "spread"], _calls.Select(c => c.Action).ToArray());
    }

    [Fact]
    public void MouseMethodsInvokeTestSeam()
    {
        KeySender.MouseMove(5, -3);
        KeySender.MouseClick();
        KeySender.MouseDown();
        KeySender.MouseUp();
        KeySender.MouseRightClick();
        KeySender.MouseScroll(120);
        KeySender.MouseHorizontalScroll(-80);

        Assert.Equal(7, _calls.Count);
        Assert.Equal(new KeySender.MouseCall("move", 5, -3, 0), _calls[0]);
        Assert.Equal(new KeySender.MouseCall("click", 0, 0, 0), _calls[1]);
        Assert.Equal(new KeySender.MouseCall("mouseDown", 0, 0, 0), _calls[2]);
        Assert.Equal(new KeySender.MouseCall("mouseUp", 0, 0, 0), _calls[3]);
        Assert.Equal(new KeySender.MouseCall("rightClick", 0, 0, 0), _calls[4]);
        Assert.Equal(new KeySender.MouseCall("scroll", 0, 0, 120), _calls[5]);
        Assert.Equal(new KeySender.MouseCall("hscroll", 0, 0, -80), _calls[6]);
    }

    [Theory]
    [InlineData("move", 10.2, -5.6, null, 10, -6, 0)]
    [InlineData("move", 800.0, -900.0, null, 500, -500, 0)]
    [InlineData("move", -750.0, 600.0, null, -500, 500, 0)]
    public void MoveIsClampedToSaneBounds(string type, double dx, double dy, double? delta, int expectedDx, int expectedDy, int expectedDelta)
    {
        TrackpadHandler.Execute(new TrackpadPayload(type, null, dx, dy, delta));
        Assert.Single(_calls);
        Assert.Equal(new KeySender.MouseCall("move", expectedDx, expectedDy, expectedDelta), _calls[0]);
    }

    [Theory]
    [InlineData("scroll", null, null, 2500.0, 1000)]
    [InlineData("scroll", null, null, -2500.0, -1000)]
    [InlineData("hscroll", null, null, 1500.0, 1000)]
    [InlineData("hscroll", null, null, -1500.0, -1000)]
    public void ScrollIsClampedToSaneBounds(string type, double? dx, double? dy, double delta, int expectedDelta)
    {
        TrackpadHandler.Execute(new TrackpadPayload(type, null, dx, dy, delta));
        Assert.Single(_calls);
        Assert.Equal(expectedDelta, _calls[0].Delta);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("drop_table")]
    [InlineData("exec")]
    public void UnknownActionTypesAreIgnored(string unknownType)
    {
        TrackpadHandler.Execute(new TrackpadPayload(unknownType, null, 10, 10, 50));
        Assert.Empty(_calls);
    }

    [Fact]
    public void NanAndInfinityDoNotTriggerMovement()
    {
        TrackpadHandler.Execute(new TrackpadPayload("move", null, double.NaN, double.PositiveInfinity, null));
        Assert.Empty(_calls);
    }

    [Fact]
    public void ForeignBrowserOriginsAreRefusedByBrowserGuard()
    {
        var path = new PathString("/api/trackpad");
        Assert.True(BrowserGuard.IsForeign("cross-site", "websocket", "GET", path));
        Assert.True(BrowserGuard.IsForeign("same-site", "cors", "GET", path));
        Assert.False(BrowserGuard.IsForeign(null, null, "GET", path));
    }

    [Fact]
    public void IsAllowedRequiresWidgetOnEnabledPageWithControlOption()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var emptyConfig = new AgentConfig();
        Assert.False(TrackpadHandler.IsAllowed(emptyConfig));

        var disabledPageConfig = JsonSerializer.Deserialize<AgentConfig>(
            """{"pages":[{"enabled":false,"widgets":[{"type":"trackpad","options":{"control":true}}]}]}""", opts)!;
        Assert.False(TrackpadHandler.IsAllowed(disabledPageConfig));

        var controlOffConfig = JsonSerializer.Deserialize<AgentConfig>(
            """{"pages":[{"enabled":true,"widgets":[{"type":"trackpad","options":{"control":false}}]}]}""", opts)!;
        Assert.False(TrackpadHandler.IsAllowed(controlOffConfig));

        var controlMissingConfig = JsonSerializer.Deserialize<AgentConfig>(
            """{"pages":[{"enabled":true,"widgets":[{"type":"trackpad"}]}]}""", opts)!;
        Assert.False(TrackpadHandler.IsAllowed(controlMissingConfig));

        var allowedConfig = JsonSerializer.Deserialize<AgentConfig>(
            """{"pages":[{"enabled":true,"widgets":[{"type":"trackpad","options":{"control":true}}]}]}""", opts)!;
        Assert.True(TrackpadHandler.IsAllowed(allowedConfig));
    }

    [Fact]
    public void ProcessPayloadHonorsConfigStore()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "phonebox-trackpad-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tempFile, """{"pages":[{"enabled":true,"widgets":[{"type":"trackpad","options":{"control":false}}]}]}""");
            var store = new ConfigStore(tempFile);

            var json = JsonSerializer.Serialize(new { type = "move", dx = 15, dy = -20 });
            var bytes = Encoding.UTF8.GetBytes(json);

            TrackpadHandler.ProcessPayload(bytes, store);
            Assert.Empty(_calls);

            File.WriteAllText(tempFile, """{"pages":[{"enabled":true,"widgets":[{"type":"trackpad","options":{"control":true}}]}]}""");
            var allowedStore = new ConfigStore(tempFile);

            TrackpadHandler.ProcessPayload(bytes, allowedStore);
            Assert.Single(_calls);
            Assert.Equal(new KeySender.MouseCall("move", 15, -20, 0), _calls[0]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task HandleEndpointReturns403WhenServiceMissingOrControlDisabled()
    {
        // Missing ConfigStore fails closed
        var emptyCtx = new DefaultHttpContext();
        emptyCtx.Request.Method = "POST";
        emptyCtx.Request.Path = "/api/trackpad";
        await TrackpadHandler.HandleEndpoint(emptyCtx);
        Assert.Equal(StatusCodes.Status403Forbidden, emptyCtx.Response.StatusCode);

        // Control disabled returns 403
        var tempFile = Path.Combine(Path.GetTempPath(), "phonebox-trackpad-endpoint-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tempFile, """{"pages":[{"enabled":true,"widgets":[{"type":"trackpad","options":{"control":false}}]}]}""");
            var store = new ConfigStore(tempFile);

            var services = new ServiceCollection();
            services.AddSingleton(store);
            var provider = services.BuildServiceProvider();

            var ctx = new DefaultHttpContext { RequestServices = provider };
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/trackpad";
            var body = Encoding.UTF8.GetBytes("""{"type":"move","dx":5,"dy":-2}""");
            ctx.Request.Body = new MemoryStream(body);
            ctx.Request.ContentLength = body.Length;

            await TrackpadHandler.HandleEndpoint(ctx);
            Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
            Assert.Empty(_calls);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task HandleEndpointReturns405OnGet()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "phonebox-trackpad-get-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tempFile, """{"pages":[{"enabled":true,"widgets":[{"type":"trackpad","options":{"control":true}}]}]}""");
            var store = new ConfigStore(tempFile);

            var services = new ServiceCollection();
            services.AddSingleton(store);
            var provider = services.BuildServiceProvider();

            var ctx = new DefaultHttpContext { RequestServices = provider };
            ctx.Request.Method = "GET";
            ctx.Request.Path = "/api/trackpad";
            ctx.Request.QueryString = new QueryString("?type=click");

            await TrackpadHandler.HandleEndpoint(ctx);
            Assert.Equal(StatusCodes.Status405MethodNotAllowed, ctx.Response.StatusCode);
            Assert.Empty(_calls);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task HandleEndpointExecutesOnPostWithJson()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "phonebox-trackpad-post-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tempFile, """{"pages":[{"enabled":true,"widgets":[{"type":"trackpad","options":{"control":true}}]}]}""");
            var store = new ConfigStore(tempFile);

            var services = new ServiceCollection();
            services.AddSingleton(store);
            var provider = services.BuildServiceProvider();

            var ctx = new DefaultHttpContext { RequestServices = provider };
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/trackpad";
            var body = Encoding.UTF8.GetBytes("""{"type":"move","dx":5.5,"dy":-2.2}""");
            ctx.Request.Body = new MemoryStream(body);
            ctx.Request.ContentLength = body.Length;

            await TrackpadHandler.HandleEndpoint(ctx);
            Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
            Assert.Single(_calls);
            Assert.Equal(new KeySender.MouseCall("move", 6, -2, 0), _calls[0]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
