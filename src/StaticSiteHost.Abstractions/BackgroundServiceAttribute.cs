namespace StaticSiteHost.Functions;

/// <summary>
/// Marks a method that runs in the background for as long as the functions are live: a queue
/// consumer, a file watcher, a loop that polls something every few seconds.
/// </summary>
/// <remarks>
/// <para>
/// The method is <c>public static</c> on a public class, returns <see cref="Task"/> or
/// <see cref="ValueTask"/>, and takes a <see cref="CancellationToken"/>, which is cancelled when
/// the functions are replaced by a deploy, removed, or the server stops. Return promptly once it
/// is: the server waits 15 seconds, then logs a warning and carries on without it. It may also
/// take an <see cref="ISite"/>, an <see cref="ISiteVariables"/>, an <see cref="IRealtime"/>, an
/// <see cref="IAiChat"/>, the data folder as a <see cref="DirectoryInfo"/>, the variables as an
/// <c>IReadOnlyDictionary&lt;string, string&gt;</c>, a non-generic <c>ILogger</c>, the functions'
/// <see cref="IServiceProvider"/> and anything registered in a
/// <see cref="ConfigureServicesAttribute">[ConfigureServices]</see> method. There is no request,
/// so nothing from one can be taken. A method that breaks these rules fails the build, with a
/// message saying what to change.
/// </para>
/// <para>
/// It starts as soon as the functions load, which for functions with background work is when
/// the server starts and right after every deploy, not on the first request. If it throws, it is
/// logged and started again after a pause that begins at a second and doubles up to a minute;
/// the pause starts over once a run has lasted longer than that. If it returns, it is finished
/// and is not started again until the functions next load.
/// </para>
/// <para>
/// The <see cref="IServiceProvider"/> it takes is the root one, which lives as long as the
/// functions do. Create a scope from it for each unit of work that needs scoped services.
/// </para>
/// <para>
/// The server finds the attribute by its name, so an attribute of your own called
/// <c>BackgroundServiceAttribute</c> works the same.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [BackgroundService]
/// public static async Task Heartbeat(CancellationToken stoppingToken, ISite site, ILogger log)
/// {
///     var file = Path.Combine(site.Data.FullName, "heartbeat.txt");
///     while (!stoppingToken.IsCancellationRequested)
///     {
///         await File.WriteAllTextAsync(file, DateTimeOffset.UtcNow.ToString("O"), stoppingToken);
///         await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
///     }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class BackgroundServiceAttribute : Attribute;
