using System.Reflection;
using StaticSiteHost.Services;

namespace StaticSiteHost.Tests;

public class JobIntervalTests
{
    [Theory]
    [InlineData("30s", 30)]
    [InlineData("5m", 5 * 60)]
    [InlineData("90M", 90 * 60)]
    [InlineData("2h", 2 * 3600)]
    [InlineData("1d", 86400)]
    [InlineData("10s", 10)]
    [InlineData("365d", 365 * 86400)]
    [InlineData("01:30:00", 90 * 60)]
    [InlineData("1.00:00:00", 86400)]
    public void Reads_a_number_and_a_unit_or_a_TimeSpan(string text, int seconds)
    {
        Assert.True(JobInterval.TryParse(text, out var interval, out var error), error);
        Assert.Equal(TimeSpan.FromSeconds(seconds), interval);
    }

    [Theory]
    [InlineData("", "required")]
    [InlineData("30", "no unit")]
    [InlineData("1.5h", "not an interval")]
    [InlineData("1h30m", "not an interval")]
    [InlineData("soon", "not an interval")]
    [InlineData("9s", "at least 10s")]
    [InlineData("1s", "[BackgroundService]")]
    [InlineData("00:00:05", "at least 10s")]
    [InlineData("366d", "at most 365d")]
    [InlineData("999999999d", "at most 365d")]
    public void Refuses_what_it_cannot_run_and_says_why(string text, string expected)
    {
        Assert.False(JobInterval.TryParse(text, out _, out var error));
        Assert.Contains(expected, error);
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(90 * 60, "90m")]
    [InlineData(2 * 3600, "2h")]
    [InlineData(86400, "1d")]
    public void Formats_in_the_largest_whole_unit(int seconds, string expected) =>
        Assert.Equal(expected, JobInterval.Format(TimeSpan.FromSeconds(seconds)));

    private static readonly MethodInfo AnyMethod = typeof(JobIntervalTests).GetMethod(nameof(Formats_in_the_largest_whole_unit))!;

    private static readonly DateTimeOffset Ten = new(2025, 6, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_interval_counts_from_when_the_last_run_was_due()
    {
        var job = new FunctionJobs.Job(AnyMethod, null, TimeSpan.FromMinutes(5), RunOnStart: false);

        Assert.Equal(Ten.AddMinutes(5), job.First(Ten));
        Assert.Equal(Ten.AddMinutes(5), job.Next(Ten, Ten.AddSeconds(3)));
    }

    [Fact]
    public void Missed_runs_are_passed_over_rather_than_made_up()
    {
        var job = new FunctionJobs.Job(AnyMethod, null, TimeSpan.FromMinutes(5), RunOnStart: true);

        Assert.Equal(Ten, job.First(Ten));
        Assert.Equal(Ten.AddMinutes(15), job.Next(Ten, Ten.AddMinutes(12)));
    }
}
