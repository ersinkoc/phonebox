using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// Updater: picking the assets of a release and checking downloads against its SHA256SUMS. The fixture is the real
/// v0.9.9 release (metadata from the GitHub API and its SHA256SUMS-0.9.9.txt), so a change to how release.yml names
/// the assets shows up here before it can stop the updates of installed agents.
/// </summary>
[Collection("Approval request expiry")] // the iOS middleware cases write the shared PhoneLastSeen timestamp
public class UpdateChecksumTests
{
    private const string Release099 = """
{
  "assets": [
    {
      "browser_download_url": "https://github.com/ersinkoc/phonebox/releases/download/v0.9.9/PhoneBox-0.9.9.apk",
      "name": "PhoneBox-0.9.9.apk",
      "url": "https://api.github.com/repos/ersinkoc/phonebox/releases/assets/752008188"
    },
    {
      "browser_download_url": "https://github.com/ersinkoc/phonebox/releases/download/v0.9.9/PhoneBox-0.9.9.intoto.jsonl",
      "name": "PhoneBox-0.9.9.intoto.jsonl",
      "url": "https://api.github.com/repos/ersinkoc/phonebox/releases/assets/752008185"
    },
    {
      "browser_download_url": "https://github.com/ersinkoc/phonebox/releases/download/v0.9.9/PhoneBoxAgent-0.9.9-linux-arm64.zip",
      "name": "PhoneBoxAgent-0.9.9-linux-arm64.zip",
      "url": "https://api.github.com/repos/ersinkoc/phonebox/releases/assets/752008190"
    },
    {
      "browser_download_url": "https://github.com/ersinkoc/phonebox/releases/download/v0.9.9/PhoneBoxAgent-0.9.9-linux-x64.zip",
      "name": "PhoneBoxAgent-0.9.9-linux-x64.zip",
      "url": "https://api.github.com/repos/ersinkoc/phonebox/releases/assets/752008184"
    },
    {
      "browser_download_url": "https://github.com/ersinkoc/phonebox/releases/download/v0.9.9/PhoneBoxAgent-0.9.9-osx-arm64.zip",
      "name": "PhoneBoxAgent-0.9.9-osx-arm64.zip",
      "url": "https://api.github.com/repos/ersinkoc/phonebox/releases/assets/752008193"
    },
    {
      "browser_download_url": "https://github.com/ersinkoc/phonebox/releases/download/v0.9.9/PhoneBoxAgent-0.9.9-osx-x64.zip",
      "name": "PhoneBoxAgent-0.9.9-osx-x64.zip",
      "url": "https://api.github.com/repos/ersinkoc/phonebox/releases/assets/752008208"
    },
    {
      "browser_download_url": "https://github.com/ersinkoc/phonebox/releases/download/v0.9.9/PhoneBoxAgent-0.9.9-win-x64.zip",
      "name": "PhoneBoxAgent-0.9.9-win-x64.zip",
      "url": "https://api.github.com/repos/ersinkoc/phonebox/releases/assets/752008218"
    },
    {
      "browser_download_url": "https://github.com/ersinkoc/phonebox/releases/download/v0.9.9/SHA256SUMS-0.9.9.txt",
      "name": "SHA256SUMS-0.9.9.txt",
      "url": "https://api.github.com/repos/ersinkoc/phonebox/releases/assets/752008305"
    }
  ],
  "html_url": "https://github.com/ersinkoc/phonebox/releases/tag/v0.9.9",
  "tag_name": "v0.9.9"
}
""";

