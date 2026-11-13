using System.Net;
using System.Text.Json;
using PhoneBox.Agent;

var endpoint = AgentEndpoint.FromArgs(args);
var Port = endpoint.Port;
string[] SameOrigins = [$"http://127.0.0.1:{Port}", $"http://localhost:{Port}"];

// Install scripts ask where the agent belongs (it differs per OS), then copy it there.
if (args.Contains("--print-install-dir")) { Console.WriteLine(AppPaths.InstallDir); return; }

// Single instance: a second launch opens the running instance's settings page and exits.
// After a restart the previous instance may still be shutting down, so wait for it briefly.
var single = new Mutex(false, endpoint.MutexName);
var waitMs = args.Contains("--restart") ? 15_000 : 0;
bool firstInstance;
try { firstInstance = single.WaitOne(waitMs); }
catch (AbandonedMutexException) { firstInstance = true; }
if (!firstInstance)
{
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://127.0.0.1:{Port}/") { UseShellExecute = true }); } catch { }
    return;
}

if (!args.Contains("--config")) AppPaths.EnsureConfig();
var configPath = args.SkipWhile(a => a != "--config").Skip(1).FirstOrDefault() ?? AppPaths.ConfigPath;
var configStore = new ConfigStore(configPath);
configStore.PersistGenerated(); // keep the Wi-Fi token stable across restarts
var lanCfg = configStore.Get().Lan;
var lanCert = lanCfg.Enabled ? Lan.Certificate() : null;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(IPAddress.Loopback, Port);
    // Wi-Fi / Tailscale: HTTPS with a pinned self-signed certificate, token required (see Lan.cs).
    if (lanCert is not null) k.Listen(IPAddress.Any, lanCfg.Port, l => l.UseHttps(lanCert));
});
builder.Logging.ClearProviders();
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddProvider(new FileLoggerProvider());
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

builder.Services.AddSingleton(configStore);
builder.Services.AddSingleton(endpoint);
builder.Services.AddSingleton<AudioControl>();
builder.Services.AddSingleton<SpotifyControl>();
builder.Services.AddSingleton<ControlDeck>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ControlDeck>());
builder.Services.AddSingleton<PushStore>();
builder.Services.AddSingleton<CommandRunner>();
builder.Services.AddSingleton<ApprovalStore>();
builder.Services.AddSingleton<HomeAssistant>();
AddMonitor<SystemMonitor>(builder.Services);
AddMonitor<MediaMonitor>(builder.Services);
AddMonitor<QuotaMonitor>(builder.Services);
AddMonitor<ProcessMonitor>(builder.Services);
AddMonitor<SessionMonitor>(builder.Services);
AddMonitor<GitHubMonitor>(builder.Services);
AddMonitor<PingMonitor>(builder.Services);
AddMonitor<AdbReverse>(builder.Services);
AddMonitor<SessionLockMonitor>(builder.Services);
AddMonitor<UpdateService>(builder.Services);
AddMonitor<PrivacyMonitor>(builder.Services);
AddMonitor<CalendarMonitor>(builder.Services);
AddMonitor<DockerMonitor>(builder.Services);
AddMonitor<ObsMonitor>(builder.Services);
AddMonitor<BatteryMonitor>(builder.Services);
AddMonitor<RuleEngine>(builder.Services);
AddMonitor<ChargeGuard>(builder.Services);
builder.Services.AddHostedService<Tray>();

var app = builder.Build();

app.MapGet("/api/controls/state", async (ControlDeck deck, CancellationToken ct) => Results.Json(await deck.Snapshot(ct)));
app.MapPost("/api/controls/command/{kind}", async (string kind, string? id, string? op, int? value, bool? muted,
    string? lease, ControlDeck deck, CancellationToken ct) => Results.Json(await deck.Act(kind, id, op, value, muted, lease, ct)));
