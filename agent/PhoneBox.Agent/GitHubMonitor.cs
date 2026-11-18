using System.Diagnostics;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record GhPr(string Repo, int Number, string Title, string? Author, bool Draft, string? Review, string? Ci, long Updated);

public sealed record GhSnapshot(string? User, int Notifications, IReadOnlyList<GhPr> ReviewRequests, IReadOnlyList<GhPr> MyPrs,
    long? UpdatedAt, string? Error);

/// <summary>Collects a pull request and notification summary through the GitHub CLI (gh) every 3 minutes.</summary>
public sealed class GitHubMonitor(ILogger<GitHubMonitor> log) : BackgroundService
{
    private const string Query = """
        query {
          viewer {
            login
            pullRequests(states: OPEN, first: 8, orderBy: {field: UPDATED_AT, direction: DESC}) {
              nodes {
                number title isDraft reviewDecision updatedAt
                repository { nameWithOwner }
                commits(last: 1) { nodes { commit { statusCheckRollup { state } } } }
              }
            }
          }
          search(query: "is:pr is:open archived:false review-requested:@me", type: ISSUE, first: 8) {
            nodes {
              ... on PullRequest { number title isDraft updatedAt author { login } repository { nameWithOwner } }
            }
          }
        }
        """;

    private volatile GhSnapshot? _snap;
    public GhSnapshot? Snapshot() => _snap;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(3000, ct);
        while (!ct.IsCancellationRequested)
        {
            try { _snap = await Fetch(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogDebug("github: {Msg}", ex.Message);
                _snap = (_snap ?? new GhSnapshot(null, 0, [], [], null, null)) with { Error = ex.Message };
            }
            await Wake.Delay(TimeSpan.FromMinutes(3), ct);
        }
    }

    private static async Task<GhSnapshot> Fetch(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Gh(["api", "graphql", "-f", "query=" + Query], ct));
        var data = doc.RootElement.GetProperty("data");
        var viewer = data.GetProperty("viewer");

        var mine = viewer.GetProperty("pullRequests").GetProperty("nodes").EnumerateArray().Select(n =>
        {
            string? ci = null;
            var commits = n.GetProperty("commits").GetProperty("nodes");
            if (commits.GetArrayLength() > 0 && commits[0].GetProperty("commit").GetProperty("statusCheckRollup") is { ValueKind: JsonValueKind.Object } r)
                ci = r.GetProperty("state").GetString();
            return Pr(n, null, ci, Str(n, "reviewDecision"));
        }).ToList();

        var reviews = data.GetProperty("search").GetProperty("nodes").EnumerateArray()
            .Where(n => n.TryGetProperty("number", out _))
            .Select(n => Pr(n, n.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "login") : null, null, null))
            .ToList();

        var notifications = 0;
        try
        {
            using var nd = JsonDocument.Parse(await Gh(["api", "notifications?per_page=50"], ct));
            notifications = nd.RootElement.GetArrayLength();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        return new GhSnapshot(Str(viewer, "login"), notifications, reviews, mine, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
    }

    private static GhPr Pr(JsonElement n, string? author, string? ci, string? review) => new(
        n.GetProperty("repository").GetProperty("nameWithOwner").GetString() ?? "",
        n.GetProperty("number").GetInt32(),
        Str(n, "title") ?? "",
        author,
        n.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True,
        review, ci,
        DateTimeOffset.TryParse(Str(n, "updatedAt"), out var u) ? u.ToUnixTimeMilliseconds() : 0);

    internal static Task<string> Gh(string[] args, CancellationToken ct) => RunGh(GhStart(args), ct);

    /// <summary>
    /// gh writes UTF-8 whatever the console is set to. Without saying so, Windows reads the output in the system's
    /// OEM code page, and a pull request title in Hebrew (or any script outside that page) reaches the phone as
    /// mojibake.
    /// </summary>
    internal static ProcessStartInfo GhStart(string[] args)
    {
        var psi = new ProcessStartInfo("gh")
        {
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    internal static async Task<string> RunGh(ProcessStartInfo psi, CancellationToken ct)
    {
        var result = await ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(20), ct);
        if (result.ExitCode != 0) throw new InvalidOperationException("gh: " + result.Error.Trim().Split('\n')[0]);
        return result.Output;
    }

    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
