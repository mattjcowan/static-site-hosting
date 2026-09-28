namespace StaticSiteHost.Functions;

/// <summary>
/// Marks a method that runs on a cron schedule, in UTC: <c>[Schedule("*/5 * * * *")]</c> runs it
/// every five minutes, <c>[Schedule("0 3 * * *")]</c> at three every morning.
/// </summary>
/// <remarks>
/// <para>
/// A schedule has five fields separated by spaces: minute (0-59), hour (0-23), day of month
/// (1-31), month (1-12) and day of week (0-7, where both 0 and 7 are Sunday). Each field is
/// <c>*</c> for every value, a number, a range <c>a-b</c>, a list <c>a,b,c</c>, or a step
/// <c>*/n</c> or <c>a-b/n</c>. Times are UTC. Names such as <c>MON</c> or <c>JAN</c>, seconds, and
/// the extensions some schedulers add (<c>L</c>, <c>W</c>, <c>#</c>, <c>@daily</c>) are not
/// understood, and fail the build with a message saying what to write instead.
/// </para>
/// <para>
/// When both the day of month and the day of week are restricted, which is to say neither
/// starts with <c>*</c>, a day that matches either one counts, as in Vixie cron:
/// <c>0 9 13 * 5</c> runs at nine on the 13th of every month and on every Friday.
/// </para>
/// <para>
/// Runs never overlap. When the next run comes due while the last one is still going, it is
/// skipped and a warning is logged. Runs that fall due while the server is down, or while the
/// functions are being replaced, are not made up afterwards. See <see cref="EveryAttribute"/>
/// for the method's rules and what it may take, which are the same.
/// </para>
/// <para>
/// The server finds the attribute by its name, so an attribute of your own called
/// <c>ScheduleAttribute</c>, taking the schedule as its one constructor argument and with a
/// <c>bool RunOnStart</c> property, works the same.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Schedule("0 * * * *")]
/// public static async Task Hourly(ISite site, IHttpClientFactory http, CancellationToken stoppingToken)
/// {
///     var feed = await http.CreateClient().GetStringAsync("https://example.com/feed.xml", stoppingToken);
///     await File.WriteAllTextAsync(Path.Combine(site.Data.FullName, "feed.xml"), feed, stoppingToken);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ScheduleAttribute : Attribute
{
    /// <param name="cron">The five-field schedule, in UTC; see the class remarks.</param>
    public ScheduleAttribute(string cron) => Cron = cron;

    /// <summary>The five-field schedule, in UTC.</summary>
    public string Cron { get; }

    /// <summary>
    /// Also runs the method as soon as the functions load, whether at startup or after a deploy,
    /// and then on the schedule. Defaults to false.
    /// </summary>
    public bool RunOnStart { get; init; }
}