app.MapGet("/api/controls/listen", async (HttpResponse response, ControlDeck deck, CancellationToken ct) => await deck.Listen(response, ct));
app.MapPost("/api/controls/capture", (ControlDeck deck) => Results.Json(deck.CaptureProfile()));
app.MapGet("/api/controls/task-output/{id}", (string id, ControlDeck deck) => Results.Text(deck.TaskOutput(id), "text/plain"));
app.MapGet("/api/controls/spotify/state", async (SpotifyControl spotify, CancellationToken ct) => Results.Json(await spotify.Snapshot(ct)));
app.MapPost("/api/controls/spotify/action/{action}", async (string action, string? id, SpotifyControl spotify, CancellationToken ct) => Results.Json(await spotify.Act(action, id, ct)));
app.MapGet("/api/controls/spotify/connect", (SpotifyControl spotify) =>
{ try { return Results.Json(new { url = spotify.Begin(), redirectUri = spotify.RedirectUri }); } catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); } });
app.MapPost("/api/controls/spotify/disconnect", async (SpotifyControl spotify, CancellationToken ct) => { await spotify.Disconnect(ct); return Results.Ok(); });
app.MapGet("/api/controls/spotify/callback", async (string? state, string? code, SpotifyControl spotify, CancellationToken ct) =>
{
    try { return state is not null && code is not null && await spotify.Complete(state, code, ct)
        ? Results.Text("Spotify connected. Return to PhoneBox settings; this tab can be closed.", "text/plain")
        : Results.BadRequest("Spotify sign-in expired or was refused. Start again from desktop settings."); }
    catch { return Results.BadRequest("Spotify sign-in failed. Start again from desktop settings."); }
});
app.MapGet("/api/controls/obs", async (ObsMonitor obs, CancellationToken ct) => Results.Json(await obs.DeckSnapshot(ct)));
app.MapPost("/api/controls/obs/{action}", async (string action, string? id, int? value, bool? enabled, ObsMonitor obs, CancellationToken ct) =>
    Results.Json(await obs.DeckAct(action, id, value, enabled, ct)));

// The panel in a desktop browser (the layout editor's live preview, /panel/) calls the API as /agent/api/..., the same
// path the phone uses; on the local port that is simply /api/... Routing runs after this, so it sees the new path.
app.Use((ctx, next) =>
{
    if (ctx.Connection.LocalPort == Port && ctx.Request.Path.StartsWithSegments("/agent/api", out var rest))
        ctx.Request.Path = "/api" + rest;
    return next(ctx);
});

