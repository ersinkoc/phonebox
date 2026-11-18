using System.Text;
using PhoneBox.Agent;
using Xunit;

public sealed class GitHubEncodingTests
{
    // gh always writes UTF-8; read in the console's code page, a Hebrew pull request title came out as mojibake.
    [Fact]
    public void GhOutputIsReadAsUtf8()
    {
        var start = GitHubMonitor.GhStart(["pr", "list", "--json", "title"]);
        Assert.Equal("gh", start.FileName);
        Assert.Equal(["pr", "list", "--json", "title"], start.ArgumentList);
        Assert.Equal(Encoding.UTF8.WebName, start.StandardOutputEncoding?.WebName);
        Assert.Equal(Encoding.UTF8.WebName, start.StandardErrorEncoding?.WebName);
    }
}
