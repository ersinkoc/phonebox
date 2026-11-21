using PhoneBox.Agent;
using Xunit;

public class PhoneCameraTests
{
    private static readonly byte[] Jpeg = [0xff, 0xd8, 1, 2, 0xff, 0xd9];
    private static string? RequestId(PhoneCamera camera, string phone = "phone1") =>
        System.Text.Json.JsonSerializer.SerializeToElement(camera.Poll(phone)).GetProperty("request").GetString();

    [Fact]
    public void FramesRequireFreshApprovalAndBothSessionAndTransportCredentials()
    {
        using var camera = new PhoneCamera(() => 0);
        Assert.True(camera.Request("phone1"));
        var id = RequestId(camera)!;
        Assert.Null(RequestId(camera, "phone2"));
        Assert.False(camera.Upload("phone1", id, "", "paired", Jpeg, 0));
        Assert.Null(camera.Approve("phone2", id, "paired"));
        var key = camera.Approve("phone1", id, "paired")!;
        Assert.NotEmpty(key);
        Assert.Null(camera.Approve("phone1", id, "paired"));
        Assert.False(camera.Upload("phone1", id, "wrong", "paired", Jpeg, 0));
        Assert.False(camera.Upload("phone1", id, key, "other-pc", Jpeg, 0));
        Assert.False(camera.Upload("phone2", id, key, "paired", Jpeg, 0));
        Assert.True(camera.Upload("phone1", id, key, "paired", Jpeg, 90));
        Assert.Equal(90, camera.Frame().Rotation);
        Assert.NotNull(camera.Frame().Jpeg);
        Assert.False(camera.End("phone1", id, "wrong", "paired"));
        Assert.True(camera.End("phone1", id, key, "paired"));
        Assert.Null(camera.Frame().Jpeg);
        Assert.False(camera.Upload("phone1", id, key, "paired", Jpeg, 0));
        Assert.True(camera.Request("phone1"));
        Assert.NotEqual(id, RequestId(camera));
        Assert.Null(camera.Approve("phone1", id, "paired"));
    }

    [Fact]
    public void PendingRequestsExpireAndCannotReplaceAnActiveSession()
    {
        long clock = 0;
        using var camera = new PhoneCamera(() => clock);
        Assert.False(camera.Request("../../bad"));
        Assert.True(camera.Request("phone1"));
        var id = RequestId(camera)!;
        Assert.False(camera.Request("phone2"));
        clock = 30_000;
        Assert.Null(camera.Approve("phone1", id, "paired"));
        Assert.True(camera.Request("phone2"));
    }

    [Fact]
    public void LostConnectionClearsFrameAndConsentEvenIfNoViewerIsOpen()
    {
        long clock = 0;
        using var camera = new PhoneCamera(() => clock);
        camera.Request("phone1");
        var id = RequestId(camera)!;
        var key = camera.Approve("phone1", id, "paired")!;
        Assert.True(camera.Upload("phone1", id, key, "paired", Jpeg, 0));
        clock = 2999;
        Assert.NotNull(camera.Frame().Jpeg);
        clock = 3000;
        Assert.Null(camera.Frame().Jpeg);
        Assert.False(camera.Upload("phone1", id, key, "paired", Jpeg, 0));
    }

    [Fact]
    public void RejectsInvalidAndOversizedFramesAndUnsupportedRotation()
    {
        using var camera = new PhoneCamera(() => 0);
        camera.Request("phone1");
        var id = RequestId(camera)!;
        var key = camera.Approve("phone1", id, "paired")!;
        Assert.False(camera.Upload("phone1", id, key, "paired", [1, 2, 3, 4], 0));
        Assert.False(camera.Upload("phone1", id, key, "paired", new byte[PhoneCamera.MaxFrameBytes + 1], 0));
        Assert.False(camera.Upload("phone1", id, key, "paired", Jpeg, 45));
        Assert.Null(camera.Frame().Jpeg);
        camera.Stop();
        Assert.False(camera.Upload("phone1", id, key, "paired", Jpeg, 0));
    }

    [Theory]
    [InlineData("/api/phone-camera/status")]
    [InlineData("/api/phone-camera/request")]
    [InlineData("/api/phone-camera/stop")]
    [InlineData("/api/phone-camera/frame")]
    [InlineData("/api/phone-camera/native")]
    [InlineData("/api/phone-camera/native/install")]
    [InlineData("/api/phone-camera/native/uninstall")]
    [InlineData("/api/phone-camera/native/enable")]
    [InlineData("/api/phone-camera/native/disable")]
    [InlineData("/phone-camera")]
    public void ReceiverAndControlsAreNeverExposedToPhones(string path)
    {
        Assert.False(Lan.AllowedOverLan(path));
        Assert.False(UsbLink.Allowed("GET", path));
    }

    [Fact]
    public void OnlyPairedPhoneCredentialCanReachUploadEndpoint()
    {
        var config = new LanConfig { Enabled = true, Token = "phone-key", WebhookToken = "webhook-key" };
        Assert.True(Lan.AllowedOverLan("/api/phone-camera/phone"));
        Assert.Equal(Lan.LanAccess.Phone, Lan.AuthorizeRequest(config, "POST", "/api/phone-camera/phone", "", "Bearer phone-key", null));
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(config, "POST", "/api/phone-camera/phone", "", "Bearer webhook-key", null));
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(config, "POST", "/api/phone-camera/phone", "https://evil.example", "Bearer phone-key", null));
    }
}