// Security:
//  - Local port 8765 listens on 127.0.0.1 only; the Host check blocks DNS rebinding. The phone's USB proxy sends
//    no Origin header; from browsers only the settings page itself (same origin) is accepted (no CSRF).
//  - The optional Wi-Fi port only serves the phone API, only over TLS, and only with the paired token.
app.UseRouting();
app.Use(async (ctx, next) =>
{
    var origin = ctx.Request.Headers.Origin.ToString();
    if (lanCert is not null && ctx.Connection.LocalPort == lanCfg.Port)
    {
        // The phone's key opens the panel API; the separate webhook key opens only the webhook endpoints, in a header
        // or, for senders that cannot set headers, in ?token= (URLs end up in logs, so it must not be the phone's key).
        var lan = configStore.Get().Lan;
        var authorized = Lan.Matches(ctx.Request.Headers.Authorization, lan.Token!)
            || (Lan.IsWebhook(ctx.Request.Path) && (Lan.Matches(ctx.Request.Headers.Authorization, lan.WebhookToken!)
                || Lan.MatchesQuery(ctx.Request.Query["token"], lan.WebhookToken!)));
        if (origin != "" || !Lan.AllowedOverLan(ctx.Request.Path) || !authorized)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    else
    {
        var host = ctx.Request.Host.Host;
        var spotifyCallback = ctx.Request.Method == "GET" && ctx.Request.Path == "/api/controls/spotify/callback"
            && app.Services.GetRequiredService<SpotifyControl>().HasPendingState(ctx.Request.Query["state"]);
        if ((host != "127.0.0.1" && host != "localhost") || (origin != "" && !SameOrigins.Contains(origin))
            || (!spotifyCallback && BrowserGuard.IsForeign(ctx.Request.Headers["Sec-Fetch-Site"], ctx.Request.Headers["Sec-Fetch-Mode"], ctx.Request.Method, ctx.Request.Path)))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    if (ctx.Request.Path.StartsWithSegments("/api/state") && ctx.Request.Headers.ContainsKey("X-PhoneBox-App"))
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    if (ctx.Request.Headers.TryGetValue("X-PhoneBox-Device", out var device))
    {
        var problem = PhoneRegistry.Touch(device.ToString(), ctx.Request.Headers["X-PhoneBox-Model"].ToString(),
            ctx.Connection.LocalPort == Port ? "USB" : "Wi-Fi", ctx.Request.Headers["X-PhoneBox-WebView"].ToString());
        if (problem is not null) app.Logger.LogWarning("WebView: {Problem}", problem);
    }
    if (ctx.Request.Headers.TryGetValue("X-PhoneBox-App", out var appVersion) && appVersion.ToString().Length is > 0 and < 32)
        UpdateService.AppVersion = appVersion.ToString();
    ctx.Response.Headers.CacheControl = "no-store";
    // only the panel may be framed, and only by the settings page itself (the live preview)
    ctx.Response.Headers["X-Frame-Options"] = ctx.Request.Path.StartsWithSegments("/panel") ? "SAMEORIGIN" : "DENY";
    await next();
});

// ---------------------------------------------------------------- Wi-Fi pairing (local / USB only)
// The phone calls this through the USB tunnel and stores everything it needs to connect over Wi-Fi.
app.MapGet("/api/pair", (ConfigStore cfg) =>
{
    var lan = cfg.Get().Lan;
    if (!lan.Enabled || lanCert is null) return Results.Json(new { enabled = false, pc = Environment.MachineName });
    return Results.Json(new
    {
        enabled = true,
        pc = Environment.MachineName,
        port = lanCfg.Port,
        token = lan.Token,
        fingerprint = Lan.Fingerprint(lanCert),
        hosts = Lan.Addresses(),
    });
});

app.MapGet("/api/lan", (ConfigStore cfg) => Results.Json(new
{
    configured = cfg.Get().Lan.Enabled,
    active = lanCert is not null,
    port = lanCfg.Port,
    restartNeeded = cfg.Get().Lan.Enabled != (lanCert is not null) || (lanCert is not null && cfg.Get().Lan.Port != lanCfg.Port),
    hosts = Lan.Addresses(),
    fingerprint = lanCert is null ? null : Lan.Fingerprint(lanCert),
}));

app.MapGet("/api/update", (UpdateService upd, ConfigStore cfg) => Results.Json(new { status = upd.Status(), auto = cfg.Get().Updates.Auto, installed = UpdateService.RunningInstalled }));
// Settings › General: install the phone app on a phone that does not have it yet (local only).
app.MapPost("/api/phone/install", async (UpdateService upd) =>
    await upd.InstallAppAsync() is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }));
app.MapPost("/api/update/check", async (UpdateService upd) => { await upd.CheckAsync(); return Results.Json(upd.Status()); });
app.MapPost("/api/update/apply", (UpdateService upd) => { _ = Task.Run(() => upd.ApplyAsync()); return Results.Accepted(); });

app.MapPost("/api/restart", (IHostApplicationLifetime life) =>
{
    AppRestart.Requested = true;
    life.StopApplication();
    return Results.Ok();
});

