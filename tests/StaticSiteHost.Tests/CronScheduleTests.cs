using System.Globalization;
using StaticSiteHost.Services;

namespace StaticSiteHost.Tests;

public class CronScheduleTests
{
    private static DateTimeOffset At(string utc) =>
        DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static DateTimeOffset? Next(string cron, string after)
    {
        Assert.True(CronSchedule.TryParse(cron, out var schedule, out var error), error);
        return schedule.NextOccurrence(At(after));
    }

    [Theory]
    // Steps, and strictly after: an occurrence exactly at "after" has already happened.
    [InlineData("*/15 * * * *", "2025-06-01 10:07:00", "2025-06-01 10:15:00")]
    [InlineData("*/15 * * * *", "2025-06-01 10:15:00", "2025-06-01 10:30:00")]
    [InlineData("*/15 * * * *", "2025-06-01 10:14:59.9", "2025-06-01 10:15:00")]
    [InlineData("*/15 * * * *", "2025-06-01 10:50:00", "2025-06-01 11:00:00")]
    // Month and year boundaries.
    [InlineData("0 0 1 * *", "2025-01-31 12:00:00", "2025-02-01 00:00:00")]
    [InlineData("0 0 1 1 *", "2025-06-01 00:00:00", "2026-01-01 00:00:00")]
    [InlineData("30 23 31 12 *", "2025-12-31 23:30:00", "2026-12-31 23:30:00")]
    [InlineData("0 12 31 * *", "2025-04-01 00:00:00", "2025-05-31 12:00:00")]
    [InlineData("0 0 29 2 *", "2025-03-01 00:00:00", "2028-02-29 00:00:00")]
    // Ranges, lists and stepped ranges. 2025-06-06 is a Friday.
    [InlineData("0 9-17 * * 1-5", "2025-06-06 17:30:00", "2025-06-09 09:00:00")]
    [InlineData("0,30 * * * *", "2025-06-01 10:00:00", "2025-06-01 10:30:00")]
    [InlineData("10-30/10 * * * *", "2025-06-01 10:00:00", "2025-06-01 10:10:00")]
    [InlineData("10-30/10 * * * *", "2025-06-01 10:30:00", "2025-06-01 11:10:00")]
    public void Finds_the_next_occurrence(string cron, string after, string expected) =>
        Assert.Equal(At(expected), Next(cron, after));

    [Fact]
    public void Sunday_is_both_0_and_7()
    {
        // 2025-06-02 is a Monday.
        Assert.Equal(At("2025-06-08 00:00:00"), Next("0 0 * * 0", "2025-06-02 00:00:00"));
        Assert.Equal(At("2025-06-08 00:00:00"), Next("0 0 * * 7", "2025-06-02 00:00:00"));
    }

    [Fact]
    public void Two_restricted_day_fields_match_either()
    {
        // The 13th of the month or any Friday. 2025-07-11 is a Friday; 2025-07-13 a Sunday.
        Assert.Equal(At("2025-07-11 00:00:00"), Next("0 0 13 * 5", "2025-07-10 00:00:00"));
        Assert.Equal(At("2025-07-13 00:00:00"), Next("0 0 13 * 5", "2025-07-11 00:00:00"));
    }

    [Fact]
    public void A_day_field_starting_with_a_star_is_unrestricted_as_in_vixie_cron()
    {
        // */2 counts as unrestricted, so both fields must match: a Monday on an odd day. 2025-06-02
        // is a Monday on an even day, 2025-06-09 one on an odd day.
        Assert.Equal(At("2025-06-09 00:00:00"), Next("0 0 */2 * 1", "2025-06-01 00:00:00"));
    }

    [Fact]
    public void Writes_itself_back_with_single_spaces()
    {
        Assert.True(CronSchedule.TryParse("  */5   *  * * * ", out var schedule, out _));
        Assert.Equal("*/5 * * * *", schedule.ToString());
    }

    [Theory]
    [InlineData("", "required")]
    [InlineData("mon", "five fields")]
    [InlineData("0 0 * * * *", "seconds")]
    [InlineData("@daily", "Shorthands")]
    [InlineData("60 * * * *", "minutes run from 0 to 59")]
    [InlineData("0 24 * * *", "hours run from 0 to 23")]
    [InlineData("0 0 0 * *", "days of the month run from 1 to 31")]
    [InlineData("0 0 * * 8", "0 and 7 for Sunday")]
    [InlineData("* * * * MON", "1 for Monday")]
    [InlineData("* * * JAN *", "1 for January")]
    [InlineData("* * L * *", "extension")]
    [InlineData("5-2 * * * *", "runs backwards")]
    [InlineData("*/0 * * * *", "step")]
    [InlineData("5/10 * * * *", "step after a single number")]
    [InlineData("1,,2 * * * *", "empty entry")]
    [InlineData("0 0 31 2 *", "never runs")]
    public void Refuses_what_it_cannot_run_and_says_why(string cron, string expected)
    {
        Assert.False(CronSchedule.TryParse(cron, out _, out var error));
        Assert.Contains(expected, error);
    }
}
