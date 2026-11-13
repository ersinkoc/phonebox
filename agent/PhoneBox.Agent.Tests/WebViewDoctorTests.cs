using PhoneBox.Agent;
using Xunit;

public class WebViewDoctorTests
{
    private const long Now = 1_000_000_000;

    [Fact]
    public void DiagnosesGoogleWebViewBelow105()
    {
        var msg = WebViewDoctor.Diagnose("com.google.android.webview/83.0.4103.106", "Pixel 3a");
        Assert.NotNull(msg);
        Assert.Contains("Pixel 3a", msg);
        Assert.Contains("outdated WebView (83", msg);
        Assert.Contains("Chromium 105+", msg);
        Assert.Contains("Update Android System WebView from Google Play", msg);
    }

    [Theory]
    [InlineData("com.android.chrome/74.0.3729.185")]
    [InlineData("com.chrome.beta/90.0.4430.82")]
    public void DiagnosesChromeProvidedWebViewOnOlderAndroid(string webView)
    {
        var msg = WebViewDoctor.Diagnose(webView, "SM-G960F");
        Assert.NotNull(msg);
        Assert.Contains("Update Google Chrome from Google Play (it provides WebView on this device)", msg);
    }

    [Fact]
    public void DiagnosesAospOrVendorWebView()
    {
        var msg = WebViewDoctor.Diagnose("com.android.webview/91.0.4472.114", "Generic Tablet");
        Assert.NotNull(msg);
        Assert.Contains("Developer options › WebView implementation", msg);
    }

    [Theory]
    [InlineData("com.google.android.webview/105.0.5195.136")]
    [InlineData("com.google.android.webview/134.0.6998.135-arm64")]
    [InlineData(null)]
    [InlineData("")]
    public void StaysQuietOnModernOrMissingWebView(string? webView) =>
        Assert.Null(WebViewDoctor.Diagnose(webView, "Pixel 8"));

    [Theory]
    [InlineData("<script>alert(1)</script>/80.0")]
    [InlineData("com.google.android.webview/80.0\r\nInjected: 1")]
    [InlineData("noslash")]
    [InlineData("bad..pkg/abc")]
    [InlineData("com.android.webview/0.1")]
    [InlineData("com.android.webview/1000.0")]
    [InlineData("com.android.webview/80.0/extra")]
    [InlineData("nopkg/80.0")]
    public void RejectsMalformedOrMaliciousHeaders(string raw)
    {
        Assert.Null(WebViewDoctor.Normalize(raw));
        Assert.Null(WebViewDoctor.Diagnose(raw, "Pixel 8"));
    }

    [Fact]
    public void RejectsOverlongPackageHeader()
    {
        var longHeader = new string('a', 90) + "/80.0";
        Assert.Null(WebViewDoctor.Normalize(longHeader));
        Assert.Null(WebViewDoctor.Diagnose(longHeader, "Pixel 8"));
    }

    [Fact]
    public void DiagnoseRecentIgnoresStalePhonesAndFindsActiveOutdatedOne()
    {
        var phones = new[]
        {
            new PhoneSeen("stale1", "Old Tablet", Now - WebViewDoctor.FreshMs - 10_000, "Wi-Fi", "com.android.webview/74.0.0.0"),
            new PhoneSeen("new1", "Pixel 8", Now - 1000, "USB", "com.google.android.webview/134.0.0.0"),
        };
        Assert.Null(WebViewDoctor.DiagnoseRecent(phones, Now));

        var withActiveOld = phones.Concat([new PhoneSeen("old2", "Pixel 3a", Now - 5000, "USB", "com.google.android.webview/83.0.4103.106")]);
        var msg = WebViewDoctor.DiagnoseRecent(withActiveOld, Now);
        Assert.NotNull(msg);
        Assert.Contains("Pixel 3a", msg);
    }

    [Fact]
    public void RegistryStoresNormalizedWebViewAndDeduplicatesWarning()
    {
        const string id = "wvtest01";
        var first = PhoneRegistry.Touch(id, "SM-G960F", "Wi-Fi", "com.android.chrome/74.0.3729.185");
        Assert.NotNull(first);
        Assert.Contains("Update Google Chrome", first);

        // Repeated requests for the same WebView do not repeat the log warning.
        var second = PhoneRegistry.Touch(id, "SM-G960F", "Wi-Fi", "com.android.chrome/74.0.3729.185");
        Assert.Null(second);
        Assert.Contains(PhoneRegistry.All(), p => p.Id == id && p.WebView == "com.android.chrome/74.0.3729.185");

        // Updating WebView clears the stored problem; malformed header is dropped.
        Assert.Null(PhoneRegistry.Touch(id, "SM-G960F", "USB", "com.google.android.webview/134.0.0.0"));
        Assert.Null(PhoneRegistry.Touch(id, "SM-G960F", "USB", "<script>/80.0"));
        Assert.Contains(PhoneRegistry.All(), p => p.Id == id && p.WebView is null);
    }
}