// ---------------------------------------------------------------- settings UI (browser)
app.MapGet("/", () =>
{
    using var s = AppPaths.Resource("settings.html")!;
    using var r = new StreamReader(s);
    return Results.Content(r.ReadToEnd(), "text/html; charset=utf-8");
});
// The phone's panel (embedded at build time) for the live preview in the layout editor; local port only.
app.MapGet("/panel/{*file}", (string? file, HttpRequest req) =>
{
    if (string.IsNullOrEmpty(file) && !req.Path.Value!.EndsWith('/')) return Results.Redirect("/panel/" + req.QueryString);
    file = string.IsNullOrEmpty(file) ? "index.html" : file;
    // index.html, main.js, style.css… and the widget modules in widgets/
    if (!System.Text.RegularExpressions.Regex.IsMatch(file, @"^(widgets/)?[A-Za-z0-9_.-]+$") || file.Contains("..")) return Results.NotFound();
    var type = Path.GetExtension(file) switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        _ => "application/octet-stream",
    };
    return AppPaths.Resource("www/" + file) is { } s ? Results.Stream(s, type) : Results.NotFound();
});
app.MapGet("/favicon.ico", () => AppPaths.Resource("phonebox.ico") is { } s ? Results.Stream(s, "image/x-icon") : Results.NotFound());

// The version lets the settings page save only over what it loaded (X-Config-Version, see ConfigStore.Save).
app.MapGet("/api/settings", (ConfigStore cfg, HttpResponse res) =>
{
    var json = JsonSerializer.Serialize(cfg.Get(), ConfigStore.WriteOpts);
    res.Headers["X-Config-Version"] = cfg.Version.ToString();
    return Results.Text(json, "application/json; charset=utf-8");
});

app.MapPut("/api/settings", async (HttpRequest req, ConfigStore cfg, ILogger<Program> log) =>
{
    if (req.ContentType?.StartsWith("application/json") != true) return Results.StatusCode(415);
    using var r = new StreamReader(req.Body);
    long? expected = long.TryParse(req.Headers["X-Config-Version"], out var v) ? v : null; // scripts may leave it out
    var err = cfg.Save(await r.ReadToEndAsync(), expected);
    if (err == ConfigStore.Conflict) return Results.Json(new { error = err }, statusCode: StatusCodes.Status409Conflict);
    if (err is not null) return Results.BadRequest(new { error = err });
    log.LogInformation("settings saved");
    return Results.Ok(new { version = cfg.Version });
});

app.MapPost("/api/shortcuts/test", async (HttpRequest req, ConfigStore cfg, ILogger<Program> log) =>
{
    if (req.ContentType?.StartsWith("application/json") != true) return Results.StatusCode(415);
    var sc = await req.ReadFromJsonAsync<PhoneBox.Agent.Shortcut>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    if (sc is null || string.IsNullOrWhiteSpace(sc.Cmd)) return Results.BadRequest(new { error = "command is empty" });
    var err = Shortcuts.Execute(sc, log, cfg.Get());
    return err is null ? Results.Ok() : Results.BadRequest(new { error = err });
});

