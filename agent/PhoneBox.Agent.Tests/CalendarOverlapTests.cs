using PhoneBox.Agent;
using Xunit;

public sealed class CalendarOverlapTests
{
    [Theory]
    [InlineData(0, "10")]
    [InlineData(1, "10")]
    [InlineData(2, "9,10")]
    [InlineData(3, "8,9,10")]
    [InlineData(4, "7,8,9,10")]
    [InlineData(10, "1,2,3,4,5,6,7,8,9,10")]
    public void RecurrenceFastForwardRetainsEveryOverlappingOccurrence(int days, string expectedDays)
    {
        var from = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var feed = $"BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:long\nDTSTART:20260101T000000Z\nDURATION:P{days}D\nRRULE:FREQ=DAILY\nSUMMARY:Long\nEND:VEVENT\nEND:VCALENDAR\n";
        var actual = Ics.Parse(feed, from, from.AddDays(1))
            .Select(e => DateTimeOffset.FromUnixTimeMilliseconds(e.Start).UtcDateTime.Day);
        Assert.Equal(expectedDays, string.Join(",", actual));
    }

    [Fact]
    public void ALongAllDayOccurrenceStartingBeforeTheWindowIsKept()
    {
        var from = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var feed = "BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:all\nDTSTART;VALUE=DATE:20260101\nDTEND;VALUE=DATE:20260105\nRRULE:FREQ=DAILY\nSUMMARY:All day\nEND:VEVENT\nEND:VCALENDAR\n";
        var events = Ics.Parse(feed, from, from.AddDays(1)).ToList();
        Assert.Equal([7, 8, 9, 10], events.Select(e => DateTimeOffset.FromUnixTimeMilliseconds(e.Start).UtcDateTime.Day));
        Assert.All(events, e => Assert.True(e.AllDay));
    }
}
