using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public sealed class GubaFeedTests
{
    // Reduced, anonymized fixture using the observed article_list schema.
    private const string FeedPage = """
        <!doctype html><html><body>
        <script>var article_list = {"re":[
          {"post_id":661,"post_top_status":1,"post_title":"Pinned notice"},
          null,
          {"post_id":"662","post_title":"Morning market notes","user_nickname":"Fixture author",
           "post_publish_time":"2026-01-01 09:20","post_click_count":36,"post_comment_count":2},
          {"post_id":663,"post_title":"Lunch notes","post_click_count":"unknown","post_comment_count":3}
        ]};</script></body></html>
        """;

    [Fact]
    public void ReadsRegularPostsFromTheEmbeddedFeed()
    {
        var posts = Assert.IsType<List<object>>(GubaFeed.ParsePage(FeedPage));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(posts, ConfigStore.WriteOpts));
        var rows = doc.RootElement.EnumerateArray().ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Equal("662", rows[0].GetProperty("id").GetString());
        Assert.Equal("Morning market notes", rows[0].GetProperty("title").GetString());
        Assert.Equal(36, rows[0].GetProperty("reads").GetInt64());
        Assert.Equal(2, rows[0].GetProperty("comments").GetInt64());
        Assert.Equal(0, rows[1].GetProperty("reads").GetInt64());
    }

    [Theory]
    [InlineData("<html>Rate limit</html>")]
    [InlineData("<script>var article_list = {broken};</script>")]
    [InlineData("<script>var article_list = {\"re\":null};</script>")]
    public void ABlockedOrMalformedPageAllowsTheFallback(string page) => Assert.Null(GubaFeed.ParsePage(page));
}