app.MapGet("/api/info", (AdbReverse adb, ConfigStore cfg) => Results.Json(new
{
    version = AppPaths.Version,
    os = AppPaths.Os,
    configPath = cfg.Path,
    logPath = AppPaths.LogPath,
    adb = AdbReverse.FindAdb(cfg.Get().AdbPath),
    connected = adb.Connected,
    devices = adb.AllDevices,
    appMissing = adb.MissingApp,
    usbProblem = adb.UsbProblem,
    webviewProblem = WebViewDoctor.DiagnoseRecent(PhoneRegistry.All(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
}));

app.MapPost("/api/phone/open", async (AdbReverse adb) => { await adb.OpenPanelAsync(); return Results.Ok(); });

// Widget types and page templates for the settings page's layout editor.
app.MapGet("/api/catalog", () => Results.Text($"{{\"cols\":{Catalog.Cols},\"rows\":{Catalog.Rows},\"widgets\":{Catalog.WidgetsJson},\"templates\":{Catalog.TemplatesJson}}}",
    "application/json; charset=utf-8"));

// ---------------------------------------------------------------- webhook API: push data and events into the panel
// POST /api/push/{key}       body: JSON or plain text (or ?value=); optional ?ttl=seconds. Shown by "push" widgets.
// DELETE /api/push/{key}     removes a value; GET /api/push lists them.
// POST /api/notify           {"title","text","level":"info|ok|warn|error","vibrate":true,"page":"<page id>","seconds":5} or plain text
// POST /api/goto/{page}      switches the phone to a page (id, or 1-based number)
// POST /api/refresh          refreshes quotas/GitHub now and makes the phone reload its settings and data; ?reload=1 reloads the whole panel
app.MapGet("/api/push", (PushStore push) => Results.Json(new { version = push.Version, items = push.Snapshot() }));
app.MapGet("/api/push/{key}", (string key, PushStore push) => push.Get(key) is { } item ? Results.Json(item) : Results.NotFound());
app.MapMethods("/api/push/{key}", ["POST", "PUT"], async (string key, int? ttl, HttpRequest req, PushStore push) =>
{
    var (data, error) = await PushStore.ReadBody(req);
    if (error is not null) return Results.BadRequest(new { error });
    error = push.Set(key, data!.Value, ttl);
    return error is null ? Results.Json(new { ok = true, key, version = push.Version }) : Results.BadRequest(new { error });
});
app.MapDelete("/api/push/{key}", (string key, PushStore push) => push.Remove(key) ? Results.Ok() : Results.NotFound());
app.MapDelete("/api/push", (PushStore push) => { push.Clear(); return Results.Ok(); });

app.MapPost("/api/notify", async (HttpRequest req, PushStore push) =>
{
    var (data, error) = await PushStore.ReadBody(req);
    if (error is not null) return Results.BadRequest(new { error });
    var d = data!.Value;
    var payload = d.ValueKind == JsonValueKind.Object ? d : JsonSerializer.SerializeToElement(new { text = d.ToString() });
    if (!payload.TryGetProperty("text", out _) && !payload.TryGetProperty("title", out _)) return Results.BadRequest(new { error = "give a text or a title" });
    return Results.Json(push.Emit("notify", payload));
});

// ?countdown=0 switches at once; otherwise the phone counts down like for a rule.
app.MapPost("/api/goto/{page}", (string page, int? countdown, PushStore push) =>
    Results.Json(push.Emit("goto", JsonSerializer.SerializeToElement(new { page, countdown = countdown is { } c ? Math.Clamp(c, 0, 30) : (int?)null }))));

app.MapPost("/api/refresh", (bool? reload, PushStore push) =>
{
    Wake.Now();
    return Results.Json(push.Emit(reload == true ? "reload" : "refresh"));
});

// ---------------------------------------------------------------- phone panel API
object State(IServiceProvider sp, long? ev)
{
    var upd = sp.GetRequiredService<UpdateService>();
    var push = sp.GetRequiredService<PushStore>();
    return new
    {
        update = upd.Status() is { } u && (u.AppUpdate || u.AgentUpdate) ? new { latest = u.Latest, app = u.AppUpdate, agent = u.AgentUpdate } : null,
        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        host = Environment.MachineName,
        cfgVersion = sp.GetRequiredService<ConfigStore>().Version,
        pushVersion = push.Version,
        evSeq = push.Seq,
        events = ev is { } since ? push.Since(since) : [],
        pc = sp.GetRequiredService<SystemMonitor>().Snapshot(),
        media = sp.GetRequiredService<MediaMonitor>().Snapshot(),
        audio = sp.GetRequiredService<AudioControl>().Snapshot(),
        ping = sp.GetRequiredService<PingMonitor>().Snapshot(),
        rules = sp.GetRequiredService<RuleEngine>().Active(),
        rulesPaused = sp.GetRequiredService<RuleEngine>().PausedUntil(),
        privacy = sp.GetRequiredService<PrivacyMonitor>().Snapshot(),
        obs = sp.GetRequiredService<ObsMonitor>().Snapshot() is { Connected: true } o ? new { o.Streaming, o.Recording, o.RecordPaused } : null,
        toggles = Shortcuts.States(sp.GetRequiredService<ConfigStore>().Get(), push, sp.GetRequiredService<AudioControl>()),
    };
}

// Upcoming calendar events (Agenda widget); the feed URLs stay here.
app.MapGet("/api/calendar", (CalendarMonitor cal) => Results.Json(cal.Snapshot()));

// Docker / Podman containers (Docker widget); start / stop / restart only if a widget allows it.
app.MapGet("/api/docker", async (DockerMonitor docker, CancellationToken ct) =>
    Results.Json(new { snapshot = await docker.SnapshotAsync(ct), control = docker.ControlAllowed() }));
app.MapPost("/api/docker/{id}/{action}", async (string id, string action, DockerMonitor docker) =>
    await docker.ActAsync(id, action) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }));

