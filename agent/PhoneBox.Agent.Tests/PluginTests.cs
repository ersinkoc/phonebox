using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class PluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"phonebox-plugtest-{Guid.NewGuid():N}");
    private readonly List<PluginService> _services = [];

    private (ConfigStore Cfg, PushStore Push, PluginService Svc, string PluginsDir) CreateEnv(
        HttpMessageHandler? handler = null,
        Func<string, string[]>? indexUrls = null,
        bool allowLoopback = false,
        string? initialConfigJson = null)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cfgPath = Path.Combine(dir, "config.json");
        var pushPath = Path.Combine(dir, "push.json");
        var statePath = Path.Combine(dir, "plugins.json");
        var pluginsDir = Path.Combine(dir, "plugins");
        if (initialConfigJson is not null) File.WriteAllText(cfgPath, initialConfigJson);

        var cfg = new ConfigStore(cfgPath);
        var push = new PushStore(NullLogger<PushStore>.Instance, pushPath);
        var http = handler is null ? null : new HttpClient(handler);
        var svc = new PluginService(
            cfg,
            push,
            NullLogger<PluginService>.Instance,
            pluginsDir,
            statePath,
            http,
            indexUrls,
            allowLoopback);
        _services.Add(svc);
        return (cfg, push, svc, pluginsDir);
    }

    public void Dispose()
    {
        foreach (var svc in _services)
        {
            try { svc.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private static byte[] BuildZip(Dictionary<string, string> entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var w = new StreamWriter(e.Open(), Encoding.UTF8);
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public void ManifestValidationRejectsInvalidIdsTraversalAndMissingPermissions()
    {
        var dir = Path.Combine(_root, "val-plugin");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "run.sh"), "#!/bin/sh\necho '{}'");
        File.WriteAllText(Path.Combine(dir, "view.html"), "<p>ok</p>");

        var valid = new PluginManifest
        {
            Id = "demo-plug",
            Name = "Demo",
            Version = "1.0.0",
            Permissions = ["exec"],
            Backend = new PluginBackendDef { Kind = "command", Runtime = "shell", Entry = "run.sh" },
            Widgets = [new PluginWidgetDef { Type = "plugin:demo-plug.card", Name = "Card", Ui = new PluginUiDef { Kind = "html", File = "view.html" } }],
        };
        Assert.Null(PluginManifestValidator.Validate(valid, dir));

        var badId = JsonSerializer.Deserialize<PluginManifest>(JsonSerializer.Serialize(valid))!;
        badId.Id = "../evil";
        Assert.NotNull(PluginManifestValidator.Validate(badId, dir));

        var badWidgetType = JsonSerializer.Deserialize<PluginManifest>(JsonSerializer.Serialize(valid))!;
        badWidgetType.Widgets[0].Type = "plugin:other.card";
        Assert.NotNull(PluginManifestValidator.Validate(badWidgetType, dir));

        var badEntryTraversal = JsonSerializer.Deserialize<PluginManifest>(JsonSerializer.Serialize(valid))!;
        badEntryTraversal.Backend.Entry = "../outside.sh";
        Assert.NotNull(PluginManifestValidator.Validate(badEntryTraversal, dir));

        var badHtmlTraversal = JsonSerializer.Deserialize<PluginManifest>(JsonSerializer.Serialize(valid))!;
        badHtmlTraversal.Widgets[0].Ui.File = "../outside.html";
        Assert.NotNull(PluginManifestValidator.Validate(badHtmlTraversal, dir));

        var missingExec = JsonSerializer.Deserialize<PluginManifest>(JsonSerializer.Serialize(valid))!;
        missingExec.Permissions = [];
        Assert.Contains("exec", PluginManifestValidator.Validate(missingExec, dir));

        var missingHttp = new PluginManifest
        {
            Id = "api-plug",
            Name = "API",
            Version = "1.0.0",
            Permissions = [],
            Backend = new PluginBackendDef { Kind = "http", Url = "https://example.com/data" },
            Widgets = [new PluginWidgetDef { Type = "plugin:api-plug.stat", Name = "Stat" }],
        };
        Assert.Contains("http", PluginManifestValidator.Validate(missingHttp, dir));
    }

    [Fact]
    public async Task MarketplaceInstallVerifiesPermissionsSha256AndZipSlip()
    {
        var manifestJson = """
        {
          "id": "demo-plug",
          "name": "Demo Plugin",
          "version": "1.2.0",
          "author": "Tester",
          "permissions": ["http"],
          "backend": { "kind": "http", "url": "https://example.test/status" },
          "widgets": [
            { "type": "plugin:demo-plug", "name": "Demo Card", "desc": "Shows status", "w": 4, "h": 2, "minW": 2, "minH": 2 }
          ]
        }
        """;
        var validZip = BuildZip(new() { ["demo-plug/plugin.json"] = manifestJson });
        var validSha = Sha256Hex(validZip);
        var zipSlipZip = BuildZip(new()
        {
            ["plugin.json"] = manifestJson,
            ["../escape.txt"] = "pwned",
        });
        var zipSlipSha = Sha256Hex(zipSlipZip);

        var statusCode = HttpStatusCode.NotFound;
        string indexJson = "";

        var handler = new StubHandler(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u.EndsWith("/index.json"))
            {
                return statusCode == HttpStatusCode.OK
                    ? JsonRes(indexJson)
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (u.EndsWith("/valid.zip")) return BytesRes(validZip);
            if (u.EndsWith("/slip.zip")) return BytesRes(zipSlipZip);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (_, _, svc, pluginsDir) = CreateEnv(handler);

        // 1. 404 index -> status = not_published
        await svc.RefreshMarketplaceAsync();
        using (var ov = ToJson(svc.GetOverview()))
            Assert.Equal("not_published", ov.RootElement.GetProperty("marketplace").GetProperty("status").GetString());

        // 2. Wrong SHA-256 rejected
        statusCode = HttpStatusCode.OK;
        indexJson = $$"""
        {
          "schemaVersion": 1,
          "plugins": [{
            "id": "demo-plug", "name": "Demo Plugin", "version": "1.2.0",
            "permissions": ["http"],
            "downloadUrl": "https://github.com/ersinkoc/phonebox-plugins/releases/download/v1.2.0/valid.zip",
            "sha256": "{{new string('0', 64)}}"
          }]
        }
        """;
        await svc.RefreshMarketplaceAsync();
        var err = await svc.InstallAsync("demo-plug", ["http"]);
        Assert.Contains("SHA-256", err);

        // 3. Unapproved permission rejected
        indexJson = indexJson.Replace(new string('0', 64), validSha);
        await svc.RefreshMarketplaceAsync();
        err = await svc.InstallAsync("demo-plug", []);
        Assert.Contains("permissions", err);

        // 4. Zip-Slip rejected and never escapes pluginsDir
        indexJson = $$"""
        {
          "schemaVersion": 1,
          "plugins": [{
            "id": "demo-plug", "name": "Demo Plugin", "version": "1.2.0",
            "permissions": ["http"],
            "downloadUrl": "https://github.com/ersinkoc/phonebox-plugins/releases/download/v1.2.0/slip.zip",
            "sha256": "{{zipSlipSha}}"
          }]
        }
        """;
        await svc.RefreshMarketplaceAsync();
        err = await svc.InstallAsync("demo-plug", ["http"]);
        Assert.Contains("unsafe path", err);
        Assert.False(File.Exists(Path.Combine(pluginsDir, "escape.txt")));

        // 5. Valid install succeeds and dynamically enters Catalog
        indexJson = $$"""
        {
          "schemaVersion": 1,
          "plugins": [{
            "id": "demo-plug", "name": "Demo Plugin", "version": "1.2.0",
            "permissions": ["http"],
            "downloadUrl": "https://github.com/ersinkoc/phonebox-plugins/releases/download/v1.2.0/valid.zip",
            "sha256": "{{validSha}}"
          }]
        }
        """;
        await svc.RefreshMarketplaceAsync();
        err = await svc.InstallAsync("demo-plug", ["http"]);
        Assert.Null(err);
        Assert.True(Catalog.IsWidget("plugin:demo-plug"));
        Assert.Contains("plugin:demo-plug", Catalog.CombinedJson(svc));
    }

    [Fact]
    public async Task SsrfFilterAndRedirectDowngradeProtection()
    {
        foreach (var blocked in new[]
        {
            "127.0.0.1", "10.0.0.5", "172.16.5.1", "192.168.1.20",
            "169.254.169.254", "100.64.1.1", "0.0.0.1",
            "::1", "fe80::1", "fc00::1", "ff02::1",
            "::ffff:127.0.0.1", "::ffff:169.254.169.254"
        })
        {
            Assert.True(PluginService.IsBlockedExternalIp(IPAddress.Parse(blocked)), $"Expected blocked: {blocked}");
        }

        Assert.False(PluginService.IsBlockedExternalIp(IPAddress.Parse("93.184.216.34")));
        Assert.False(PluginService.IsBlockedExternalIp(IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946")));

        var handler = new StubHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.Found);
            res.Headers.Location = new Uri("http://downgraded.github.io/index.json");
            return res;
        });
        var (_, _, svc, _) = CreateEnv(handler, allowLoopback: false);
        await svc.RefreshMarketplaceAsync();
        using var ov = ToJson(svc.GetOverview());
        var err = ov.RootElement.GetProperty("marketplace").GetProperty("error").GetString();
        Assert.Contains("downgrade", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecretSettingsAndPublicWidgetOptionsStayOffThePhone()
    {
        var (cfg, _, svc, pluginsDir) = CreateEnv();
        var plugDir = Path.Combine(pluginsDir, "gitea-test");
        Directory.CreateDirectory(plugDir);
        File.WriteAllText(Path.Combine(plugDir, "plugin.json"), """
        {
          "id": "gitea-test",
          "name": "Gitea Test",
          "version": "1.0.0",
          "permissions": ["http"],
          "settings": [
            { "key": "baseUrl", "label": "URL", "type": "text" },
            { "key": "token", "label": "Token", "type": "password", "secret": true }
          ],
          "backend": { "kind": "http", "url": "{settings.baseUrl}/api/{options.repo}" },
          "widgets": [{
            "type": "plugin:gitea-test.repo",
            "name": "Gitea",
            "options": [
              { "key": "repo", "label": "Repo", "type": "text" },
              { "key": "widgetSecret", "label": "Widget Secret", "type": "password", "secret": true }
            ]
          }]
        }
        """);
        svc.ReloadPluginsFromDisk();
        Assert.Null(await svc.SetEnabledAsync("gitea-test", true, ["http"]));

        var incoming = new Dictionary<string, JsonElement>
        {
            ["baseUrl"] = JsonSerializer.SerializeToElement("https://git.example.com"),
            ["token"] = JsonSerializer.SerializeToElement("super-secret-token"),
        };
        Assert.Null(await svc.SaveSettingsAsync("gitea-test", incoming));

        using (var ov1 = ToJson(svc.GetOverview()))
        {
            var inst = ov1.RootElement.GetProperty("installed")[0];
            Assert.False(inst.GetProperty("settings").TryGetProperty("token", out _));
            Assert.Equal("token", inst.GetProperty("secretKeysSet")[0].GetString());
        }

        // Resave with SecretPlaceholder preserves existing secret without re-sending it
        var resend = new Dictionary<string, JsonElement>
        {
            ["baseUrl"] = JsonSerializer.SerializeToElement("https://git2.example.com"),
            ["token"] = JsonSerializer.SerializeToElement(PluginService.SecretPlaceholder),
        };
        Assert.Null(await svc.SaveSettingsAsync("gitea-test", resend));

        using (var ov2 = ToJson(svc.GetOverview()))
        {
            var inst = ov2.RootElement.GetProperty("installed")[0];
            Assert.Equal("https://git2.example.com", inst.GetProperty("settings").GetProperty("baseUrl").GetString());
            Assert.Equal("token", inst.GetProperty("secretKeysSet")[0].GetString());
        }

        // Widget options on top-level and inside Smart Stack strip secret options from /api/config
        var rawCfg = """
        {
          "pages": [{
            "id": "p1", "title": "P1",
            "widgets": [
              { "id": "w1", "type": "plugin:gitea-test.repo", "x": 0, "y": 0, "w": 4, "h": 2,
                "options": { "repo": "ersinkoc/phonebox", "widgetSecret": "top-secret" } },
              { "id": "st1", "type": "stack", "x": 4, "y": 0, "w": 4, "h": 2,
                "options": { "widgets": [{ "id": "c1", "type": "plugin:gitea-test.repo", "options": { "repo": "a/b", "widgetSecret": "stack-secret" } }] } }
            ]
          }]
        }
        """;
        Assert.Null(cfg.Save(rawCfg));
        using var pub = ToJson(cfg.Get().ToPublic());
        var widgetsEl = pub.RootElement.GetProperty("pages")[0].GetProperty("widgets");
        var topOpts = widgetsEl[0].GetProperty("options");
        Assert.Equal("ersinkoc/phonebox", topOpts.GetProperty("repo").GetString());
        Assert.False(topOpts.TryGetProperty("widgetSecret", out _));

        var stackChildOpts = widgetsEl[1].GetProperty("options").GetProperty("widgets")[0].GetProperty("options");
        Assert.Equal("a/b", stackChildOpts.GetProperty("repo").GetString());
        Assert.False(stackChildOpts.TryGetProperty("widgetSecret", out _));
    }

    [Fact]
    public async Task HttpBackendInterpolatesBaseUrlAndEnforcesPhoneControlSetting()
    {
        var seenUrls = new List<string>();
        string? seenHeader = null;
        var handler = new StubHandler(req =>
        {
            seenUrls.Add(req.RequestUri!.AbsoluteUri);
            seenHeader = req.Headers.TryGetValues("X-Auth", out var vals) ? string.Join(",", vals) : null;
            if (req.Method == HttpMethod.Post)
                return JsonRes("""{ "value": "synced", "badge": "OK" }""");
            return JsonRes("""{ "value": 7, "unit": "PRs" }""");
        });

        var (cfg, _, svc, pluginsDir) = CreateEnv(handler);
        var plugDir = Path.Combine(pluginsDir, "http-plug");
        Directory.CreateDirectory(plugDir);
        File.WriteAllText(Path.Combine(plugDir, "plugin.json"), """
        {
          "id": "http-plug",
          "name": "HTTP Plug",
          "version": "1.0.0",
          "permissions": ["http"],
          "settings": [
            { "key": "baseUrl", "label": "Base URL", "type": "text" },
            { "key": "key", "label": "Key", "type": "password", "secret": true }
          ],
          "backend": {
            "kind": "http",
            "url": "{settings.baseUrl}/repos/{options.repo}",
            "headers": { "X-Auth": "bearer-{settings.key}" },
            "actions": {
              "sync": { "method": "POST", "url": "{settings.baseUrl}/repos/{options.repo}/sync?arg={action.arg}" }
            }
          },
          "widgets": [{
            "type": "plugin:http-plug",
            "name": "HTTP Widget",
            "options": [
              { "key": "repo", "label": "Repo", "type": "text" },
              { "key": "control", "label": "Control", "type": "bool", "default": true }
            ]
          }]
        }
        """);
        svc.ReloadPluginsFromDisk();
        Assert.Null(await svc.SetEnabledAsync("http-plug", true, ["http"]));
        Assert.Null(await svc.SaveSettingsAsync("http-plug", new()
        {
            ["baseUrl"] = JsonSerializer.SerializeToElement("https://api.example.test/"),
            ["key"] = JsonSerializer.SerializeToElement("secret-99"),
        }));

        Assert.Null(cfg.Save("""
        {
          "pages": [{
            "id": "home", "title": "Home",
            "widgets": [
              { "id": "w1", "type": "plugin:http-plug", "x": 0, "y": 0, "w": 4, "h": 2, "options": { "repo": "ersinkoc/phonebox", "control": true } },
              { "id": "w2", "type": "plugin:http-plug", "x": 4, "y": 0, "w": 4, "h": 2, "options": { "repo": "ersinkoc/phonebox", "control": false } }
            ]
          }]
        }
        """));

        using var st = ToJson(await svc.GetWidgetStateAsync("http-plug", "home", "w1", force: true, CancellationToken.None));
        Assert.True(st.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("https://api.example.test/repos/ersinkoc/phonebox", seenUrls[0]);
        Assert.Equal("bearer-secret-99", seenHeader);
        Assert.Equal(7, st.RootElement.GetProperty("data").GetProperty("value").GetInt32());

        var (actCode, actBody) = await svc.ActAsync("http-plug", "home", "w1", "sync", "a b", CancellationToken.None);
        Assert.Equal(200, actCode);
        using (var actJson = ToJson(actBody))
            Assert.Equal("synced", actJson.RootElement.GetProperty("data").GetProperty("value").GetString());
        Assert.Contains("/repos/ersinkoc/phonebox/sync?arg=a%20b", seenUrls[^1]);

        var (blockedCode, _) = await svc.ActAsync("http-plug", "home", "w2", "sync", "", CancellationToken.None);
        Assert.Equal(403, blockedCode);
    }

    [Fact]
    public async Task CommandSidecarAndBridgeBackendsExecuteEndToEnd()
    {
        var (cfg, _, svc, pluginsDir) = CreateEnv();

        // 1. Command plugin
        var cmdDir = Path.Combine(pluginsDir, "cmd-plug");
        Directory.CreateDirectory(cmdDir);
        var cmdEntry = OperatingSystem.IsWindows() ? "cmd.ps1" : "cmd.sh";
        if (OperatingSystem.IsWindows())
            File.WriteAllText(Path.Combine(cmdDir, cmdEntry), "$null = [Console]::In.ReadToEnd()\nWrite-Output '{\"data\":{\"value\":42,\"unit\":\"ms\"}}'");
        else
            File.WriteAllText(Path.Combine(cmdDir, cmdEntry), "#!/bin/sh\ncat >/dev/null\necho '{\"data\":{\"value\":42,\"unit\":\"ms\"}}'\n");
        File.WriteAllText(Path.Combine(cmdDir, "plugin.json"), $$"""
        {
          "id": "cmd-plug", "name": "Cmd", "version": "1.0.0", "permissions": ["exec"],
          "backend": { "kind": "command", "runtime": "shell", "entry": "{{cmdEntry}}" },
          "widgets": [{ "type": "plugin:cmd-plug", "name": "Cmd Widget" }]
        }
        """);

        // 2. Sidecar plugin
        var sideDir = Path.Combine(pluginsDir, "side-plug");
        Directory.CreateDirectory(sideDir);
        var sideEntry = OperatingSystem.IsWindows() ? "side.ps1" : "side.sh";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(sideDir, sideEntry), """
            while (($line = [Console]::In.ReadLine()) -ne $null) {
              $m = $line | ConvertFrom-Json
              if ($m.type -eq 'poll') { Write-Output (@{ type='reply'; reqId=$m.reqId; data=@{ value=99; sub='from-sidecar' } } | ConvertTo-Json -Compress) }
              elseif ($m.type -eq 'act') { Write-Output (@{ type='reply'; reqId=$m.reqId; ok=$true; data=@{ value=100; op=$m.payload.op } } | ConvertTo-Json -Compress) }
            }
            """);
        }
        else
        {
            File.WriteAllText(Path.Combine(sideDir, sideEntry), """
            #!/bin/sh
            while IFS= read -r line; do
              case "$line" in
                *'"type":"poll"'*)
                  req=$(printf '%s' "$line" | sed -n 's/.*"reqId":"\([^"]*\)".*/\1/p')
                  printf '{"type":"reply","reqId":"%s","data":{"value":99,"sub":"from-sidecar"}}\n' "$req"
                  ;;
                *'"type":"act"'*)
                  req=$(printf '%s' "$line" | sed -n 's/.*"reqId":"\([^"]*\)".*/\1/p')
                  printf '{"type":"reply","reqId":"%s","ok":true,"data":{"value":100}}\n' "$req"
                  ;;
              esac
            done
            """);
        }
        File.WriteAllText(Path.Combine(sideDir, "plugin.json"), $$"""
        {
          "id": "side-plug", "name": "Side", "version": "1.0.0", "permissions": ["exec"],
          "backend": { "kind": "sidecar", "runtime": "shell", "entry": "{{sideEntry}}" },
          "widgets": [{ "type": "plugin:side-plug", "name": "Side Widget" }]
        }
        """);

        // 3. Bridge plugin
        var brDir = Path.Combine(pluginsDir, "bridge-plug");
        Directory.CreateDirectory(brDir);
        File.WriteAllText(Path.Combine(brDir, "plugin.json"), """
        {
          "id": "bridge-plug", "name": "Meet Bridge", "version": "1.0.0", "permissions": ["bridge"],
          "backend": { "kind": "none" },
          "widgets": [{ "type": "plugin:bridge-plug", "name": "Meet" }]
        }
        """);

        svc.ReloadPluginsFromDisk();
        Assert.Null(await svc.SetEnabledAsync("cmd-plug", true, ["exec"]));
        Assert.Null(await svc.SetEnabledAsync("side-plug", true, ["exec"]));
        Assert.Null(await svc.SetEnabledAsync("bridge-plug", true, ["bridge"]));

        Assert.Null(cfg.Save("""
        {
          "pages": [{
            "id": "p1", "title": "P1",
            "widgets": [
              { "id": "c1", "type": "plugin:cmd-plug", "x": 0, "y": 0, "w": 4, "h": 2 },
              { "id": "s1", "type": "plugin:side-plug", "x": 4, "y": 0, "w": 4, "h": 2 },
              { "id": "b1", "type": "plugin:bridge-plug", "x": 8, "y": 0, "w": 4, "h": 2 }
            ]
          }]
        }
        """));

        using (var cmdSt = ToJson(await svc.GetWidgetStateAsync("cmd-plug", "p1", "c1", force: true, CancellationToken.None)))
            Assert.Equal(42, cmdSt.RootElement.GetProperty("data").GetProperty("value").GetInt32());

        using (var sideSt = ToJson(await svc.GetWidgetStateAsync("side-plug", "p1", "s1", force: true, CancellationToken.None)))
            Assert.Equal(99, sideSt.RootElement.GetProperty("data").GetProperty("value").GetInt32());

        var (sideActCode, sideActBody) = await svc.ActAsync("side-plug", "p1", "s1", "ping", null, CancellationToken.None);
        Assert.Equal(200, sideActCode);
        using (var sideActJson = ToJson(sideActBody))
            Assert.Equal(100, sideActJson.RootElement.GetProperty("data").GetProperty("value").GetInt32());

        string token;
        using (var ov = ToJson(svc.GetOverview()))
            token = ov.RootElement.GetProperty("installed").EnumerateArray().First(x => x.GetProperty("id").GetString() == "bridge-plug").GetProperty("bridgeToken").GetString()!;

        Assert.False(svc.AuthorizeBridgeRequest(new PathString("/api/plugins/bridge-plug/bridge"), "Bearer wrong"));
        Assert.True(svc.AuthorizeBridgeRequest(new PathString("/api/plugins/bridge-plug/bridge"), $"Bearer {token}"));

        var (bOk, bErr, _) = await svc.HandleBridgePostAsync("bridge-plug", JsonSerializer.SerializeToElement(new { title = "Standup", muted = false }), CancellationToken.None);
        Assert.True(bOk, bErr);

        using (var brSt = ToJson(await svc.GetWidgetStateAsync("bridge-plug", "p1", "b1", force: false, CancellationToken.None)))
            Assert.Equal("Standup", brSt.RootElement.GetProperty("data").GetProperty("title").GetString());

        var (brActCode, _) = await svc.ActAsync("bridge-plug", "p1", "b1", "mute", "1", CancellationToken.None);
        Assert.Equal(200, brActCode);

        using var q1 = ToJson(svc.DrainBridgeActions("bridge-plug"));
        Assert.Equal("mute", q1.RootElement.GetProperty("actions")[0].GetProperty("op").GetString());
        using var q2 = ToJson(svc.DrainBridgeActions("bridge-plug"));
        Assert.Equal(0, q2.RootElement.GetProperty("actions").GetArrayLength());
    }

    [Fact]
    public void LanAllowlistPermitsWidgetStateAndActButBlocksManagementAndBridge()
    {
        Assert.True(Lan.AllowedOverLan(new PathString("/api/plugin/gitea/state")));
        Assert.True(Lan.AllowedOverLan(new PathString("/api/plugin/gitea/act")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/plugins")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/plugins/install")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/plugins/gitea/settings")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/plugins/gitea/bridge")));
    }

    private static readonly JsonSerializerOptions WebJsonOpts = new(JsonSerializerDefaults.Web);

    private static JsonDocument ToJson(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj, WebJsonOpts));

    private static HttpResponseMessage JsonRes(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage BytesRes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
