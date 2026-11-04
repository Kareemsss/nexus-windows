using System;
using System.Linq;
using Nexus.Core.Calendar;
using Xunit;

namespace Nexus.Core.Tests;

public class CalendarTests
{
    private const string Ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" +
        "BEGIN:VEVENT\r\nSUMMARY:Infrastructure Weekly\r\nDTSTART:20261019T070000Z\r\nDTEND:20261019T073000Z\r\n" +
        "RRULE:FREQ=WEEKLY;BYDAY=MO,TH\r\nEXDATE:20261022T070000Z\r\n" +
        "DESCRIPTION:Join: https://teams.microsoft.com/l/meetup-join/abc\\, see you\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nSUMMARY:Lunch\r\nDTSTART;VALUE=DATE:20261024\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nSUMMARY:Vendor\r\n call\r\nDTSTART:20261024T080000Z\r\nDTEND:20261024T090000Z\r\nLOCATION:Zoom https://zoom.us/j/123\r\nEND:VEVENT\r\n" +
        "END:VCALENDAR\r\n";

    [Fact]
    public void Expands_weekly_rules_honours_exdate_and_skips_all_day()
    {
        var from = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        var events = IcsCalendar.Occurrences(Ics, from, from.AddDays(8));
        var weekly = events.Where(e => e.Title == "Infrastructure Weekly").Select(e => e.Start.UtcDateTime.Day).ToList();
        Assert.Equal(new[] { 19, 26 }, weekly); // Mon 19, (Thu 22 removed by EXDATE), Mon 26; Thu 29 is outside the window
        Assert.DoesNotContain(events, e => e.Title == "Lunch");
    }

    [Fact]
    public void Detects_platform_join_link_and_unfolds_lines()
    {
        var from = new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        var events = IcsCalendar.Occurrences(Ics, from, from.AddDays(1));
        var vendor = Assert.Single(events, e => e.Title.StartsWith("Vendor"));
        Assert.Equal("Vendorcall", vendor.Title);
        Assert.Equal("Zoom", vendor.Platform);
        Assert.Equal("https://zoom.us/j/123", vendor.JoinUrl);

        var weekly = IcsCalendar.Occurrences(Ics, new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero)).Single();
        Assert.Equal("Microsoft Teams", weekly.Platform);
        Assert.StartsWith("https://teams.microsoft.com/l/meetup-join/abc", weekly.JoinUrl);
    }
}