// Home Assistant (widget): states of the entities the widgets list, and switching those a widget allows.
app.MapGet("/api/ha/states", async (string? ids, HomeAssistant ha) =>
    Results.Json(await ha.StatesAsync((ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))));
app.MapPost("/api/ha/toggle/{id}", async (string id, HomeAssistant ha) =>
    await ha.ToggleAsync(id) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }));
// Settings › General › Home Assistant › Test (local only)
app.MapPost("/api/ha/test", async (HomeAssistantConfig body) =>
    Results.Json(new { result = await HomeAssistant.TestAsync(body.Url ?? "", body.Token ?? "") }));

// OBS Studio (widget): state, scenes and the widget's buttons (only if a widget allows control).
app.MapGet("/api/obs", (ObsMonitor obs, ConfigStore cfg) => Results.Json(new
{
    enabled = cfg.Get().Obs.Enabled,
    snapshot = obs.Snapshot(),
    control = cfg.Get().Widgets().Any(w => w.Type == "obs" && w.Bool("control") == true),
}));
app.MapPost("/api/obs/{action}", async (string action, string? scene, ObsMonitor obs) =>
    await obs.ActAsync(action, scene) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }));
// Settings › General › OBS › Test (local only: deliberately not under /api/obs, which the phone may reach over Wi-Fi)
app.MapPost("/api/obs-test", async (ObsConfig body) => Results.Json(new { result = await ObsMonitor.TestAsync(body.Url, body.Password) }));

// Battery levels of wireless devices (headphones, mice, keyboards…), for the Device batteries widget.
app.MapGet("/api/batteries", (BatteryMonitor b) => Results.Json(new { devices = b.Snapshot() }));

// Settings › Rules › Test: runs a rule's action once (the rule may be unsaved).
app.MapPost("/api/rules/test", (RuleConfig rule, RuleEngine rules) =>
{
    if (!RuleConfig.Actions.Contains(rule.Then)) return Results.BadRequest(new { error = "unknown action" });
    if (string.IsNullOrWhiteSpace(rule.Id)) rule.Id = "test";
    rules.Test(rule);
    return Results.Ok(new { ok = true });
});

// The phone reports its battery (charge guard); the settings show the guard's state and can switch the power to test it.
app.MapPost("/api/phone/battery", (int level, bool plugged, double? temp, string? source, string? usb) => { ChargeGuard.Report(level, plugged, temp, source, usb); return Results.Ok(); });
app.MapGet("/api/charge", (ChargeGuard guard) => Results.Json(guard.Status()));
app.MapPost("/api/charge/test", async (string action, ChargeGuard guard, ConfigStore cfg) =>
    action is "on" or "off" && await guard.Switch(cfg.Get().Charge, action) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }));

// Pause all rules for a while (the phone's overview, the settings); minutes=0 resumes.
app.MapPost("/api/rules/pause", (int? minutes, RuleEngine rules) =>
{
    rules.Pause(minutes ?? 60);
    return Results.Ok(new { pausedUntil = rules.PausedUntil() });
});

app.MapGet("/api/state", (long? ev, HttpContext ctx) => Results.Json(State(ctx.RequestServices, ev)));

