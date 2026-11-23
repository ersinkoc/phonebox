using System.Text.Json;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// Unit tests for Antigravity endpoint discovery, quota parsing, and plan resolution.
/// </summary>
public sealed class AntigravityTests
{
    [Fact]
    public void The_CLI_is_found_where_the_current_installer_puts_it_when_its_folder_is_not_on_PATH()
    {
        Assert.Equal(
            [Path.Combine("C:/Users/u", ".gemini", "bin", "agy.exe"), Path.Combine("C:/Users/u/AppData/Local", "agy", "bin", "agy.exe")],
            QuotaMonitor.AntigravityCliCandidates(true, "C:/Users/u", "C:/Users/u/AppData/Local"));
        Assert.Equal([Path.Combine("/home/u", ".gemini", "bin", "agy")],
            QuotaMonitor.AntigravityCliCandidates(false, "/home/u", ""));
        Assert.Empty(QuotaMonitor.AntigravityCliCandidates(false, "", ""));
    }

    [Fact]
    public async Task CliRequiresASuccessfulUsageCommandEnvelope()
    {
        var provider = await QuotaMonitor.ReadAntigravityCliAsync("agy", CancellationToken.None, (start, _, _) =>
            Task.FromResult(start.ArgumentList[0] == "--version" ? new ToolProcess.Result(0, "1.5.3", "")
                : new ToolProcess.Result(0, """{"groups":[{"buckets":[{"remaining_fraction":0.5}]}]}""", "")));
        Assert.Null(provider);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("unknown")]
    public async Task UnsupportedCliVersionsNeverRunAPrintPrompt(string version)
    {
        var calls = 0;
        var provider = await QuotaMonitor.ReadAntigravityCliAsync("agy", CancellationToken.None, (start, timeout, ct) =>
        {
            calls++;
            Assert.Equal(new[] { "--version" }, start.ArgumentList);
            return Task.FromResult(new ToolProcess.Result(0, version, ""));
        });
        Assert.Null(provider);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SupportedCliReadsOnlyTheUsageCommandWithBoundedExecution()
    {
        var calls = 0;
        var provider = await QuotaMonitor.ReadAntigravityCliAsync("agy", CancellationToken.None, (start, timeout, ct) =>
        {
            calls++;
            Assert.Equal("true", start.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"]);
            Assert.Equal("false", start.Environment["BROWSER"]);
            Assert.InRange(timeout.TotalSeconds, 1, 20);
            if (calls == 1) return Task.FromResult(new ToolProcess.Result(0, "1.5.3\n", ""));
            Assert.Equal(new[] { "-p", "/usage", "--output-format", "json", "--print-timeout", "15s" }, start.ArgumentList);
            return Task.FromResult(new ToolProcess.Result(0, """
                {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[{"name":"Gemini Models",
                "buckets":[{"id":"gemini-weekly","window":"weekly","remaining_fraction":0.7354}]}]}}}
                """, ""));
        });
        Assert.NotNull(provider);
        Assert.Equal(2, calls);
        Assert.Equal(26.5, Assert.Single(provider.Limits).Percent);
        Assert.Equal("Antigravity", provider.Plan);
        Assert.Null(provider.Error);
    }

    [Theory]
    [InlineData("ERROR", "usage")]
    [InlineData("SUCCESS", "other")]
    public void FailedOrUnrelatedCliCommandsAreNotQuotaData(string status, string command)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { status, command = new { name = command,
            data = new { groups = new[] { new { buckets = new[] { new { remaining_fraction = 0.5 } } } } } } }));
        Assert.Empty(QuotaMonitor.ParseAntigravityLimits(doc.RootElement));
    }

    [Fact]
    public async Task FailedCliExitDoesNotPublishEvenAnApparentlyValidPayload()
    {
        var provider = await QuotaMonitor.ReadAntigravityCliAsync("agy", CancellationToken.None, (start, _, _) =>
            Task.FromResult(start.ArgumentList[0] == "--version" ? new ToolProcess.Result(0, "1.5.3", "")
                : new ToolProcess.Result(1, """{"groups":[{"buckets":[{"remaining_fraction":0.5}]}]}""", "private auth error")));
        Assert.Null(provider);
    }

    [Fact]
    public void CliUsageEnvelopeMapsMeasuredGroupsWithoutExposingConversationMetadata()
    {
        using var doc = JsonDocument.Parse("""
            {"status":"SUCCESS","conversation_id":"private-session","num_turns":0,"command":{"name":"usage","data":{"groups":[
            {"name":"Gemini Models","buckets":[{"id":"gemini-weekly","window":"weekly","remaining_fraction":0.7354,"reset_time":"2026-11-13T23:43:55Z"},
            {"id":"gemini-5h","window":"5h","remaining_fraction":1,"reset_time":"2026-11-10T04:17:04Z"}]},
            {"name":"Claude and GPT models","buckets":[{"id":"3p-weekly","window":"weekly","remaining_fraction":1},
            {"id":"3p-5h","window":"5h","remaining_fraction":1}]}]}}}
            """);
        var limits = QuotaMonitor.ParseAntigravityLimits(doc.RootElement);
        Assert.Equal(4, limits.Count);
        Assert.Equal("5-hour (Gemini)", limits[0].Label);
        Assert.Equal(0, limits[0].Percent);
        Assert.Equal("Weekly (Gemini)", limits[1].Label);
        Assert.Equal(26.5, limits[1].Percent);
        Assert.NotNull(limits[1].ResetsAt);
        Assert.Equal("5-hour (Claude/GPT)", limits[2].Label);
        Assert.Equal("Weekly (Claude/GPT)", limits[3].Label);
        Assert.DoesNotContain("private-session", JsonSerializer.Serialize(limits));
    }

    [Fact]
    public void DiscoveryIncludesLegacyNamesAndRejectsOtherEditors()
    {
        var commands = new Dictionary<string, string[]>
        {
            ["language_server_windows_x64"] = ["language_server_windows_x64 --app_data_dir=antigravity --extension_server_port=18765 --csrf_token=windows"],
            ["language_server_linux_x64"] = ["language_server_linux_x64 --app_data_dir=antigravity --extension_server_port=18766 --csrf_token=linux"],
            ["language_server_linux_arm"] = ["language_server_linux_arm --app_data_dir=antigravity --extension_server_port=18767 --csrf_token=arm"],
            ["language_server"] = ["language_server --app_data_dir=windsurf --port=18768 --csrf_token=other-editor"],
            ["agy"] = ["agy --hub --hub-port=18769 --csrf_token=hub"]
        };
        var endpoints = QuotaMonitor.FindAntigravityEndpoints(
            name => (commands.GetValueOrDefault(name) ?? []).Select(cmd => (1000, cmd)),
            _ => throw new InvalidOperationException("No command line asked for a run-time port")).ToArray();
        Assert.Equal(new[] { 18765, 18766, 18767, 18769 }, endpoints.Select(ep => ep.Port).Order().ToArray());
        Assert.All(endpoints, ep => Assert.Equal("http", ep.Scheme));
    }

    [Theory]
    [InlineData("language_server --csrf_token abc --https_server_port 0", 0, "abc")]
    [InlineData("language_server --csrf_token=abc --https_server_port=62173", 62173, "abc")]
    [InlineData("language_server --extension_server_port 1 --extension_server_csrf_token ext --csrf_token main --https_server_port 0", 0, "main")]
    [InlineData("language_server --extension_server_csrf_token ext --https_server_port 0", 0, "ext")]
    public void HttpsServerPortIsReadWithTheServersOwnToken(string cmd, int port, string token)
    {
        var https = QuotaMonitor.ParseHttpsServerFromCommandLine(cmd);
        Assert.NotNull(https);
        Assert.Equal(port, https.Value.Port);
        Assert.Equal(token, https.Value.CsrfToken);
    }

    [Theory]
    [InlineData("language_server --csrf_token abc")]
    [InlineData("language_server --https_server_port 0")]
    [InlineData("language_server --csrf_token abc --https_server_port -1")]
    [InlineData("language_server --csrf_token abc --https_server_port 65536")]
    [InlineData("language_server --csrf_token abc --https_server_port 0x10")]
    public void MissingOrInvalidHttpsServerFlagsAreNotCandidates(string cmd) =>
        Assert.Null(QuotaMonitor.ParseHttpsServerFromCommandLine(cmd));

    [Fact]
    public void RunTimeHttpsPortsComeFromTheSameProcessAfterEveryNamedPort()
    {
        // Issue #227: Antigravity 2.32 passes --https_server_port 0 and serves the RPC over HTTPS on a port it picks.
        var commands = new Dictionary<string, (int, string)[]>
        {
            ["language_server"] =
            [
                (41, "C:\\Antigravity\\language_server.exe --csrf_token=dyn --https_server_port 0 --extension_server_port 58324"),
                (42, "/opt/antigravity/language_server --csrf_token=other --https_server_port 0"),
            ],
            ["language_server_windows_x64"] = [(43, "antigravity\\language_server_windows_x64 --csrf_token=fixed --https_server_port=50001")],
            ["agy"] = [(44, "agy --hub --hub-port=18769 --csrf_token=hub")],
        };
        var asked = new List<int>();
        var endpoints = QuotaMonitor.FindAntigravityEndpoints(
            name => commands.GetValueOrDefault(name) ?? [],
            pid =>
            {
                asked.Add(pid);
                return pid switch
                {
                    41 => [V4(58324), V4(62173), V6(62174), V6(58324)],
                    42 => [V4(50001), V4(7000)],
                    _ => [V4(9999)],
                };
            }).ToArray();

        Assert.Equal(new[] { 41, 42 }, asked);
        Assert.Equal(new QuotaMonitor.AntigravityEndpoint[]
        {
            new(18769, "hub"),
            new(58324, "dyn"),                  // the explicit HTTP extension port of process 41
            new(50001, "fixed", "https"),       // a concrete HTTPS port
            new(62173, "dyn", "https"),         // process 41's own listeners, minus the port already named
            new(62174, "dyn", "https", "[::1]"), // an IPv6-only listener is asked on ::1
            new(58324, "dyn", "https", "[::1]"), // [::1]:58324 is not the named 127.0.0.1:58324
            new(7000, "other", "https"),        // 50001 is already a named candidate
        }, endpoints);
    }

    private static ListeningPorts.Listener V4(int port) => new(System.Net.IPAddress.Loopback, port);
    private static ListeningPorts.Listener V6(int port) => new(System.Net.IPAddress.IPv6Loopback, port);

    [Fact]
    public void EndpointTextNeverContainsTheToken()
    {
        var ep = new QuotaMonitor.AntigravityEndpoint(62173, "secret-csrf-token", "https");
        Assert.Equal("https://127.0.0.1:62173", ep.ToString());
        Assert.Equal("https://[::1]:62174", (ep with { Port = 62174, Host = "[::1]" }).ToString());
        Assert.DoesNotContain("secret-csrf-token", $"{ep}");
    }

    [Theory]
    [InlineData("https://127.0.0.1:62173/x", true)]
    [InlineData("https://[::1]:62173/x", true)]
    [InlineData("https://localhost:62173/x", true)]
    [InlineData("https://LOCALHOST:62173/x", true)]
    [InlineData("https://127.0.0.2:62173/x", true)]  // all of 127/8 is loopback
    [InlineData("https://192.168.1.10:62173/x", false)]
    [InlineData("https://example.com/x", false)]
    [InlineData("https://localhost.example.com/x", false)]
    [InlineData("https://127.0.0.1.nip.io/x", false)]
    [InlineData("http://127.0.0.1:62173/x", false)]
    public void InvalidCertificatesPassOnlyForLoopbackHttps(string url, bool accepted)
    {
        Assert.Equal(accepted, QuotaMonitor.AcceptLoopbackCertificate(new Uri(url), SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Equal(accepted, QuotaMonitor.AcceptLoopbackCertificate(new Uri(url),
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.True(QuotaMonitor.AcceptLoopbackCertificate(new Uri(url), SslPolicyErrors.None));
        Assert.False(QuotaMonitor.AcceptLoopbackCertificate(null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public async Task HttpsCandidatesUseTheLoopbackTlsClientAndHttpTheSharedOne()
    {
        var shared = new List<string>();
        var tls = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            shared.Add(request.RequestUri!.GetLeftPart(UriPartial.Authority));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        using var https = new HttpClient(new Handler((request, _) =>
        {
            tls.Add(request.RequestUri!.GetLeftPart(UriPartial.Authority));
            Assert.Equal("dyn", Assert.Single(request.Headers.GetValues("X-Codeium-Csrf-Token")));
            return Task.FromResult(JsonResponse("""{"groups":[{"buckets":[{"window":"5h","remaining":{"remainingFraction":0.5}}]}]}"""));
        }));
        var provider = await QuotaMonitor.ReadAntigravityAsync(http,
            [new(58324, "dyn"), new(62173, "dyn", "https")], CancellationToken.None, https);
        Assert.NotNull(provider);
        Assert.Equal(50, Assert.Single(provider.Limits).Percent);
        Assert.Equal(new[] { "http://127.0.0.1:58324", "http://127.0.0.1:58324" }, shared);
        Assert.Equal(new[] { "https://127.0.0.1:62173", "https://127.0.0.1:62173" }, tls);
    }

    [Fact]
    public async Task ReadsQuotasFromASelfSignedLoopbackHttpsServer()
    {
        using var cert = SelfSignedLoopbackCertificate();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(cert)));
        await using var app = builder.Build();
        app.MapPost("/exa.language_server_pb.LanguageServerService/{method}", (string method, HttpRequest request) =>
            request.Headers["X-Codeium-Csrf-Token"] != "loopback-token" ? Results.StatusCode(403)
            : method == "RetrieveUserQuotaSummary"
                ? Results.Text("""{"groups":[{"buckets":[{"window":"5h","remaining":{"remainingFraction":0.25}}]}]}""", "application/json")
                : Results.NotFound());
        await app.StartAsync();
        var port = new Uri(app.Urls.Single()).Port;

        // The certificate really is untrusted: a client with default validation gets nothing.
        using var strict = new HttpClient();
        Assert.Null(await QuotaMonitor.ReadAntigravityAsync(strict, [new(port, "loopback-token", "https")], CancellationToken.None));

        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("HTTPS went to the shared client")));
        using var https = new HttpClient(QuotaMonitor.NewLoopbackTlsHandler());
        var provider = await QuotaMonitor.ReadAntigravityAsync(http, [new(port, "loopback-token", "https")], CancellationToken.None, https);
        Assert.NotNull(provider);
        Assert.Equal(75, Assert.Single(provider.Limits).Percent);

        await app.StopAsync();
    }

    [Fact]
    public void LoopbackTlsHandlerChecksTheRequestHost()
    {
        using var handler = QuotaMonitor.NewLoopbackTlsHandler();
        var check = handler.ServerCertificateCustomValidationCallback!;
        using var local = new HttpRequestMessage(HttpMethod.Post, "https://127.0.0.1:62173/");
        using var remote = new HttpRequestMessage(HttpMethod.Post, "https://example.com/");
        Assert.True(check(local, null, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(check(remote, null, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
    }

    private static X509Certificate2 SelfSignedLoopbackCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // Windows' TLS stack can't use an ephemeral key, so the certificate goes through a PFX round trip.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"response\":null}")]
    [InlineData("{\"groups\":[null,2,{\"buckets\":[null,2,{}]}]}")]
    public void MalformedShapesDoNotThrowOrProduceMeasurements(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(QuotaMonitor.ParseAntigravityLimits(doc.RootElement));
        Assert.Empty(QuotaMonitor.ParseAntigravityLegacyLimits(doc.RootElement));
        Assert.Null(QuotaMonitor.ParseAntigravityPlan(doc.RootElement));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 0)]
    public void RealZeroAndFullMeasurementsAreRetained(double remaining, double used)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { groups = new[] { new { buckets = new[] { new { remainingFraction = remaining } } } } }));
        Assert.Equal(used, Assert.Single(QuotaMonitor.ParseAntigravityLimits(doc.RootElement)).Percent);
    }

    [Fact]
    public async Task ReadRetriesCandidatesAndUsesLegacyMeasuredQuotas()
    {
        var requests = new List<(int Port, string Method)>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Assert.Equal("1", Assert.Single(request.Headers.GetValues("Connect-Protocol-Version")));
            Assert.Equal("test-token", Assert.Single(request.Headers.GetValues("X-Codeium-Csrf-Token")));
            var method = request.RequestUri.Segments.Last();
            requests.Add((request.RequestUri.Port, method));
            if (request.RequestUri.Port == 18765 || method == "RetrieveUserQuotaSummary")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(JsonResponse("""
                {"userStatus":{"planStatus":{"planInfo":{"planName":"Ultra"}},"cascadeModelConfigData":{
                "clientModelConfigs":[{"label":"Gemini","quotaInfo":{"remainingFraction":0.25,"resetTime":"2026-11-15T07:30:12Z"}},
                {"label":"Unknown","quotaInfo":{"resetTime":"2026-11-15T07:30:12Z"}}]}}}
                """));
        }));
        var provider = await QuotaMonitor.ReadAntigravityAsync(http, [new(18765, "test-token"), new(18766, "test-token")], CancellationToken.None);
        Assert.NotNull(provider);
        Assert.Equal("Ultra", provider.Plan);
        Assert.Equal(75, Assert.Single(provider.Limits).Percent);
        Assert.Null(provider.Limits[0].WindowSec);
        Assert.NotNull(provider.Limits[0].ResetsAt);
        Assert.Equal(4, requests.Count);
    }

    [Fact]
    public async Task InvalidSummaryStillUsesTheMeasuredLegacyResponse()
    {
        using var http = new HttpClient(new Handler((request, _) =>
            Task.FromResult(request.RequestUri!.Segments.Last() == "RetrieveUserQuotaSummary"
                ? JsonResponse("<not an RPC response>")
                : JsonResponse("""
                    {"userStatus":{"planStatus":{"planInfo":{"planName":"Ultra"}},
                    "cascadeModelConfigData":{"clientModelConfigs":[{"label":"Gemini",
                    "quotaInfo":{"remainingFraction":0.25}}]}}}
                    """))));
        var provider = await QuotaMonitor.ReadAntigravityAsync(http, [new(18765, "test-token")], CancellationToken.None);
        Assert.NotNull(provider);
        Assert.Equal("Ultra", provider.Plan);
        Assert.Equal(75, Assert.Single(provider.Limits).Percent);
    }

    [Fact]
    public async Task PlanFailureDoesNotDiscardMeasuredSummary()
    {
        using var http = new HttpClient(new Handler((request, _) =>
            Task.FromResult(request.RequestUri!.Segments.Last() == "GetUserStatus"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : JsonResponse("""{"groups":[{"buckets":[{"window":"5h","remaining":{"remainingFraction":0.25}}]}]}"""))));
        var provider = await QuotaMonitor.ReadAntigravityAsync(http, [new(18765, "test-token")], CancellationToken.None);
        Assert.NotNull(provider);
        Assert.Equal("Antigravity", provider.Plan);
        Assert.Equal(75, Assert.Single(provider.Limits).Percent);
    }

    [Fact]
    public async Task CancelledReadStopsWithoutPublishingHealthyData()
    {
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(new Handler((_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not forwarded");
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            QuotaMonitor.ReadAntigravityAsync(http, [new(18765, "test-token")], cts.Token));
    }

    [Fact]
    public async Task MissingMeasurementsDoNotProduceAHealthyProvider()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(JsonResponse("""{"groups":[{"buckets":[{}]}]}"""))));
        Assert.Null(await QuotaMonitor.ReadAntigravityAsync(http, [new(18765, "test-token")], CancellationToken.None));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    [Fact]
    public void NestedRemainingFractionIsMeasuredInsteadOfReportedAsUnused()
    {
        using var doc = JsonDocument.Parse("""
            {"response":{"groups":[{"buckets":[{"bucketId":"gemini-5h",
            "remaining":{"remainingFraction":0.25},"resetTime":"2026-11-15T07:30:12Z"}]}]}}
            """);
        var limit = Assert.Single(QuotaMonitor.ParseAntigravityLimits(doc.RootElement));
        Assert.Equal(75, limit.Percent);
        Assert.NotNull(limit.ResetsAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"remainingFraction\":null}")]
    [InlineData("{\"remainingFraction\":\"unknown\"}")]
    [InlineData("{\"remainingFraction\":-0.1}")]
    [InlineData("{\"remainingFraction\":1.1}")]
    public void UnknownOrInvalidMeasurementsAreSkipped(string bucket)
    {
        using var doc = JsonDocument.Parse("{\"groups\":[{\"buckets\":[" + bucket + "]}]}");
        Assert.Empty(QuotaMonitor.ParseAntigravityLimits(doc.RootElement));
    }

    [Theory]
    [InlineData("language_server_windows_x64 --extension_server_port=18766 --csrf_token=legacy", 18766, "legacy")]
    [InlineData("language_server_linux_x64 --extension_server_port 18766 --csrf_token main --extension_server_csrf_token extension", 18766, "extension")]
    [InlineData("agy --port=18765 --hub-port=18766 --csrf_token main", 18766, "main")]
    public void EndpointUsesTheCorrectPortAndMatchingToken(string cmd, int port, string token)
    {
        var endpoint = QuotaMonitor.ParseEndpointFromCommandLine(cmd);
        Assert.NotNull(endpoint);
        Assert.Equal(port, endpoint.Value.Port);
        Assert.Equal(token, endpoint.Value.CsrfToken);
    }

    [Theory]
    [InlineData("agy --hub-port=0 --csrf_token=x")]
    [InlineData("agy --hub-port=65536 --csrf_token=x")]
    [InlineData("agy --hub-port=123abc --csrf_token=x")]
    public void InvalidPortsAreNotEndpoints(string cmd) => Assert.Null(QuotaMonitor.ParseEndpointFromCommandLine(cmd));

    /// <summary>
    /// Verifies that port and CSRF tokens are correctly extracted from various command-line flag styles.
    /// </summary>
    /// <param name="cmd">The command line to parse.</param>
    /// <param name="expectedPort">The expected port number.</param>
    /// <param name="expectedCsrf">The expected CSRF token string.</param>
    [Theory]
    [InlineData("agy.exe --hub --hub-port=13897 --csrf_token=token-12345", 13897, "token-12345")]
    [InlineData("language_server --port 8080 --csrfToken abc-xyz", 8080, "abc-xyz")]
    [InlineData("Antigravity --hub_port=9090 --csrf_token \"my-quoted-token\"", 9090, "my-quoted-token")]
    public void ParseEndpointFromCommandLine_ExtractsPortAndToken(string cmd, int expectedPort, string expectedCsrf)
    {
        var ep = QuotaMonitor.ParseEndpointFromCommandLine(cmd);
        Assert.NotNull(ep);
        Assert.Equal(expectedPort, ep.Value.Port);
        Assert.Equal(expectedCsrf, ep.Value.CsrfToken);
    }

    /// <summary>
    /// Verifies that command lines missing required endpoint flags return <c>null</c>.
    /// </summary>
    /// <param name="cmd">The command line to evaluate.</param>
    [Theory]
    [InlineData("")]
    [InlineData("agy.exe --hub --hub-port=13897")]
    [InlineData("language_server --csrf_token=my-token")]
    [InlineData("notepad.exe somefile.txt")]
    public void ParseEndpointFromCommandLine_Invalid_ReturnsNull(string cmd)
    {
        var ep = QuotaMonitor.ParseEndpointFromCommandLine(cmd);
        Assert.Null(ep);
    }

    /// <summary>
    /// Verifies that multi-group quota buckets are mapped and ordered with session windows before weekly windows.
    /// </summary>
    [Fact]
    public void ParseAntigravityLimits_MultiGroup_OrdersSessionBeforeWeekly()
    {
        var json = """
        {
            "response": {
                "groups": [
                    {
                        "displayName": "Gemini Models",
                        "buckets": [
                            {
                                "bucketId": "gemini-weekly",
                                "window": "weekly",
                                "remainingFraction": 0.95,
                                "resetTime": "2026-11-15T07:30:12Z"
                            },
                            {
                                "bucketId": "gemini-5h",
                                "window": "5h",
                                "remainingFraction": 0.90,
                                "resetTime": "2026-11-09T22:15:55Z"
                            }
                        ]
                    },
                    {
                        "displayName": "Claude and GPT models",
                        "buckets": [
                            {
                                "bucketId": "3p-weekly",
                                "window": "weekly",
                                "remainingFraction": 0.15,
                                "resetTime": "2026-11-16T17:46:14Z"
                            },
                            {
                                "bucketId": "3p-5h",
                                "window": "5h",
                                "remainingFraction": 1.0,
                                "resetTime": "2026-11-09T22:46:14Z"
                            }
                        ]
                    }
                ]
            }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var limits = QuotaMonitor.ParseAntigravityLimits(doc.RootElement);

        Assert.Equal(4, limits.Count);

        // 1. Gemini 5-hour
        Assert.Equal("5-hour (Gemini)", limits[0].Label);
        Assert.Equal(10.0, limits[0].Percent);
        Assert.Equal(18000L, limits[0].WindowSec);
        Assert.Equal("normal", limits[0].Severity);

        // 2. Gemini Weekly
        Assert.Equal("Weekly (Gemini)", limits[1].Label);
        Assert.Equal(5.0, limits[1].Percent);
        Assert.Equal(604800L, limits[1].WindowSec);
        Assert.Equal("normal", limits[1].Severity);

        // 3. Claude/GPT 5-hour
        Assert.Equal("5-hour (Claude/GPT)", limits[2].Label);
        Assert.Equal(0.0, limits[2].Percent);
        Assert.Equal(18000L, limits[2].WindowSec);
        Assert.Equal("normal", limits[2].Severity);

        // 4. Claude/GPT Weekly (85% used -> warning)
        Assert.Equal("Weekly (Claude/GPT)", limits[3].Label);
        Assert.Equal(85.0, limits[3].Percent);
        Assert.Equal(604800L, limits[3].WindowSec);
        Assert.Equal("warning", limits[3].Severity);
    }

    /// <summary>
    /// Verifies that single-group quota responses use standard 5-hour session and weekly labels.
    /// </summary>
    [Fact]
    public void ParseAntigravityLimits_SingleGroup_UsesSimpleLabels()
    {
        var json = """
        {
            "response": {
                "groups": [
                    {
                        "displayName": "Gemini Models",
                        "buckets": [
                            {
                                "bucketId": "gemini-5h",
                                "window": "5h",
                                "remainingFraction": 0.02,
                                "resetTime": "2026-11-09T22:15:55Z"
                            },
                            {
                                "bucketId": "gemini-weekly",
                                "window": "weekly",
                                "remainingFraction": 0.80,
                                "resetTime": "2026-11-15T07:30:12Z"
                            }
                        ]
                    }
                ]
            }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var limits = QuotaMonitor.ParseAntigravityLimits(doc.RootElement);

        Assert.Equal(2, limits.Count);
        Assert.Equal("5-hour session", limits[0].Label);
        Assert.Equal(98.0, limits[0].Percent);
        Assert.Equal("critical", limits[0].Severity);

        Assert.Equal("Weekly", limits[1].Label);
        Assert.Equal(20.0, limits[1].Percent);
        Assert.Equal("normal", limits[1].Severity);
    }

    /// <summary>
    /// Verifies that empty or malformed JSON payloads return an empty list without throwing exceptions.
    /// </summary>
    [Fact]
    public void ParseAntigravityLimits_MalformedOrEmpty_ReturnsEmptyList()
    {
        using var doc = JsonDocument.Parse("{}");
        var limits = QuotaMonitor.ParseAntigravityLimits(doc.RootElement);
        Assert.Empty(limits);
    }

    /// <summary>
    /// Verifies that the plan name is parsed from planInfo or falls back to userTier.
    /// </summary>
    [Fact]
    public void ParseAntigravityPlan_ExtractsPlanName_FallsBackToUserTier()
    {
        var jsonWithPlanName = """
        {
            "userStatus": {
                "planStatus": {
                    "planInfo": {
                        "planName": "Pro"
                    }
                },
                "userTier": {
                    "name": "Google AI Pro"
                }
            }
        }
        """;

        using var doc1 = JsonDocument.Parse(jsonWithPlanName);
        Assert.Equal("Pro", QuotaMonitor.ParseAntigravityPlan(doc1.RootElement));

        var jsonWithTierOnly = """
        {
            "userStatus": {
                "userTier": {
                    "name": "Google AI Ultra"
                }
            }
        }
        """;

        using var doc2 = JsonDocument.Parse(jsonWithTierOnly);
        Assert.Equal("Google AI Ultra", QuotaMonitor.ParseAntigravityPlan(doc2.RootElement));

        using var docEmpty = JsonDocument.Parse("{}");
        Assert.Null(QuotaMonitor.ParseAntigravityPlan(docEmpty.RootElement));
    }
}
