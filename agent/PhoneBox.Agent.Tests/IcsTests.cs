using PhoneBox.Agent;
using Xunit;

public class IcsTests
{
    private static readonly DateTime From = new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);   // Monday
    private static readonly DateTime To = From.AddDays(14);

    private static string Cal(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + string.Concat(events.Select(e => "BEGIN:VEVENT\r\n" + e.Trim().Replace("\n", "\r\n") + "\r\nEND:VEVENT\r\n")) + "END:VCALENDAR\r\n";

    private static DateTime Utc(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    [Fact]
    public void SingleEventInUtc()
    {
        var e = Ics.Parse(Cal("UID:1\nDTSTART:20261110T090000Z\nDTEND:20261110T093000Z\nSUMMARY:Stand-up\\, team\nLOCATION:Room 1"), From, To).Single();
        Assert.Equal("Stand-up, team", e.Title);
        Assert.Equal("Room 1", e.Location);
        Assert.Equal(new DateTime(2026, 11, 10, 9, 0, 0), Utc(e.Start));
        Assert.Equal(TimeSpan.FromMinutes(30), Utc(e.End) - Utc(e.Start));
        Assert.False(e.AllDay);
    }

    [Fact]
    public void EscapedBackslashIsNotReadAsALineBreak()
    {
        // RFC 5545 TEXT: "\\" is one backslash, so "\\new" is a backslash and "new", not a line break and "ew"
        var e = Ics.Parse(Cal("UID:1\nDTSTART:20261110T090000Z\n" + @"SUMMARY:Fix C:\\new\\build\, then\ntest" + "\n" + @"LOCATION:\\\\Nas\\Meeting"), From, To).Single();
        Assert.Equal(@"Fix C:\new\build, then test", e.Title);
        Assert.Equal(@"\\Nas\Meeting", e.Location);
    }

    [Fact]
    public void FoldedLinesAreJoined()
    {
        var e = Ics.Parse(Cal("UID:1\nDTSTART:20261110T090000Z\nSUMMARY:A very long\n  title"), From, To).Single();
        Assert.Equal("A very long title", e.Title);
    }

    [Fact]
    public void AlarmRepeatDurationDoesNotShortenItsAllDayEvent()
    {
        var e = Assert.Single(Ics.Parse(Cal("""
            UID:all-day
            DTSTART;VALUE=DATE:20261110
            SUMMARY:Day off
            BEGIN:VALARM
            ACTION:DISPLAY
            TRIGGER:-PT15M
            REPEAT:2
            DURATION:PT15M
            DESCRIPTION:Reminder
            END:VALARM
            """), From, To));
        Assert.True(e.AllDay);
        Assert.Equal(TimeSpan.FromDays(1), Utc(e.End) - Utc(e.Start));
    }

    [Theory]
    [InlineData("PT2147483648H")]
    [InlineData("P1000000000W")]
    [InlineData("P4000000D")]
    [InlineData("PT\u0661H")]
    public void UnrepresentableDurationDoesNotDiscardTheCalendar(string duration)
    {
        var events = Ics.Parse(Cal(
            $"UID:bad\nDTSTART;VALUE=DATE:20261110\nDURATION:{duration}\nSUMMARY:Day off",
            "UID:next\nDTSTART:20261111T090000Z\nDURATION:PT45M\nSUMMARY:Meeting"), From, To).ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal(TimeSpan.FromDays(1), Utc(events[0].End) - Utc(events[0].Start));
        Assert.Equal(TimeSpan.FromMinutes(45), Utc(events[1].End) - Utc(events[1].Start));
    }

    [Fact]
    public void AllDayEvent()
    {
        var e = Ics.Parse(Cal("UID:1\nDTSTART;VALUE=DATE:20261112\nDTEND;VALUE=DATE:20261113\nSUMMARY:Holiday"), From, To).Single();
        Assert.True(e.AllDay);
        Assert.Equal(TimeSpan.FromDays(1), Utc(e.End) - Utc(e.Start));
    }

    [Fact]
    public void TimeZoneIsApplied()
    {
        // 10:00 in Tallinn in November (UTC+2) is 08:00 UTC
        var e = Ics.Parse(Cal("UID:1\nDTSTART;TZID=Europe/Tallinn:20261110T100000\nDTEND;TZID=Europe/Tallinn:20261110T110000\nSUMMARY:Call"), From, To).Single();
        Assert.Equal(new DateTime(2026, 11, 10, 8, 0, 0), Utc(e.Start));
    }

    [Fact]
    public void WeeklyByDayWithExceptionAndMovedOccurrence()
    {
        var events = Ics.Parse(Cal(
            "UID:w\nDTSTART:20261001T070000Z\nDTEND:20261001T073000Z\nRRULE:FREQ=WEEKLY;BYDAY=MO,TH\nEXDATE:20261112T070000Z\nSUMMARY:Sync",
            "UID:w\nRECURRENCE-ID:20261116T070000Z\nDTSTART:20261116T120000Z\nDTEND:20261116T123000Z\nSUMMARY:Sync (moved)"), From, To).ToList();
        var starts = events.Select(e => (Utc(e.Start), e.Title)).OrderBy(x => x.Item1).ToList();
        Assert.Equal(
        [
            (new DateTime(2026, 11, 9, 7, 0, 0), "Sync"),
            // Thursday the 12th is excluded
            (new DateTime(2026, 11, 16, 12, 0, 0), "Sync (moved)"),
            (new DateTime(2026, 11, 19, 7, 0, 0), "Sync"),
        ], starts);
    }

    [Fact]
    public void DailyStandUpFromYearsAgoStillShows()
    {
        var events = Ics.Parse(Cal("UID:d\nDTSTART:20150105T080000Z\nDTEND:20150105T081500Z\nRRULE:FREQ=DAILY\nSUMMARY:Daily"), From, To).ToList();
        Assert.Equal(14, events.Count);
        Assert.Equal(new DateTime(2026, 11, 9, 8, 0, 0), Utc(events[0].Start));
    }

    [Fact]
    public void CountAndUntilEndTheSeries()
    {
        var counted = Ics.Parse(Cal("UID:c\nDTSTART:20261109T100000Z\nRRULE:FREQ=DAILY;COUNT=3\nSUMMARY:Three"), From, To);
        Assert.Equal(3, counted.Count());
        var until = Ics.Parse(Cal("UID:u\nDTSTART:20261109T100000Z\nRRULE:FREQ=DAILY;UNTIL=20261111T235959Z\nSUMMARY:Until"), From, To);
        Assert.Equal(3, until.Count());
    }

    [Theory]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE;COUNT=3", false)]
    [InlineData("freq=weekly;byday=mo,we;count=3", false)]
    [InlineData("FrEq=Weekly;ByDaY=Mo,wE;COUNT=3", true)]
    public void RecurrenceTokensAreCaseInsensitive(string rule, bool mixedEnvelope)
    {
        var calendar = Cal($"UID:case\nDTSTART:20261109T090000Z\nRRULE:{rule}\nSUMMARY:Keep My Case");
        if (mixedEnvelope)
            calendar = calendar.Replace("BEGIN:VEVENT", "begin:vEvent").Replace("END:VEVENT", "End:vevent");
        var result = Ics.Parse(calendar, From, To).ToArray();
        Assert.All(result, item => Assert.Equal("Keep My Case", item.Title));
        var actual = string.Join(",", result.Select(e => Utc(e.Start).Day));
        Assert.True(actual == "9,11,16", $"EXPECTED: 9,11,16 | ACTUAL: {actual}");
    }

    [Theory]
    [InlineData("FREQ=DAILY;COUNT=1")]
    [InlineData("FREQ=DAILY;COUNT=1;COUNT=2")]
    [InlineData("FREQ=WEEKLY;freq=DAILY")]
    public void AnInvalidRecurrenceRuleDoesNotDiscardOtherEvents(string rule)
    {
        string[] expected = ["First", "Second"];
        var error = Record.Exception(() =>
        {
            var events = Ics.Parse(Cal($"UID:one\nDTSTART:20261109T090000Z\nRRULE:{rule}\nSUMMARY:First",
                "UID:two\nDTSTART:20261110T100000Z\nSUMMARY:Second"), From, To);
            Assert.Equal(expected, events.Select(e => e.Title).ToArray());
        });
        Assert.True(error is null, $"EXPECTED: both first occurrences | ACTUAL: {error?.GetType().Name} {error?.Message}");
    }

    [Theory]
    [InlineData("20261101", "", "11-01,11-02,11-03")]
    [InlineData("20261101", ";BYMONTHDAY=1", "11-01,12-01,01-01")]
    [InlineData("20261130", ";BYMONTHDAY=-1", "11-30,12-31,01-31")]
    public void DailyRulesLimitCandidatesToTheirMonthDays(string start, string filter, string expected)
    {
        var from = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var events = Ics.Parse(Cal($"UID:m\nDTSTART;VALUE=DATE:{start}\nRRULE:FREQ=DAILY;COUNT=3{filter}"), from, from.AddMonths(4));
        var actual = string.Join(",", events.Select(e => Utc(e.Start).ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.True(actual == expected, $"EXPECTED: {expected} | ACTUAL: {actual}");
    }

    [Fact]
    public void DailyWithByDayKeepsOnlyThoseWeekdays()
    {
        // Thunderbird writes "every weekday" as FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR (RFC 5545: BYDAY limits a DAILY rule)
        var weekdays = Ics.Parse(Cal("UID:d\nDTSTART:20261102T080000Z\nRRULE:FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR\nSUMMARY:Stand-up"), From, To)
            .Select(e => Utc(e.Start)).ToList();
        Assert.Equal(10, weekdays.Count);
        Assert.DoesNotContain(weekdays, d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.Equal(new DateTime(2026, 11, 9, 8, 0, 0), weekdays[0]);

        // COUNT counts only the matching days
        var counted = Ics.Parse(Cal("UID:c\nDTSTART:20261109T080000Z\nRRULE:FREQ=DAILY;BYDAY=MO,WE,FR;COUNT=4\nSUMMARY:Gym"), From, To)
            .Select(e => Utc(e.Start)).ToList();
        Assert.Equal(
            [new DateTime(2026, 11, 9, 8, 0, 0), new DateTime(2026, 11, 11, 8, 0, 0), new DateTime(2026, 11, 13, 8, 0, 0), new DateTime(2026, 11, 16, 8, 0, 0)],
            counted);

        // INTERVAL still steps in days before the weekday filter: every 2nd day from Mon 9 Nov, weekdays only
        var everyOther = Ics.Parse(Cal("UID:i\nDTSTART:20261109T080000Z\nRRULE:FREQ=DAILY;INTERVAL=2;BYDAY=MO,TU,WE,TH,FR\nSUMMARY:Alt"), From, To)
            .Select(e => Utc(e.Start).Day).ToList();
        Assert.Equal([9, 11, 13, 17, 19], everyOther);
    }

    [Theory]
    [InlineData("20240311T090000Z", "MO", "8,9,10,11,12,13,14", "03-11,04-08,05-13")]
    [InlineData("20240331T090000Z", "SU", "-7,-6,-5,-4,-3,-2,-1", "03-31,04-28,05-26")]
    public void MonthlyRulesCombineWeekdayAndMonthDay(string first, string byDay, string byMonthDay, string dates)
    {
        var start = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var events = Ics.Parse(Cal($"UID:m\nDTSTART:{first}\nRRULE:FREQ=MONTHLY;BYDAY={byDay};BYMONTHDAY={byMonthDay};COUNT=3"),
            start, start.AddMonths(4));
        Assert.Equal(dates, string.Join(",", events.Select(e => Utc(e.Start).ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Theory]
    [InlineData("20260101", "BYMONTHDAY=1,-31", "01-01,02-01,03-01")]
    [InlineData("20260105", "BYDAY=1MO,MO", "01-05,01-12,01-19")]
    public void OverlappingMonthlyFiltersCountDistinctOccurrences(string first, string filters, string dates)
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var events = Ics.Parse(Cal($"UID:overlap\nDTSTART;VALUE=DATE:{first}\nRRULE:FREQ=MONTHLY;COUNT=3;{filters}"),
            from, from.AddMonths(4));

        Assert.Equal(dates, string.Join(",", events.Select(e => Utc(e.Start).ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Theory]
    [InlineData("20250519", "BYDAY=20MO", "2025-05-19,2026-05-18,2027-05-17")]
    [InlineData("20251229", "BYDAY=-1MO", "2025-12-29,2026-12-28,2027-12-27")]
    [InlineData("20260131", "BYMONTHDAY=31", "2026-01-31,2026-03-31,2026-05-31")]
    public void YearlyDayFiltersWithoutMonthApplyToTheEntireYear(string first, string filters, string dates)
    {
        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var events = Ics.Parse(Cal($"UID:year\nDTSTART;VALUE=DATE:{first}\nRRULE:FREQ=YEARLY;COUNT=3;{filters}"),
            from, from.AddYears(4));

        Assert.Equal(dates, string.Join(",", events.Select(e => Utc(e.Start).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void MonthlySecondTuesday()
    {
        var e = Ics.Parse(Cal("UID:m\nDTSTART:20260113T150000Z\nRRULE:FREQ=MONTHLY;BYDAY=2TU\nSUMMARY:Review"), From, To).Single();
        Assert.Equal(new DateTime(2026, 11, 10, 15, 0, 0), Utc(e.Start));
    }

    [Theory]
    [InlineData("DAILY", "20261130", "2026-11-30,2027-11-01")]
    [InlineData("WEEKLY", "20261130", "2026-11-30,2027-11-01")]
    [InlineData("MONTHLY", "20261101", "2026-11-01,2027-11-01")]
    public void MonthFiltersLimitNonYearlyRulesBeforeCount(string frequency, string start, string expected)
    {
        var from = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var events = Ics.Parse(Cal($"UID:x\nDTSTART;VALUE=DATE:{start}\nRRULE:FREQ={frequency};BYMONTH=11;COUNT=2"),
            from, from.AddYears(2));
        Assert.Equal(expected, string.Join(",", events.Select(e => Utc(e.Start).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void YearlyByMonthAndByDayPicksTheRightDay()
    {
        // Outlook / Google write "yearly on the last Thursday of November" as FREQ=YEARLY;BYMONTH=11;BYDAY=-1TH
        var nov = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var thanksgiving = Ics.Parse(Cal("UID:t\nDTSTART:20251127T120000Z\nRRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=-1TH\nSUMMARY:Dinner"), nov, nov.AddDays(30)).Single();
        Assert.Equal(new DateTime(2026, 11, 26, 12, 0, 0), Utc(thanksgiving.Start));

        // second Sunday of May, across the year boundary of the series
        var may = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var mothersDay = Ics.Parse(Cal("UID:m\nDTSTART:20250511T090000Z\nRRULE:FREQ=YEARLY;BYDAY=2SU;BYMONTH=5\nSUMMARY:Mother's Day"), may, may.AddDays(30)).Single();
        Assert.Equal(new DateTime(2026, 5, 10, 9, 0, 0), Utc(mothersDay.Start));

        // several months without BYDAY keep the start's day of the month: 15 March and 15 September
        var jan = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var twice = Ics.Parse(Cal("UID:s\nDTSTART:20250315T080000Z\nRRULE:FREQ=YEARLY;BYMONTH=3,9\nSUMMARY:Service"), jan, jan.AddYears(1))
            .Select(e => Utc(e.Start)).ToList();
        Assert.Equal([new DateTime(2026, 3, 15, 8, 0, 0), new DateTime(2026, 9, 15, 8, 0, 0)], twice);
    }

    [Theory]
    // RFC 5545 3.8.5.3: "An example where the days generated makes a difference because of WKST"
    [InlineData("WKST=MO", new[] { 5, 10, 19, 24 })]
    [InlineData("WKST=SU", new[] { 5, 17, 19, 31 })] // Google Calendar writes WKST=SU
    public void BiweeklyRuleUsesItsWeekStart(string wkst, int[] augustDays)
    {
        var events = Ics.Parse(Cal($"UID:x\nDTSTART:19970805T130000Z\nRRULE:FREQ=WEEKLY;INTERVAL=2;COUNT=4;BYDAY=TU,SU;{wkst}\nSUMMARY:Biweekly"),
            new DateTime(1997, 8, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(1997, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(augustDays, events.Select(e => Utc(e.Start).Day));
    }

    [Theory]
    // RFC 5545 3.8.5.3 examples, and Outlook's "last weekday of every month"
    [InlineData("19970904T130000Z", "FREQ=MONTHLY;COUNT=3;BYDAY=TU,WE,TH;BYSETPOS=3", "09-04,10-07,11-06")]
    [InlineData("19970929T130000Z", "FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-2", "09-29,10-30,11-27,12-30")]
    [InlineData("19970930T130000Z", "FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1", "09-30,10-31,11-28,12-31")]
    public void BySetPosPicksFromEachMonthsSet(string start, string rrule, string dates)
    {
        var events = Ics.Parse(Cal($"UID:x\nDTSTART:{start}\nRRULE:{rrule}\nSUMMARY:S"),
            new DateTime(1997, 9, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(1998, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(dates, string.Join(",", events.Select(e => Utc(e.Start).ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void LeapDayBirthdayOnlyHappensInLeapYears()
    {
        // RFC 5545: invalid dates (29 February in 2026) are ignored, not moved to the 28th
        var y2026 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Empty(Ics.Parse(Cal("UID:b\nDTSTART;VALUE=DATE:20240229\nRRULE:FREQ=YEARLY\nSUMMARY:Birthday"), y2026, y2026.AddYears(1)));
        var y2028 = new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2028, 2, 29), Utc(Ics.Parse(Cal("UID:b\nDTSTART;VALUE=DATE:20240229\nRRULE:FREQ=YEARLY\nSUMMARY:Birthday"), y2028, y2028.AddYears(1)).Single().Start));
        // COUNT counts the real occurrences
        var counted = Ics.Parse(Cal("UID:c\nDTSTART:20240229T090000Z\nRRULE:FREQ=YEARLY;COUNT=3\nSUMMARY:Leap"),
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Select(e => Utc(e.Start).Year).ToList();
        Assert.Equal([2024, 2028, 2032], counted);
    }

    [Fact]
    public void CancelledEventsAreSkipped()
    {
        Assert.Empty(Ics.Parse(Cal("UID:x\nDTSTART:20261110T090000Z\nSTATUS:CANCELLED\nSUMMARY:Gone"), From, To));
    }

    [Fact]
    public void AmbiguousFallBackTimesUseTheFirstOccurrence()
    {
        var from = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var events = Ics.Parse(Cal(
            "UID:single\nDTSTART;TZID=America/New_York:20261101T013000\nSUMMARY:Single",
            "UID:weekly\nDTSTART;TZID=America/New_York:20261025T013000\nRRULE:FREQ=WEEKLY;COUNT=2\nSUMMARY:Weekly"),
            from, from.AddDays(1)).ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc), Utc(e.Start)));
    }

    [Fact]
    public void WallClockTimeSurvivesDaylightSavingChange()
    {
        // weekly at 09:00 Berlin time across the end of DST (25 Oct 2026): 07:00 UTC before, 08:00 UTC after
        var from = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
        var events = Ics.Parse(Cal("UID:b\nDTSTART;TZID=Europe/Berlin:20261019T090000\nRRULE:FREQ=WEEKLY\nSUMMARY:Weekly"), from, from.AddDays(10)).ToList();
        Assert.Equal(new DateTime(2026, 10, 19, 7, 0, 0), Utc(events[0].Start));
        Assert.Equal(new DateTime(2026, 10, 26, 8, 0, 0), Utc(events[1].Start));
    }

    [Fact]
    public void LocalTimeSeriesKeepsItsWallClockAcrossDaylightSaving()
    {
        // floating times and TZIDs the computer does not know are the computer's own time: 09:00 in winter and in summer
        // A series repeating one fixed UTC time can match at most one of the two, whatever this machine's zone is.
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var winter = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc);
        var summer = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc);
        foreach (var start in new[] { "DTSTART:20260302T090000", "DTSTART;TZID=Customized Time Zone:20260302T090000" })
        {
            var ics = Cal($"UID:f\n{start}\nRRULE:FREQ=WEEKLY\nSUMMARY:Standup");
            Assert.Equal(new DateTime(2026, 3, 9, 8, 0, 0), Utc(Ics.Parse(ics, winter, winter.AddDays(7), berlin).Single().Start)); // 09:00 CET
            Assert.Equal(new DateTime(2026, 7, 6, 7, 0, 0), Utc(Ics.Parse(ics, summer, summer.AddDays(7), berlin).Single().Start)); // 09:00 CEST
        }
        // a UTC series still repeats in UTC
        var utc = Ics.Parse(Cal("UID:u\nDTSTART:20260302T080000Z\nRRULE:FREQ=WEEKLY\nSUMMARY:Sync"), summer, summer.AddDays(7), berlin).Single();
        Assert.Equal(new DateTime(2026, 7, 6, 8, 0, 0), Utc(utc.Start));
    }

    [Fact]
    public void AHugeIntervalCannotTakeDownTheFeed()
    {
        // RFC 5545 INTERVAL is any positive int; the period math used to overflow int and the date adds used to
        // throw past DateTime.MaxValue, so one weird rule crashed Parse and dropped every event in the feed
        var events = Ics.Parse(Cal(
            "UID:1\nDTSTART:20261110T090000Z\nSUMMARY:Daily\nRRULE:FREQ=DAILY;INTERVAL=2000000000",
            "UID:2\nDTSTART:20261110T090000Z\nSUMMARY:Monthly\nRRULE:FREQ=MONTHLY;INTERVAL=2000000",
            "UID:3\nDTSTART:20261110T090000Z\nSUMMARY:Yearly\nRRULE:FREQ=YEARLY;INTERVAL=20000",
            "UID:4\nDTSTART:20261111T100000Z\nSUMMARY:Review"), From, To).ToList();
        Assert.Contains(events, e => e.Title == "Review");
        Assert.Equal(3, events.Count(e => e.Title is "Daily" or "Monthly" or "Yearly")); // each still yields its DTSTART
    }

    [Fact]
    public void ARuleThatRunsIntoTheYear9999OnlyStopsItsOwnEvent()
    {
        // within the interval bounds, but a weekday or a time of day near the end of the calendar still steps past
        // DateTime.MaxValue: that event stops repeating, the rest of the feed stays
        var events = Ics.Parse(Cal(
            "UID:1\nDTSTART:20261110T090000Z\nSUMMARY:Weekly\nRRULE:FREQ=WEEKLY;INTERVAL=416018;BYDAY=TU,SU",
            "UID:2\nDTSTART:20261110T230000Z\nDURATION:PT2H\nSUMMARY:Daily\nRRULE:FREQ=DAILY;INTERVAL=2912410",
            "UID:3\nDTSTART:20261111T100000Z\nSUMMARY:Review"), From, To).ToList();
        Assert.Contains(events, e => e.Title == "Review");
        Assert.Contains(events, e => e.Title == "Weekly");
        Assert.Contains(events, e => e.Title == "Daily");
    }
}

public class RuleWindowTests
{
    [Theory]
    [InlineData("22:00-07:00", "23:30", true)]
    [InlineData("22:00-07:00", "06:59", true)]
    [InlineData("22:00-07:00", "07:00", false)]
    [InlineData("09:00-17:00", "12:00", true)]
    [InlineData("09:00-17:00", "17:00", false)]
    [InlineData("08:30", "08:30", true)]
    [InlineData("08:30", "08:31", false)]
    [InlineData("nonsense", "08:30", false)]
    // "until midnight" written as 24:00 holds all evening, not only its first minute
    [InlineData("18:00-24:00", "23:30", true)]
    [InlineData("18:00-24:00", "20:00", true)]
    [InlineData("18:00-24:00", "17:59", false)]
    [InlineData("18:00-24:00", "00:00", false)]
    public void TimeWindows(string window, string now, bool expected) =>
        Assert.Equal(expected, RuleEngine.InWindow(window, TimeSpan.Parse(now)));
}
