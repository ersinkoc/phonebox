using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

public sealed class PairingTests
{
    private const long Now = 1_800_000_000_000;

    [Fact]
    public void ACodeWorksOnceAndCountsTheClaim()
    {
        var codes = new PairingCodes();
        var (code, expires, _) = codes.Start(Now);
        Assert.Equal(Now + PairingCodes.LifetimeMs, expires);
        Assert.Matches("^[A-Za-z0-9_-]{22}$", code); // 128 bits, base64url
        Assert.True(codes.Claim(code, Now + 1000));
        Assert.Equal(1, codes.Claims);
        Assert.False(codes.Claim(code, Now + 2000));
        Assert.Equal(1, codes.Claims);
    }

    [Fact]
    public void AnExpiredCancelledOrReplacedCodeFails()
    {
        var codes = new PairingCodes();
        var (expired, _, _) = codes.Start(Now);
        Assert.False(codes.Claim(expired, Now + PairingCodes.LifetimeMs + 1));

        var (cancelled, _, attempt) = codes.Start(Now);
        codes.Cancel(attempt);
        Assert.False(codes.Claim(cancelled, Now));

        var (old, _, _) = codes.Start(Now);
        var (current, _, _) = codes.Start(Now);
        Assert.False(codes.Claim(old, Now));
        Assert.True(codes.Claim(current, Now));
    }

    [Fact]
    public void ALateCancelFromAnOlderDialogLeavesTheNewPairing()
    {
        var codes = new PairingCodes();
        var (_, _, first) = codes.Start(Now);
        var (second, _, _) = codes.Start(Now);
        codes.Cancel(first);
        Assert.True(codes.Claim(second, Now));
    }

    [Fact]
    public void TooManyWrongCodesDropTheCurrentOne()
    {
        var codes = new PairingCodes();
        var (code, _, _) = codes.Start(Now);
        Assert.False(codes.Claim(null, Now));
        Assert.False(codes.Claim("", Now));
        for (var i = 0; i < PairingCodes.MaxFailures; i++) Assert.False(codes.Claim("wrong" + i, Now));
        Assert.False(codes.Claim(code, Now));
        Assert.Equal(0, codes.Claims);
    }

    [Fact]
    public void TheLinkKeepsEverythingInTheFragment()
    {
        var link = PairingCodes.Link("Desk PC & co", "ab12", 8766, ["192.168.1.10", "100.64.0.7"], "CODE_x-1");
        var uri = new Uri(link);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("ersinkoc.github.io", uri.Host);
        Assert.Equal("/phonebox/pair/", uri.AbsolutePath);
        Assert.Equal("", uri.Query); // nothing a web server would receive
        Assert.Equal("#v=1&pc=Desk%20PC%20%26%20co&fp=ab12&port=8766&h=192.168.1.10,100.64.0.7&c=CODE_x-1", uri.Fragment);
    }

    [Fact]
    public void TheCheckCodeIsTheStartOfTheFingerprint() =>
        Assert.Equal("AB12 CD34", PairingCodes.Check("ab12cd34" + new string('0', 56)));