    private const string Sums099 = """
da72c2917e3575e171c5419ac20f5accf689972f2694b1a569e6deb04994bf08  PhoneBox-0.9.9.apk
07d2d81e217d7067807673ea63924a9ab373a6142f82a84829f5d2c40cf5263b  PhoneBox-0.9.9.intoto.jsonl
a37fdd69197d0b7635f91dca4e0c4358c76a4aacf5fc3a3afbfe977ee3dff839  PhoneBoxAgent-0.9.9-linux-arm64.zip
b47da3d09583268d7bacef2d4375555313bf455af8a3ab16fd4d7ee95082826e  PhoneBoxAgent-0.9.9-linux-x64.zip
39114b5f1db06d66df3aa6c123a5eff7b66b6fcae5315a6af21bb3414745e58c  PhoneBoxAgent-0.9.9-osx-arm64.zip
18f620183523c014668475deddc737f313912239d767bb8dcd6876d12f8f4818  PhoneBoxAgent-0.9.9-osx-x64.zip
17e70d3df7db81eb767ee3328bdbdfc9f6492f69ebc63fe814afc96cf0990fd0  PhoneBoxAgent-0.9.9-win-x64.zip
""";

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-arm64")]
    [InlineData("osx-x64")]
    public void EveryRidFindsItsZipTheAppAndTheChecksumsAndEachIsListed(string rid)
    {
        using var doc = JsonDocument.Parse(Release099);
        var r = UpdateService.SelectAssets(doc.RootElement, rid);
        Assert.Equal("0.9.9", r.Version);
        Assert.Equal($"PhoneBoxAgent-0.9.9-{rid}.zip", UpdateService.AssetName(r.AgentZip!));
        Assert.Equal("PhoneBox-0.9.9.apk", UpdateService.AssetName(r.Apk!));
        Assert.Equal("SHA256SUMS-0.9.9.txt", UpdateService.AssetName(r.Sums!));
        Assert.NotNull(UpdateService.ExpectedSha256(Sums099, UpdateService.AssetName(r.AgentZip!)));
        Assert.NotNull(UpdateService.ExpectedSha256(Sums099, UpdateService.AssetName(r.Apk!)));
    }

    [Fact]
    public void TheLinuxX64ZipIsNotTakenForTheArm64One()
    {
        var x64 = UpdateService.ExpectedSha256(Sums099, "PhoneBoxAgent-0.9.9-linux-x64.zip");
        var arm = UpdateService.ExpectedSha256(Sums099, "PhoneBoxAgent-0.9.9-linux-arm64.zip");
        Assert.NotNull(x64);
        Assert.NotNull(arm);
        Assert.NotEqual(x64, arm);
    }