// Live stream (server-sent events) for the phone: "state" every second, "event" as soon as one happens, "work" when it
// changes. The Android shell keeps one connection open instead of polling; the panel falls back to polling without it.
app.MapGet("/api/stream", async (HttpContext ctx, PushStore push, SessionMonitor sessions, GitHubMonitor gh, ILogger<Program> log) =>
{
    var ct = ctx.RequestAborted;
    var isPhone = ctx.Request.Headers.ContainsKey("X-PhoneBox-App");
    var via = ctx.Connection.LocalPort == Port ? "USB/local" : "Wi-Fi";
    log.LogInformation("live stream opened ({Who}, {Via})", isPhone ? "phone" : "other client", via);
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    async Task Send(string name, object data)
    {
        await ctx.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, opts)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
    using var wake = new SemaphoreSlim(0);
    void OnEmit() { try { wake.Release(); } catch (ObjectDisposedException) { } }
    push.Emitted += OnEmit;
    try
    {
        var seq = push.Seq;
        var lastWork = "";
        var next = 0L;
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            foreach (var e in push.Since(seq)) { await Send("event", e); seq = e.Seq; }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now >= next)
            {
                if (isPhone) Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, now);
                await Send("state", State(ctx.RequestServices, null));
                if (tick++ % 2 == 0)
                {
                    var work = new { sessions = sessions.Snapshot(), github = gh.Snapshot() };
                    var json = JsonSerializer.Serialize(work, opts);
                    if (json != lastWork) { lastWork = json; await Send("work", work); }
                }
                next = now + 1000;
            }
            await wake.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(5, next - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())), ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (IOException) { }
    finally
    {
        push.Emitted -= OnEmit;
        log.LogInformation("live stream closed ({Via})", via);
    }
});

// Claude Code PermissionRequest hook (local only): waits for Allow / Deny from the phone, prints nothing otherwise.
app.MapPost(ClaudeHook.Endpoint, async (HttpRequest req, ConfigStore cfg, ApprovalStore approvals, SessionMonitor sessions) =>
{
    JsonElement input;
    try { input = (await JsonDocument.ParseAsync(req.Body)).RootElement.Clone(); }
    catch (JsonException) { return Results.Text(""); }
    var output = await ClaudeHook.HandleAsync(input, cfg, approvals, sessions, req.HttpContext.RequestAborted);
    return output is null ? Results.Text("") : Results.Json(output);
});
app.MapGet("/api/claude/hook", () => Results.Json(new { installed = ClaudeHook.Installed(), path = ClaudeHook.SettingsPath }));
app.MapPost("/api/claude/hook", (ConfigStore cfg) => ClaudeHook.Configure(true, Port, cfg.Get().Claude.TimeoutSec) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok());
app.MapDelete("/api/claude/hook", (ConfigStore cfg) => ClaudeHook.Configure(false, Port, cfg.Get().Claude.TimeoutSec) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok());

// Permission prompts waiting for the phone (posted by the Claude Code hook, see ClaudeHook.cs).
app.MapGet("/api/approvals", (ApprovalStore a) => Results.Json(new { items = a.Snapshot() }));
app.MapPost("/api/approvals/{id}", (string id, string decision, ApprovalStore a) => a.Decide(id, decision) ? Results.Ok() : Results.NotFound());

// Output of a command widget (the command itself stays here).
app.MapGet("/api/widget/{page}/{widget}", (string page, string widget, bool? force, CommandRunner runner) =>
    runner.Get(page, widget, force == true) is { } r ? Results.Json(r) : Results.NotFound());

app.MapGet("/api/config", (ConfigStore cfg, HttpRequest req) =>
    Results.Json(new { version = cfg.Version, config = cfg.Get().ToPublic(req.Headers["X-PhoneBox-Device"].ToString()) }));

