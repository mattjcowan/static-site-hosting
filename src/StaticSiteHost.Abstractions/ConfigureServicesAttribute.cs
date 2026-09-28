using Microsoft.Extensions.DependencyInjection;

namespace StaticSiteHost.Functions;

/// <summary>
/// Marks a method that registers services for the rest of the functions: a database, a typed
/// client, a cache, anything a handler, a job or a background service then takes as a parameter
/// or resolves from <see cref="ISite.Services"/>.
/// </summary>
/// <remarks>
/// <para>
/// The method is <c>public static void</c> on a public class and takes the
/// <see cref="IServiceCollection"/> to add to. It may also take an <see cref="ISite"/>, an
/// <see cref="ISiteVariables"/>, an <see cref="IRealtime"/>, an <see cref="IAiChat"/>, the data
/// folder as a <see cref="DirectoryInfo"/>, the variables as an
/// <c>IReadOnlyDictionary&lt;string, string&gt;</c>, a non-generic <c>ILogger</c> and a
/// <see cref="CancellationToken"/>; nothing else, since the services it is adding do not exist
/// yet. A method that breaks these rules fails the build, with a message saying what to change.
/// </para>
/// <para>
/// It runs once, when the functions load, before any request reaches them. Several are allowed,
/// in any files, and run in order of class name, then method name. The collection already holds
/// what the server provides: <c>ILoggerFactory</c>, <c>ILogger&lt;T&gt;</c>, a non-generic
/// <c>ILogger</c>, <c>IDataProtectionProvider</c>, <c>IHttpClientFactory</c>,
/// <see cref="TimeProvider"/>, <see cref="ISite"/>, <see cref="ISiteVariables"/>,
/// <see cref="IRealtime"/> and <see cref="IAiChat"/>. Once they have all run the services are
/// built and checked, so a service that cannot be constructed is reported then rather than on
/// some later request.
/// </para>
/// <para>
/// A method that throws, or services that fail the check, stop the functions loading. The
/// Functions card shows why, and a site whose functions include middleware answers
/// <c>503</c> until they are fixed, rather than letting requests past a gate that is not there.
/// </para>
/// <para>
/// The services belong to one build of the functions. Deploying again builds new ones and
/// disposes the old, so nothing registered here outlives the code that registered it.
/// </para>
/// <para>
/// The server finds the attribute by its name, so an attribute of your own called
/// <c>ConfigureServicesAttribute</c> works the same.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [ConfigureServices]
/// public static void Configure(IServiceCollection services, ISite site)
/// {
///     services.AddSingleton(new Visits(Path.Combine(site.Data.FullName, "visits.db")));
///     services.AddHttpClient("github", client =&gt; client.BaseAddress = new Uri("https://api.github.com/"));
/// }
///
/// [HttpGet("/visits")]
/// public static IResult Count(Visits visits) =&gt; Results.Text(visits.Next().ToString());
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ConfigureServicesAttribute : Attribute;
