namespace StaticSiteHost.Functions;

/// <summary>
/// Marks a method that runs at a fixed interval: <c>[Every("30s")]</c>, <c>[Every("5m")]</c>,
/// <c>[Every("2h")]</c>, <c>[Every("1d")]</c>, or a <see cref="TimeSpan"/> such as
/// <c>[Every("01:30:00")]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The interval is a whole number with <c>s</c>, <c>m</c>, <c>h</c> or <c>d</c> after it, or a
/// <see cref="TimeSpan"/> written with colons; from 10 seconds to 365 days. The first run is one
/// interval after the functions load (straight away with <see cref="RunOnStart"/>), and each run
/// after that one interval after the one before it started. Runs never overlap: when the next
/// run comes due while the last one is still going, it is skipped and a warning is logged. Runs
/// that fall due while the server is down, or while the functions are being replaced, are not
/// made up afterwards.
/// </para>
/// <para>
/// The method is <c>public static</c> on a public class and returns <see cref="Task"/>,
/// <see cref="ValueTask"/> or nothing. It may take a <see cref="CancellationToken"/>, which is
/// cancelled when the functions are replaced, removed or the server stops; an
/// <see cref="ISite"/>, an <see cref="ISiteVariables"/>, an <see cref="IRealtime"/>, an
/// <see cref="IAiChat"/>, the data folder as a <see cref="DirectoryInfo"/>, the variables as an
/// <c>IReadOnlyDictionary&lt;string, string&gt;</c>, a non-generic <c>ILogger</c>; an
/// <see cref="IServiceProvider"/>, which is a scope made for the run and disposed when it ends; and
/// anything registered in a <see cref="ConfigureServicesAttribute">[ConfigureServices]</see>
/// method, resolved from that scope. There is no request, so nothing from one can be taken. A
/// method that breaks these rules fails the build, with a message saying what to change.
/// </para>
/// <para>
/// An exception is logged, with whose functions and which method, and counted on the Functions
/// card; the next run goes ahead as usual.
/// </para>
/// <para>
/// The server finds the attribute by its name, so an attribute of your own called
/// <c>EveryAttribute</c>, taking the interval as its one constructor argument and with a
/// <c>bool RunOnStart</c> property, works the same.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Every("10m", RunOnStart = true)]
/// public static void Tidy(DirectoryInfo data)
/// {
///     foreach (var file in data.EnumerateFiles("*.tmp"))
///         if (file.LastWriteTimeUtc &lt; DateTime.UtcNow.AddDays(-1)) file.Delete();
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EveryAttribute : Attribute
{
    /// <param name="interval">How often to run: <c>30s</c>, <c>5m</c>, <c>2h</c>, <c>1d</c> or a <see cref="TimeSpan"/>; see the class remarks.</param>
    public EveryAttribute(string interval) => Interval = interval;

    /// <summary>How often to run, as written.</summary>
    public string Interval { get; }

    /// <summary>
    /// Also runs the method as soon as the functions load, whether at startup or after a deploy,
    /// rather than one interval later. Defaults to false.
    /// </summary>
    public bool RunOnStart { get; init; }
}