// Phones that have talked to this agent, with their settings (Settings › Phones).
app.MapGet("/api/phones", (ConfigStore cfg) =>
{
    var c = cfg.Get();
    var seen = PhoneRegistry.All().ToDictionary(p => p.Id);
    var ids = seen.Keys.Union(c.Phones.Select(p => p.Id));
    return Results.Json(ids.Select(id => new
    {
        id,
        model = seen.TryGetValue(id, out var s) ? s.Model : null,
        lastSeen = s?.LastSeen,
        via = s?.Via,
        webView = s?.WebView,
        webviewProblem = s is null ? null : WebViewDoctor.Diagnose(s.WebView, s.Model),
        name = c.Phones.FirstOrDefault(p => p.Id == id)?.Name,
        pages = c.Phones.FirstOrDefault(p => p.Id == id)?.Pages,
    }).OrderByDescending(p => p.lastSeen ?? 0));
});

app.MapGet("/api/art", (MediaMonitor media) =>
    media.Art is { } art ? Results.File(art.Bytes, art.Mime) : Results.NotFound());

app.MapPost("/api/media/{action}", async (string action, long? ms, MediaMonitor media) =>
    await media.Control(action, ms) ? Results.Ok() : Results.BadRequest());

app.MapPost("/api/volume", (int? level, string? op, AudioControl audio) =>
{
    var ok = op switch
    {
        "mute" => audio.ToggleMute(),
        "up" => audio.Step(+5),
        "down" => audio.Step(-5),
        _ when level is { } l => audio.SetVolume(l),
        _ => false,
    };
    return ok ? Results.Json(audio.Snapshot()) : Results.BadRequest();
});

app.MapGet("/api/quotas", (QuotaMonitor q) => Results.Json(new { providers = q.Snapshot() }));

app.MapGet("/api/work", (SessionMonitor s, GitHubMonitor gh) => Results.Json(new
{
    sessions = s.Snapshot(),
    github = gh.Snapshot(),
}));

app.MapGet("/api/procs", (ProcessMonitor procs, int? top) =>
{
    var all = procs.Snapshot();
    return Results.Json(new { total = all.Sum(g => g.Count), groupCount = all.Count, groups = all.Take(Math.Clamp(top ?? 40, 1, 200)) });
});

app.MapGet("/api/procs/icon", (string name, ProcessMonitor procs) =>
    procs.Icon(name) is { } png ? Results.File(png, "image/png") : Results.NotFound());

app.MapGet("/api/procs/detail", (string name, ProcessMonitor procs) => Results.Json(procs.Detail(name)));

// ?name=X ends every instance of X; ?pid=N ends one process together with its children.
app.MapPost("/api/procs/kill", (string? name, int? pid, ProcessMonitor procs, ConfigStore cfg) =>
{
    if (!cfg.Get().AllowKill())
        return Results.Json(new KillResult(0, 0, "Terminating processes is disabled in the settings"), statusCode: 403);
    var r = pid is { } id ? procs.KillPid(id) : procs.Kill(name ?? "");
    return r.Killed > 0 ? Results.Json(r) : Results.Json(r, statusCode: 409);
});

app.MapPost("/api/run/{id}", (string id, ConfigStore cfg, PushStore push, AudioControl audio, ILogger<Program> log) =>
{
    var result = Shortcuts.Run(cfg.Get(), id, log, push, audio);
    return result is null ? Results.Ok() : Results.Problem(result);
});

app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation("PhoneBox agent {Version} ready on http://127.0.0.1:{Port} (config: {Path})", AppPaths.Version, Port, configPath));

app.Run();

// Restart requested from the settings page (e.g. Wi-Fi turned on): hand over to a fresh instance.
single.ReleaseMutex();
single.Dispose();
if (AppRestart.Requested && Environment.ProcessPath is { } exe)
    System.Diagnostics.Process.Start(AppRestart.StartInfo(exe, args));

static void AddMonitor<T>(IServiceCollection s) where T : class, IHostedService
{
    s.AddSingleton<T>();
    s.AddHostedService(sp => sp.GetRequiredService<T>());
}
