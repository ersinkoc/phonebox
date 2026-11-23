using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed partial class QuotaMonitor
{
    private static readonly string[] AntigravityProcessNames =
    [
        "agy", "language_server", "language-server", "Antigravity", "Antigravity IDE",
        "language_server_macos", "language_server_macos_arm", "language_server_linux",
        "language_server_windows_x64", "language_server_linux_x64", "language_server_linux_arm"
    ];

    /// <summary>
    /// One loopback RPC candidate. Older servers answer plain HTTP on the port from their command line; newer ones serve
    /// HTTPS with a self-signed certificate, on a port that may be chosen at run time.
    /// </summary>
    /// <param name="Host">The loopback address in URL form: 127.0.0.1, or [::1] for a server that listens on IPv6 only.</param>
    internal readonly record struct AntigravityEndpoint(int Port, string CsrfToken, string Scheme = "http", string Host = "127.0.0.1")
    {
        /// <summary>Never includes the CSRF token, so a candidate can't leak it into a log line.</summary>
        public override string ToString() => $"{Scheme}://{Host}:{Port}";
    }

    private HttpClient? _loopbackTlsClient;

    /// <summary>Only for the Antigravity server's HTTPS port; the shared client keeps full certificate validation.</summary>
    private HttpClient LoopbackTlsHttp => _loopbackTlsClient ??= NewHttpClient(NewLoopbackTlsHandler());

    /// <summary>
    /// A handler that accepts a certificate which fails validation only for requests to this computer, where the
    /// Antigravity server's certificate is self-signed. Redirects are off, so a response can't move the request elsewhere.
    /// </summary>
    internal static HttpClientHandler NewLoopbackTlsHandler() => new()
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (request, _, _, errors) => AcceptLoopbackCertificate(request.RequestUri, errors),
    };

    /// <summary>A valid certificate always passes; an invalid one only for HTTPS to a loopback address or localhost.</summary>
    internal static bool AcceptLoopbackCertificate(Uri? uri, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (uri is not { IsAbsoluteUri: true } || uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// Refreshes Antigravity quota and plan data by discovering the local language server process and querying its RPC endpoints.
    /// </summary>
    /// <param name="ct">Cancellation token for async operations.</param>
    /// <returns><c>true</c> if quotas were successfully refreshed; otherwise, <c>false</c>.</returns>
    private async Task<bool> RefreshAntigravity(CancellationToken ct)
    {
        var endpoints = FindAntigravityEndpoints().Distinct().Take(8).ToArray();
        var provider = endpoints.Length > 0 ? await ReadAntigravityAsync(Http, endpoints, ct, LoopbackTlsHttp) : null;
        var cli = FindAntigravityCli();
        if (provider is null && cli is not null)
        {
            try { provider = await ReadAntigravityCliAsync(cli, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or System.ComponentModel.Win32Exception)
            { log.LogDebug("Antigravity CLI quota refresh failed ({Kind})", ex.GetType().Name); }
        }
        if (endpoints.Length == 0 && cli is null)
        {
            _antigravity = _antigravity is { Limits.Count: > 0 } prev
                ? prev with { Error = "Antigravity process not running" }
                : null;
            return false;
        }

        if (provider is not null)
        {
            _antigravity = provider;
            return true;
        }
        const string error = "Could not read local Antigravity quotas";
        _antigravity = _antigravity is { Limits.Count: > 0 } cached
            ? cached with { Error = error } : Fail("antigravity", "Antigravity", error);
        return false;
    }

    /// <summary>Finds the CLI on PATH or in a standard per-user installation.</summary>
    private static string? FindAntigravityCli() => ToolProcess.Find("agy") ?? AntigravityCliCandidates(
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)).FirstOrDefault(File.Exists);

    /// <summary>
    /// Where the installers put agy when its folder is not on PATH: the current installer and the CLI's in-place
    /// self-update use ~/.gemini/bin (#462), older Windows installers %LOCALAPPDATA%\agy\bin.
    /// </summary>
    internal static IEnumerable<string> AntigravityCliCandidates(bool windows, string home, string localAppData)
    {
        if (home.Length > 0) yield return Path.Combine(home, ".gemini", "bin", windows ? "agy.exe" : "agy");
        if (windows && localAppData.Length > 0) yield return Path.Combine(localAppData, "agy", "bin", "agy.exe");
    }

    /// <summary>Reads the CLI's structured usage command; older versions must never interpret it as a model prompt.</summary>
    internal static async Task<QuotaProvider?> ReadAntigravityCliAsync(string path, CancellationToken ct,
        Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ToolProcess.Result>>? run = null)
    {
        run ??= ToolProcess.RunAsync;
        ProcessStartInfo Start(params string[] args)
        {
            var start = new ProcessStartInfo(path);
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";
            start.Environment["BROWSER"] = "false";
            return start;
        }
        var version = await run(Start("--version"), TimeSpan.FromSeconds(3), ct);
        if (version.ExitCode != 0 || !Version.TryParse(version.Output.Trim().TrimStart('v'), out var parsed)
            || parsed < new Version(1, 1, 11)) return null;
        var result = await run(Start("-p", "/usage", "--output-format", "json", "--print-timeout", "15s"), TimeSpan.FromSeconds(20), ct);
        if (result.ExitCode != 0 || result.Output.Length > 1 << 20) return null;
        using var doc = JsonDocument.Parse(result.Output);
        if (Str(doc.RootElement, "status") != "SUCCESS" || Str(AntigravityProp(doc.RootElement, "command"), "name") != "usage")
            return null;
        var limits = ParseAntigravityLimits(doc.RootElement);
        return limits.Count == 0 ? null : new QuotaProvider("antigravity", "Antigravity", "Antigravity", limits,
            null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
    }

    /// <summary>
    /// Tries bounded loopback candidates in order; an unavailable server must not hide a healthy one.
    /// HTTPS candidates go through <paramref name="https"/> (the loopback-only certificate check), the rest through
    /// <paramref name="http"/>; without <paramref name="https"/> both use <paramref name="http"/>.
    /// </summary>
    /// <param name="perEndpoint">For tests: how long one candidate may take (3 s), on a busy test runner.</param>
    internal static async Task<QuotaProvider?> ReadAntigravityAsync(HttpClient http,
        IEnumerable<AntigravityEndpoint> endpoints, CancellationToken ct, HttpClient? https = null, TimeSpan? perEndpoint = null)
    {
        var each = perEndpoint ?? TimeSpan.FromSeconds(3);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(each > TimeSpan.FromSeconds(3) ? each * 2 : TimeSpan.FromSeconds(10));
        foreach (var ep in endpoints.Distinct().Take(8))
        {
            ct.ThrowIfCancellationRequested();
            if (budget.IsCancellationRequested) break;
            if (ep.Scheme is not ("http" or "https")) continue;
            var client = ep.Scheme == "https" ? https ?? http : http;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            deadline.CancelAfter(each);
            using var summary = await Post("RetrieveUserQuotaSummary");
            var limits = summary is not null ? ParseAntigravityLimits(summary.RootElement) : [];
            using var status = await Post("GetUserStatus");
            var plan = status is not null ? ParseAntigravityPlan(status.RootElement) : null;
            if (status is not null && limits.Count == 0)
                limits = ParseAntigravityLegacyLimits(status.RootElement);
            if (limits.Count > 0)
                return new QuotaProvider("antigravity", "Antigravity", plan ?? "Antigravity", limits,
                    null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);

            async Task<JsonDocument?> Post(string method)
            {
                ct.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested) return null;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post,
                        $"{ep}/exa.language_server_pb.LanguageServerService/{method}");
                    req.Headers.Add("X-Codeium-Csrf-Token", ep.CsrfToken);
                    req.Headers.Add("Connect-Protocol-Version", "1");
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(req, deadline.Token);
                    if (!response.IsSuccessStatusCode) return null;
                    return JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException) { return null; }
            }
        }
        return null;
    }

    /// <summary>
    /// Extracts the subscription plan name from a GetUserStatus response JSON.
    /// </summary>
    /// <param name="root">Root JSON element of the user status response.</param>
    /// <returns>The plan name, or <c>null</c> if not present.</returns>
    internal static string? ParseAntigravityPlan(JsonElement root)
    {
        var u = AntigravityProp(root, "userStatus") is { ValueKind: JsonValueKind.Object } us ? us : root;
        var planInfo = AntigravityProp(AntigravityProp(u, "planStatus"), "planInfo");
        var name = Str(planInfo, "planName");
        if (string.IsNullOrWhiteSpace(name))
            name = Str(AntigravityProp(u, "userTier"), "name");
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Parses quota groups and bucket limits from a RetrieveUserQuotaSummary response JSON, converting remaining fractions to used percentages.
    /// </summary>
    /// <param name="root">Root JSON element of the quota summary response.</param>
    /// <returns>A list of parsed quota limits ordered by window duration.</returns>
    internal static List<QuotaLimit> ParseAntigravityLimits(JsonElement root)
    {
        var list = new List<QuotaLimit>();
        var resp = AntigravityProp(root, "response") is { ValueKind: JsonValueKind.Object } r ? r : root;
        if (AntigravityProp(root, "command") is { ValueKind: not JsonValueKind.Undefined } command)
        {
            if (Str(root, "status") != "SUCCESS" || Str(command, "name") != "usage") return list;
            resp = AntigravityProp(command, "data");
        }
        var groups = AntigravityProp(resp, "groups");
        if (groups.ValueKind != JsonValueKind.Array)
            return list;

        var groupCount = groups.GetArrayLength();
        foreach (var g in groups.EnumerateArray())
        {
            var groupName = Str(g, "displayName") ?? Str(g, "name") ?? "";
            var isGemini = groupName.Contains("Gemini", StringComparison.OrdinalIgnoreCase);
            var isClaudeOrGpt = groupName.Contains("Claude", StringComparison.OrdinalIgnoreCase)
                || groupName.Contains("GPT", StringComparison.OrdinalIgnoreCase);
            var groupPrefix = isGemini ? "Gemini" : isClaudeOrGpt ? "Claude/GPT" : groupName;

            var buckets = AntigravityProp(g, "buckets");
            if (buckets.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var b in buckets.EnumerateArray())
            {
                var bucketId = Str(b, "bucketId") ?? Str(b, "id") ?? "";
                var window = Str(b, "window") ?? "";
                var is5h = window == "5h" || bucketId.Contains("5h", StringComparison.OrdinalIgnoreCase);
                var isWeekly = window == "weekly" || bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase);

                string label;
                long? windowSec;
                if (is5h)
                {
                    label = groupCount > 1 ? $"5-hour ({groupPrefix})" : "5-hour session";
                    windowSec = 5 * 3600L;
                }
                else if (isWeekly)
                {
                    label = groupCount > 1 ? $"Weekly ({groupPrefix})" : "Weekly";
                    windowSec = 7 * 86400L;
                }
                else
                {
                    label = Str(b, "displayName") ?? Str(b, "name") ?? bucketId;
                    windowSec = null;
                }

                // Current servers nest the measured fraction; older servers put it on the bucket.
                // Unknown usage must not become a healthy, unused quota.
                var fraction = Num(AntigravityProp(b, "remaining"), "remainingFraction") ?? Num(b, "remainingFraction")
                    ?? Num(b, "remaining_fraction");
                if (fraction is not { } rem || rem < 0 || rem > 1) continue;
                var usedPct = Math.Round((1.0 - rem) * 100.0, 1);
                var resetsAt = Iso(Str(b, "resetTime") ?? Str(b, "reset_time"));
                var severity = usedPct >= 95 ? "critical" : usedPct >= 80 ? "warning" : "normal";

                list.Add(new QuotaLimit(label, usedPct, resetsAt, windowSec, severity));
            }
        }

        return list
            .OrderBy(l => l.Label.Contains("Claude", StringComparison.OrdinalIgnoreCase) || l.Label.Contains("GPT", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(l => l.WindowSec ?? long.MaxValue)
            .ToList();
    }

    /// <summary>Reads measured model quotas from older IDE servers without inventing weekly limits.</summary>
    internal static List<QuotaLimit> ParseAntigravityLegacyLimits(JsonElement root)
    {
        var status = AntigravityProp(root, "userStatus");
        var models = AntigravityProp(AntigravityProp(status, "cascadeModelConfigData"), "clientModelConfigs");
        var list = new List<QuotaLimit>();
        if (models.ValueKind != JsonValueKind.Array) return list;
        foreach (var model in models.EnumerateArray())
        {
            var quota = AntigravityProp(model, "quotaInfo");
            var label = Str(model, "label");
            if (string.IsNullOrWhiteSpace(label) || Num(quota, "remainingFraction") is not { } rem || rem < 0 || rem > 1) continue;
            var percent = Math.Round((1 - rem) * 100, 1);
            list.Add(new QuotaLimit(label, percent, Iso(Str(quota, "resetTime")), null,
                percent >= 95 ? "critical" : percent >= 80 ? "warning" : "normal"));
        }
        return list;
    }

    /// <summary>
    /// Extracts the loopback port and CSRF token flags from a process command line.
    /// </summary>
    /// <param name="cmd">The command line string.</param>
    /// <returns>A tuple containing the port and CSRF token, or <c>null</c> if not found.</returns>
    internal static (int Port, string CsrfToken)? ParseEndpointFromCommandLine(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        var hubPort = AntigravityFlag(cmd, "hub-port", "hub_port", "port");
        var csrf = hubPort is null
            ? AntigravityFlag(cmd, "extension_server_csrf_token", "csrf_token", "csrfToken")
            : AntigravityFlag(cmd, "csrf_token", "csrfToken");
        var portValue = hubPort ?? AntigravityFlag(cmd, "extension_server_port");
        if (int.TryParse(portValue, out var port) && port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(csrf))
            return (port, csrf);
        return null;
    }

    /// <summary>
    /// Extracts the language server's own HTTPS port and its CSRF token. Port 0 means the server picked a free port at
    /// start-up, which then has to be found among the ports the process listens on.
    /// </summary>
    /// <param name="cmd">The command line string.</param>
    /// <returns>The HTTPS port (0 when chosen at run time) and token, or <c>null</c> without a usable flag pair.</returns>
    internal static (int Port, string CsrfToken)? ParseHttpsServerFromCommandLine(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        // The server's own RPC checks --csrf_token; the extension token belongs to the IDE's extension server.
        var csrf = AntigravityFlag(cmd, "csrf_token", "csrfToken", "extension_server_csrf_token");
        if (int.TryParse(AntigravityFlag(cmd, "https_server_port", "https-server-port"), out var port)
            && port is >= 0 and <= 65535 && !string.IsNullOrWhiteSpace(csrf))
            return (port, csrf);
        return null;
    }

    /// <summary>The value of the last occurrence of the first flag name present, or <c>null</c>.</summary>
    private static string? AntigravityFlag(string cmd, params string[] names)
    {
        foreach (var name in names)
        {
            var match = AntigravityFlagRegex().Matches(cmd).Cast<Match>()
                .LastOrDefault(m => string.Equals(m.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match.Groups["double"].Success ? match.Groups["double"].Value
                    : match.Groups["single"].Success ? match.Groups["single"].Value : match.Groups["bare"].Value;
        }
        return null;
    }

    private static JsonElement AntigravityProp(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value : default;

    /// <summary>
    /// Enumerates only Antigravity-marked candidates, including legacy server names. Ports named on a command line come
    /// first, from every process, so the bounded reader tries them before guessing among a process's listening ports;
    /// those are tried over HTTPS with the token of the same process.
    /// </summary>
    /// <param name="readCommands">For tests: the process id and command line of each process with the given name.</param>
    /// <param name="listeningPorts">For tests: the loopback TCP listeners of a process id.</param>
    internal static IEnumerable<AntigravityEndpoint> FindAntigravityEndpoints(
        Func<string, IEnumerable<(int Pid, string CommandLine)>>? readCommands = null,
        Func<int, IReadOnlyList<ListeningPorts.Listener>>? listeningPorts = null)
    {
        var named = new HashSet<(string Host, int Port)>(); // 127.0.0.1:62173 and [::1]:62173 are different sockets
        var dynamic = new List<(int Pid, string CsrfToken)>();
        foreach (var name in AntigravityProcessNames)
        foreach (var (pid, cmd) in (readCommands ?? ReadAntigravityCommands)(name))
        {
            if (name != "agy" && !cmd.Contains("antigravity", StringComparison.OrdinalIgnoreCase)) continue;
            if (ParseEndpointFromCommandLine(cmd) is { } ep)
            {
                named.Add(("127.0.0.1", ep.Port));
                yield return new AntigravityEndpoint(ep.Port, ep.CsrfToken);
            }
            if (ParseHttpsServerFromCommandLine(cmd) is not { } https) continue;
            if (https.Port > 0)
            {
                named.Add(("127.0.0.1", https.Port));
                yield return new AntigravityEndpoint(https.Port, https.CsrfToken, "https");
            }
            else if (pid > 0) dynamic.Add((pid, https.CsrfToken));
        }
        foreach (var (pid, csrf) in dynamic)
        foreach (var listener in (listeningPorts ?? (id => ListeningPorts.ForProcess(id)))(pid))
        {
            var host = ListeningPorts.UrlHost(listener.Address);
            if (!named.Contains((host, listener.Port))) yield return new AntigravityEndpoint(listener.Port, csrf, "https", host);
        }
    }

    private static IEnumerable<(int Pid, string CommandLine)> ReadAntigravityCommands(string name)
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName(name); }
        catch { yield break; }
        try
        {
            foreach (var p in procs)
            {
                string? cmd = null;
                try { cmd = GetCommandLine(p.Id); } catch { }
                if (!string.IsNullOrEmpty(cmd)) yield return (p.Id, cmd);
            }
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    /// <summary>
    /// Retrieves the command-line arguments for a process across supported operating systems.
    /// </summary>
    /// <param name="pid">The process identifier.</param>
    /// <returns>The command-line string, or <c>null</c> if unreadable.</returns>
    private static string? GetCommandLine(int pid)
    {
        if (OperatingSystem.IsWindows())
            return GetWindowsCommandLine(pid);
        if (OperatingSystem.IsLinux())
        {
            try { return File.Exists($"/proc/{pid}/cmdline") ? File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim() : null; }
            catch { return null; }
        }
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                return ToolProcess.RunAsync(new ProcessStartInfo("/bin/ps", $"-ww -p {pid} -o args="),
                    TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult().Output.Trim();
            }
            catch { return null; }
        }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int pid);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint h, int infoClass, nint info, int length, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint h);

    /// <summary>
    /// Reads the process command line on Windows using NtQueryInformationProcess.
    /// </summary>
    /// <param name="pid">The target process identifier.</param>
    /// <returns>The command line string, or <c>null</c> if query fails.</returns>
    private static string? GetWindowsCommandLine(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try
        {
            NtQueryInformationProcess(h, 60 /* ProcessCommandLineInformation */, 0, 0, out var len);
            if (len <= 0 || len > 1 << 20) return null;
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (NtQueryInformationProcess(h, 60, buf, len, out _) != 0) return null;
                var chars = (ushort)Marshal.ReadInt16(buf) / 2;
                return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, IntPtr.Size), chars);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Reads complete flag values, including quoted tokens, without accepting numeric prefixes.
    /// </summary>
    [GeneratedRegex("(?:^|\\s)--(?<name>[a-zA-Z_-]+)(?:=|\\s+)(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)'|(?<bare>(?!--)[^\\s\"']+))(?=\\s|$)")]
    private static partial Regex AntigravityFlagRegex();
}