    [Fact]
    public void ListingFormatsAreRead()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, UpdateService.ExpectedSha256($"{hash}  PhoneBox-1.0.0.apk\n", "PhoneBox-1.0.0.apk"));     // text mode
        Assert.Equal(hash, UpdateService.ExpectedSha256($"{hash} *PhoneBox-1.0.0.apk\r\n", "PhoneBox-1.0.0.apk")); // binary mode, CRLF
        Assert.Equal(hash, UpdateService.ExpectedSha256($"{hash.ToUpperInvariant()}  PhoneBox-1.0.0.apk", "PhoneBox-1.0.0.apk"));
    }

    [Theory]
    [InlineData("PhoneBox-0.9.9.apk.idsig")]  // only a suffix more
    [InlineData("PhoneBox-0.9.9")]            // only a prefix
    [InlineData("phonebox-0.9.9.apk")]        // file names are compared exactly
    [InlineData("SHA256SUMS-0.9.9.txt")]      // the listing does not list itself
    public void NamesThatAreNotListedGiveNothing(string name) => Assert.Null(UpdateService.ExpectedSha256(Sums099, name));

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>Rate limit</body></html>")] // an error page saved instead of the listing
    [InlineData("")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz  PhoneBox-0.9.9.apk")] // not hex
    public void TextThatIsNotAListingGivesNothing(string text) => Assert.Null(UpdateService.ExpectedSha256(text, "PhoneBox-0.9.9.apk"));

    [Fact]
    public void TheAssetNameComesFromTheLinkNotTheCachedName()
    {
        Assert.Equal("PhoneBoxAgent-0.9.9-win-x64.zip",
            UpdateService.AssetName("https://github.com/ersinkoc/phonebox/releases/download/v0.9.9/PhoneBoxAgent-0.9.9-win-x64.zip"));
    }

    [Fact]
    public void AReleaseWithoutAChecksumFileHasNoSums()
    {
        using var doc = JsonDocument.Parse("""
            { "tag_name": "v1.0.0", "html_url": "https://example.invalid", "assets": [
              { "name": "PhoneBox-1.0.0.apk", "browser_download_url": "https://example.invalid/PhoneBox-1.0.0.apk" } ] }
            """);
        var r = UpdateService.SelectAssets(doc.RootElement, "win-x64");
        Assert.Null(r.Sums);
        Assert.Null(r.AgentZip);
        Assert.NotNull(r.Apk);
    }

    [Fact]
    public void AFileMatchesOnlyItsOwnHash()
    {
        var file = Path.Combine(Path.GetTempPath(), "phonebox-hash-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "PhoneBox");
        try
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("PhoneBox"u8.ToArray()));
            Assert.True(UpdateService.HashMatches(file, hash));                     // upper case from the listing works too
            Assert.False(UpdateService.HashMatches(file, new string('0', 64)));
            File.AppendAllText(file, "!");                                         // a damaged or partial copy
            Assert.False(UpdateService.HashMatches(file, hash));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void WhenThePhonesReportTheCurrentAppOnlyTheOnesThatFailedAreTriedAgain()
    {
        // AppVersion comes from whichever phone spoke last: B updated and says "current", A refused before
        Assert.Equal(["A"], UpdateService.PhoneTargets(["A", "B"], appCurrent: true, retry: ["A"]));
        Assert.Empty(UpdateService.PhoneTargets(["B"], appCurrent: true, retry: ["A"]));          // A is not plugged in
        Assert.Equal(["A", "B"], UpdateService.PhoneTargets(["A", "B"], appCurrent: false, retry: [])); // a new release: all
    }

    [Fact]
    public void AStreamedInstallThatFailsInTransferIsTriedAgainWithoutStreaming()
    {
        // a new Android on an old kernel cannot splice() the streamed APK; a pushed one installs (#353)
        Assert.True(UpdateService.RetryWithoutStreaming(
            "Performing Streamed Install\nadb.exe: failed to install PhoneBox.apk: Error: failed to write; splice failed: EINVAL (Invalid argument)\n"));
        Assert.False(UpdateService.RetryWithoutStreaming("Performing Streamed Install\nSuccess\n"));
        // refusals by the package manager are not a transfer problem
        Assert.False(UpdateService.RetryWithoutStreaming("adb: failed to install x.apk: Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: signatures do not match]"));
        Assert.False(UpdateService.RetryWithoutStreaming("Failure [INSTALL_PARSE_FAILED_NOT_APK]"));
    }

    [Fact]
    public void APhoneUpdatedAnotherWayIsNotInstalledAgain()
    {
        // installed by hand after a failed update: the retry ends without installing, so "update available" clears (#353)
        const string dump = "Packages:\n  Package [net.oxog.phonebox.deskpanel] (3ec2f24):\n    versionCode=47 minSdk=21 targetSdk=35\n    versionName=0.9.26\n";
        Assert.True(UpdateService.AppCurrentOn(dump, "0.9.26"));
        Assert.True(UpdateService.AppCurrentOn(dump.Replace("0.9.26", "0.9.27"), "0.9.26"));
        Assert.False(UpdateService.AppCurrentOn(dump.Replace("0.9.26", "0.9.25"), "0.9.26"));
        Assert.False(UpdateService.AppCurrentOn("", "0.9.26"));                   // no answer: install as before
        Assert.False(UpdateService.AppCurrentOn("Unable to find package", "0.9.26"));
        Assert.False(UpdateService.AppCurrentOn(dump.Replace("0.9.26", "0.9.25+local"), "0.9.26")); // can't compare: install
        Assert.False(UpdateService.AppCurrentOn(dump.Replace("0.9.26", "0.9.26_local"), "0.9.26"));
    }

    [Fact]
    public void APhoneStaysPendingUntilItsOwnUpdateWorks()
    {
        Assert.Equal(["A"], UpdateService.NextPhoneRetry([], attempted: ["A", "B"], failed: ["A"]));
        Assert.Equal(["A"], UpdateService.NextPhoneRetry(["A"], attempted: [], failed: []));          // A unplugged: kept
        Assert.Empty(UpdateService.NextPhoneRetry(["A"], attempted: ["A"], failed: []));               // A worked
        Assert.Equal(["C", "B"], UpdateService.NextPhoneRetry(["A", "C"], attempted: ["A", "B"], failed: ["B"]));
    }

    [Fact]
    public async Task ApplyClearsErrorAfterRetrySuccessAndZeroTargetsPreserveAppVersion()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pb-upd-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var prevAppVer = UpdateService.AppVersion;
        var prevLastSeen = Interlocked.Read(ref ApprovalStore.PhoneLastSeen);
        try
        {
            string fakeAdb;
            var modePath = Path.Combine(dir, "mode.txt");
            File.WriteAllText(modePath, "fail");
            if (OperatingSystem.IsWindows())
            {
                // mode.txt is found next to the script, not through a temp path written into it (TMPDIR may hold
                // characters the shell would expand). "fail" fails every phone; a serial fails only that phone.
                fakeAdb = Path.Combine(dir, "adb.cmd");
                File.WriteAllText(fakeAdb, """
                    @echo off
                    set /p MODE=<"%~dp0mode.txt"
                    if "%3"=="install" (
                      if "%MODE%"=="fail" ( echo Failure [LOCKED] & exit /b 1 )
                      if "%MODE%"=="%2" ( echo Failure [LOCKED] & exit /b 1 )
                      echo Success
                      exit /b 0
                    )
                    echo ok
                    """);
            }
            else
            {
                fakeAdb = Path.Combine(dir, "adb");
                File.WriteAllText(fakeAdb, "#!/bin/sh\nMODE=$(cat \"$(dirname \"$0\")/mode.txt\")\nif [ \"$3\" = \"install\" ]; then\n  if [ \"$MODE\" = \"fail\" ] || [ \"$MODE\" = \"$2\" ]; then echo 'Failure [LOCKED]'; exit 1; fi\n  echo 'Success'; exit 0\nfi\necho ok\n");
                File.SetUnixFileMode(fakeAdb, (UnixFileMode)0b111_101_101);
            }

            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, JsonSerializer.Serialize(new { adbPath = fakeAdb, updates = new { auto = false } }));
            var cfg = new ConfigStore(cfgPath);
            var adb = new AdbReverse(cfg, NullLogger<AdbReverse>.Instance);
            var devicesField = typeof(AdbReverse).GetField("_devices", BindingFlags.Instance | BindingFlags.NonPublic)!;

            var ver = AppPaths.Version;
            var apkBytes = "apk-v-test"u8.ToArray();
            var sums = $"{Convert.ToHexStringLower(SHA256.HashData(apkBytes))}  PhoneBox-{ver}.apk\n";
            var relJson = $$"""
                {
                  "tag_name": "v{{ver}}",
                  "html_url": "https://example.invalid/release",
                  "assets": [
                    { "name": "PhoneBox-{{ver}}.apk", "browser_download_url": "https://example.invalid/PhoneBox-{{ver}}.apk" },
                    { "name": "SHA256SUMS-{{ver}}.txt", "browser_download_url": "https://example.invalid/SHA256SUMS-{{ver}}.txt" }
                  ]
                }
                """;

            var httpField = typeof(UpdateService).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var svc = new UpdateService(cfg, adb, new NoopLifetime(), NullLogger<UpdateService>.Instance) { DownloadsDir = Path.Combine(dir, "dl1") };
            httpField.SetValue(svc, new HttpClient(new FakeReleaseHandler(relJson, apkBytes, sums)));

            UpdateService.AppVersion = "0.0.1";
            devicesField.SetValue(adb, new[] { "SER1" });
            await svc.CheckAsync();

            var gate = new RequestGate(8765, [], new UsbLink(8767, "unused"), null, cfg, _ => false, NullLogger.Instance);
            var ios = new DefaultHttpContext();
            ios.Request.Host = new HostString("localhost");
            ios.Request.Headers["X-PhoneBox-App"] = "0.1.0";
            ios.Request.Headers["X-PhoneBox-Model"] = "iOS 27.2 iPhone";
            await gate.InvokeAsync(ios, () => Task.CompletedTask);
            Assert.Equal("0.0.1", UpdateService.AppVersion);
            Assert.True(svc.Status().AppUpdate);
            var iosStatus = svc.Status(includeAndroid: !RequestGate.IsIosApp(ios.Request.Headers));
            Assert.False(iosStatus.AppUpdate);
            Assert.Null(iosStatus.AppVersion);

            ios.Request.Headers.Remove("X-PhoneBox-Model");
            ios.Request.Headers["X-PhoneBox-Platform"] = "ios";
            await gate.InvokeAsync(ios, () => Task.CompletedTask);
            Assert.Equal("0.0.1", UpdateService.AppVersion);

            await svc.ApplyAsync();
            Assert.NotNull(svc.Status().Error);
            Assert.True(svc.Status().AppUpdate);

            File.WriteAllText(modePath, "ok");
            await svc.ApplyAsync();
            Assert.Null(svc.Status().Error);
            Assert.False(svc.Status().AppUpdate);
            Assert.Equal(ver, UpdateService.AppVersion);

            var svc2 = new UpdateService(cfg, adb, new NoopLifetime(), NullLogger<UpdateService>.Instance) { DownloadsDir = Path.Combine(dir, "dl2") };
            httpField.SetValue(svc2, new HttpClient(new FakeReleaseHandler(relJson, apkBytes, sums, () => devicesField.SetValue(adb, Array.Empty<string>()))));
            UpdateService.AppVersion = "0.0.1";
            devicesField.SetValue(adb, new[] { "SER1" });
            await svc2.CheckAsync();
            await svc2.ApplyAsync();
            Assert.Equal("0.0.1", UpdateService.AppVersion);
            Assert.True(svc2.Status().AppUpdate);

            // A fails, B installs; B then reports the new app while A is unplugged. The next apply has nothing to
            // install, and A's update is still pending: the reason it failed must stay next to it.
            var svc3 = new UpdateService(cfg, adb, new NoopLifetime(), NullLogger<UpdateService>.Instance) { DownloadsDir = Path.Combine(dir, "dl3") };
            httpField.SetValue(svc3, new HttpClient(new FakeReleaseHandler(relJson, apkBytes, sums)));
            File.WriteAllText(modePath, "SERA");
            UpdateService.AppVersion = "0.0.1";
            devicesField.SetValue(adb, new[] { "SERA", "SERB" });
            await svc3.CheckAsync();
            await svc3.ApplyAsync();
            var reason = svc3.Status().Error;
            Assert.Contains("SERA", reason);
            UpdateService.AppVersion = ver;                 // SERB, updated, reports the new app
            devicesField.SetValue(adb, new[] { "SERB" });   // SERA unplugged
            await svc3.ApplyAsync();
            Assert.True(svc3.Status().AppUpdate);
            Assert.Equal(reason, svc3.Status().Error);
        }
        finally
        {
            UpdateService.AppVersion = prevAppVer;
            Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, prevLastSeen);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>While "adb install" runs, AdbReverse knows it, and doesn't restart an adb server that seems hung.</summary>
    [Fact]
    public async Task AnAppInstallIsMarkedAsRunningForTheAdbWatchdog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pb-upd-installing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var prevAppVer = UpdateService.AppVersion;
        try
        {
            // "install" leaves a mark and waits for the gate file; everything else answers at once
            string fakeAdb;
            if (OperatingSystem.IsWindows())
            {
                fakeAdb = Path.Combine(dir, "adb.cmd");
                File.WriteAllText(fakeAdb, """
                    @echo off
                    if not "%3"=="install" (echo ok& exit /b 0)
                    echo.>"%~dp0installing"
                    :hold
                    if not exist "%~dp0gate" (ping -n 2 127.0.0.1 >nul& goto hold)
                    echo Success
                    exit /b 0
                    """.ReplaceLineEndings("\r\n")); // goto finds its label reliably only in a CRLF batch file
            }
            else
            {
                fakeAdb = Path.Combine(dir, "adb");
                File.WriteAllText(fakeAdb, "#!/bin/sh\nD=$(dirname \"$0\")\nif [ \"$3\" != install ]; then echo ok; exit 0; fi\n"
                    + "touch \"$D/installing\"\nwhile [ ! -e \"$D/gate\" ]; do sleep 0.1; done\necho Success\n");
                File.SetUnixFileMode(fakeAdb, (UnixFileMode)0b111_101_101);
            }
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, JsonSerializer.Serialize(new { adbPath = fakeAdb, updates = new { auto = false } }));
            var cfg = new ConfigStore(cfgPath);
            var adb = new AdbReverse(cfg, NullLogger<AdbReverse>.Instance);
            typeof(AdbReverse).GetField("_devices", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(adb, new[] { "SER1" });

            var ver = AppPaths.Version;
            var apkBytes = "apk-v-test"u8.ToArray();
            var sums = $"{Convert.ToHexStringLower(SHA256.HashData(apkBytes))}  PhoneBox-{ver}.apk\n";
            var relJson = $$"""
                {
                  "tag_name": "v{{ver}}",
                  "html_url": "https://example.invalid/release",
                  "assets": [
                    { "name": "PhoneBox-{{ver}}.apk", "browser_download_url": "https://example.invalid/PhoneBox-{{ver}}.apk" },
                    { "name": "SHA256SUMS-{{ver}}.txt", "browser_download_url": "https://example.invalid/SHA256SUMS-{{ver}}.txt" }
                  ]
                }
                """;
            var svc = new UpdateService(cfg, adb, new NoopLifetime(), NullLogger<UpdateService>.Instance) { DownloadsDir = Path.Combine(dir, "dl") };
            typeof(UpdateService).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(svc, new HttpClient(new FakeReleaseHandler(relJson, apkBytes, sums)));

            UpdateService.AppVersion = "0.0.1";
            await svc.CheckAsync();
            Assert.False(adb.InstallRunning);
            using var cancel = new CancellationTokenSource();
            var apply = svc.ApplyAsync(cancel.Token);
            try
            {
                for (var i = 0; i < 600 && !File.Exists(Path.Combine(dir, "installing")); i++) await Task.Delay(50);
                Assert.True(File.Exists(Path.Combine(dir, "installing")));
                Assert.True(adb.InstallRunning);

                File.WriteAllText(Path.Combine(dir, "gate"), "");
                await apply.WaitAsync(TimeSpan.FromSeconds(60));
                Assert.Null(svc.Status().Error);
                Assert.False(adb.InstallRunning);
            }
            finally
            {
                // a failed assertion above: the fake install is ended and joined before the folder goes
                if (!apply.IsCompleted)
                {
                    await cancel.CancelAsync();
                    try { await apply.WaitAsync(TimeSpan.FromSeconds(30)); } catch (Exception) { }
                }
            }
        }
        finally
        {
            UpdateService.AppVersion = prevAppVer;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private sealed class FakeReleaseHandler(string relJson, byte[] apk, string sums, Action? onApk = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var u = request.RequestUri!.AbsoluteUri;
            if (u.EndsWith("/releases/latest"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(relJson, Encoding.UTF8, "application/json") });
            if (u.EndsWith(".txt"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sums, Encoding.UTF8, "text/plain") });
            onApk?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(apk) });
        }
    }

    private sealed class NoopLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