    [Fact]
    public void TheQrCodeIsAnSvg()
    {
        var svg = PairingCodes.Svg(PairingCodes.Link("PC", new string('a', 64), 8766, ["192.168.1.10"], new string('A', 22)));
        Assert.StartsWith("<svg", svg.TrimStart());
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<string?> ClaimBody(string? contentType, string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        if (contentType is not null) ctx.Request.ContentType = contentType;
        ctx.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        return PairingCodes.ReadClaimCodeAsync(ctx.Request);
    }

    // Anyone on the Wi-Fi can send a claim without the key: a body that is not a JSON code used to throw out of the
    // endpoint (HTTP 500 plus an error line in agent.log per request) instead of being refused.
    [Theory]
    [InlineData("text/plain", """{"code":"abc"}""")]
    [InlineData(null, """{"code":"abc"}""")]
    [InlineData("application/json; charset=nope", """{"code":"abc"}""")]
    [InlineData("application/json", "{ not json")]
    [InlineData("application/json", """{"code":123}""")]
    [InlineData("application/json", """{"code":""}""")]
    [InlineData("application/json", """{"code":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    public async Task AClaimBodyThatIsNotAJsonCodeIsRefusedWithoutThrowing(string? contentType, string body) =>
        Assert.Null(await ClaimBody(contentType, body));

    [Fact]
    public async Task AJsonClaimBodyGivesItsCode() =>
        Assert.Equal("CODE_x-1", await ClaimBody("application/json; charset=utf-8", """{"code":"CODE_x-1"}"""));

    [Theory]
    [InlineData("POST", "/api/pair/claim", true)]
    [InlineData("GET", "/api/pair/claim", false)]
    [InlineData("POST", "/api/pair/start", false)]
    [InlineData("POST", "/api/pair", false)]
    [InlineData("POST", "/api/state", false)]
    public void OnlyTheClaimNeedsNoKeyOverWifi(string method, string path, bool open) =>
        Assert.Equal(open, Lan.IsPairingClaim(method, new PathString(path)));

    [Fact]
    public void DisabledLanRejectsAllTiersAndWebhookTokenStaysSeparateFromPhoneToken()
    {
        var enabled = new LanConfig { Enabled = true, Port = 8766, Token = "phone-tok", WebhookToken = "hook-tok" };
        Assert.Equal(Lan.LanAccess.Phone, Lan.AuthorizeRequest(enabled, "GET", "/api/state", "", "Bearer phone-tok", null));
        Assert.Equal(Lan.LanAccess.Webhook, Lan.AuthorizeRequest(enabled, "POST", "/api/push/ci", "", null, "hook-tok"));
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(enabled, "GET", "/api/state", "", "Bearer hook-tok", null));
        Assert.Equal(Lan.LanAccess.PairingClaim, Lan.AuthorizeRequest(enabled, "POST", "/api/pair/claim", "", null, null));
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(enabled, "GET", "/api/state", "https://evil.example", "Bearer phone-tok", null));

        var disabled = new LanConfig { Enabled = false, Port = 8766, Token = "phone-tok", WebhookToken = "hook-tok" };
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(disabled, "GET", "/api/state", "", "Bearer phone-tok", null));
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(disabled, "POST", "/api/push/ci", "", null, "hook-tok"));
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(disabled, "POST", "/api/pair/claim", "", null, null));
    }

    // Both keys with one value (a hand-edited config): every webhook sender would hold the phone key too.
    [Fact]
    public void ASharedPhoneAndWebhookKeyIsNotAPhoneKeyAndCannotBeSaved()
    {
        var shared = new LanConfig { Enabled = true, Port = 8766, Token = "same-tok", WebhookToken = "same-tok" };
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(shared, "GET", "/api/state", "", "Bearer same-tok", null));
        Assert.Equal(Lan.LanAccess.Webhook, Lan.AuthorizeRequest(shared, "POST", "/api/push/ci", "", "Bearer same-tok", null));

        // the settings page's save refuses it (other writes, like a Note from the phone, still go through)
        Assert.True(Program.LanKeysShared("""{"lan":{"enabled":true,"token":"same-tok","webhookToken":"same-tok"}}"""));
        Assert.True(Program.LanKeysShared("""{"lan":{"token":"a","webhookToken":"b"},"LAN":{"token":"c","webhookToken":"c"}}"""));
        Assert.False(Program.LanKeysShared("""{"lan":{"enabled":true,"token":"phone-tok","webhookToken":"hook-tok"}}"""));
        Assert.False(Program.LanKeysShared("""{"lan":{"token":"","webhookToken":""}}""")); // both get new, different keys
        Assert.False(Program.LanKeysShared("""{"lan":{"token":"c","webhookToken":"c"},"lan":{"token":"a","webhookToken":"b"}}"""));

        var dir = Directory.CreateTempSubdirectory("phonebox-shared-key-").FullName;
        try { Assert.Null(new ConfigStore(Path.Combine(dir, "config.json")).Save("""{"lan":{"token":"same-tok","webhookToken":"same-tok"}}""")); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // A claim waiting for its body when Wi-Fi is turned off must be refused, and must not use up the code.
    [Fact]
    public async Task AClaimThatOutlivesWifiBeingTurnedOffIsRefusedAndKeepsTheCode()
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-claim-off-").FullName;
        try
        {
            var cfg = new ConfigStore(Path.Combine(dir, "config.json"));
            Assert.Null(cfg.Save("""{"lan":{"enabled":true}}"""));
            using var cert = Lan.Certificate(dir);
            var codes = new PairingCodes();
            var (code, _, _) = codes.Start(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "POST";
            ctx.Request.ContentType = "application/json";
            ctx.Request.Body = new GatedMemoryStream(System.Text.Encoding.UTF8.GetBytes($$"""{"code":"{{code}}"}"""), entered, release.Task);

            var claim = Program.ClaimPairingAsync(ctx.Request, cfg, codes, cert, 8766, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Null(cfg.Save("""{"lan":{"enabled":false}}"""));
            }
            finally { release.TrySetResult(); }

            var result = await claim.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            Assert.True(codes.Claim(code, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ConfigStore.Save keeps the last of repeated members; the port check must look at that one.
    [Theory]
    [InlineData("""{"lan":{"enabled":false},"lan":{"enabled":true,"port":8765}}""", true)]
    [InlineData("""{"lan":{"enabled":true,"port":8765},"LAN":{"enabled":false}}""", false)]
    [InlineData("""{"lan":{"enabled":true,"port":8765},"lan":null}""", false)]
    public void TheLastOfRepeatedLanMembersIsTheOneChecked(string json, bool collides)
    {
        Assert.Equal(collides, Program.LanPortCollidesWithLocal(json, 8765));
        var dir = Directory.CreateTempSubdirectory("phonebox-lan-repeat-").FullName;
        try
        {
            var cfg = new ConfigStore(Path.Combine(dir, "config.json"));
            Assert.Null(cfg.Save(json));
            Assert.Equal(collides, cfg.Get().Lan.Enabled && cfg.Get().Lan.Port == 8765);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LanCertificateIsolatesToConfigDirectoryAndCollidingLanPortIsDetected()
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-lan-cert-").FullName;
        try
        {
            var certPath = Path.Combine(dir, "lan-cert.pfx");
            File.WriteAllText(certPath, "corrupt");
            using var first = Lan.Certificate(dir);
            Assert.True(File.Exists(certPath));
            var fp = Lan.Fingerprint(first);
            using var second = Lan.Certificate(dir);
            Assert.Equal(fp, Lan.Fingerprint(second));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(certPath));

            Assert.True(Program.LanPortCollidesWithLocal("""{"lan":{"enabled":true,"port":8765}}""", 8765));
            Assert.True(Program.LanPortCollidesWithLocal("""{"lan":{"enabled":true}}""", 8766));
            Assert.False(Program.LanPortCollidesWithLocal("""{"lan":{"enabled":false,"port":8765}}""", 8765));
            Assert.False(Program.LanPortCollidesWithLocal("""{"lan":{"enabled":true,"port":8766}}""", 8765));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ShortcutIconUploadRejectsConcurrentSettingsSaveAcrossBodyReadWithoutOrphans()
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-icon-race-").FullName;
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            var iconsDir = Path.Combine(dir, "icons");
            var cfg = new ConfigStore(cfgPath);
            Assert.Null(cfg.Save("""{"shortcuts":[{"id":"keep-me","label":"Keep","cmd":"builtin:mute"},{"id":"temp-sc","label":"Temp","cmd":"builtin:mute"}]}"""));
            var icons = new ShortcutIconUploads(iconsDir);
            var png = Convert.FromHexString("89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000a49444154789c63000100000500010d0a2db40000000049454e44ae426082");

            var v1 = cfg.VersionText;
            var enteredRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var gated = new GatedMemoryStream(png, enteredRead, releaseRead.Task);

            var uploadTask = Program.UploadShortcutIconAsync("temp-sc", v1, gated, "image/png", cfg, icons);
            long savedV2;
            try
            {
                // an upload that returns before reading the body fails here instead of hanging the test
                var first = await Task.WhenAny(enteredRead.Task, uploadTask).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(first == enteredRead.Task, "the upload finished before it read the body");

                // Advance config timestamp deterministically and delete temp-sc while body read is suspended
                File.SetLastWriteTimeUtc(cfgPath, new DateTime(cfg.Version, DateTimeKind.Utc).AddSeconds(-2));
                _ = cfg.Get();
                Assert.Null(cfg.Save("""{"shortcuts":[{"id":"keep-me","label":"Keep","cmd":"builtin:mute"}]}""", cfg.Version, out savedV2));
                icons.Cleanup(() => cfg.Get().Shortcuts.Select(s => s.Id));
            }
            finally
            {
                // released whatever happened above, and the upload observed before the directory goes
                releaseRead.TrySetResult();
                try { await uploadTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            }
            var result = await uploadTask;
            var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;
            Assert.Equal(StatusCodes.Status409Conflict, status);
            Assert.False(icons.Exists("temp-sc"));
            Assert.Equal(savedV2, cfg.Version);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private sealed class GatedMemoryStream(byte[] data, TaskCompletionSource entered, Task release) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos == 0)
            {
                entered.TrySetResult();
                await release.WaitAsync(cancellationToken);
            }
            var n = Math.Min(buffer.Length, data.Length - _pos);
            data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return n;
        }
    }
}
