using System.Text.Json.Serialization;

namespace StaticSiteHost.Models;

/// <summary>
/// One compiled set of function files: the sources exactly as uploaded and the published build
/// next to them, under functions/&lt;id&gt;/. Bundles are immutable once built; a new upload is a
/// new bundle, and a release names the bundle it runs in <see cref="ReleaseRecord.Functions"/>.
/// </summary>
public sealed class FunctionBundle
{
    public string Id { get; set; } = "";

    /// <summary>The uploaded files' names, which are also what their sources are saved as.</summary>
    public List<string> Files { get; set; } = [];

    /// <summary>How the bundle reads in a table or a message: "Orders.cs" or "Orders.cs + 2 more".</summary>
    [JsonIgnore]
    public string Label => Files.Count switch
    {
        0 => "(no files)",
        1 => Files[0],
        _ => $"{Files[0]} + {Files.Count - 1} more",
    };

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? UploadedBy { get; set; }

    /// <summary>"web" or "api".</summary>
    public string Source { get; set; } = "web";

    /// <summary>What the build discovered, e.g. "POST /ping/{name?}", for display.</summary>
    public List<string> Routes { get; set; } = [];

    /// <summary>
    /// The [Middleware] methods, in the order they run, e.g. "10 Gate.RequireToken": the order,
    /// then the class and method. For display.
    /// </summary>
    public List<string> Middleware { get; set; } = [];

    /// <summary>The [ConfigureServices] methods, in the order they run, e.g. "Setup.Configure". For display.</summary>
    public List<string> Services { get; set; } = [];

    /// <summary>The [BackgroundService] methods, e.g. "Worker.Run". For display.</summary>
    public List<string> BackgroundServices { get; set; } = [];

    /// <summary>
    /// The [Schedule] and [Every] methods with when they run, e.g. "*/5 * * * * Reports.Send" or
    /// "every 5m Cache.Refresh on start": the schedule, then the class and method, then "on start"
    /// when it also runs as the functions load. For display, and the key the Functions card finds
    /// a job's runs by.
    /// </summary>
    public List<string> Jobs { get; set; } = [];

    /// <summary>
    /// The [RealtimeConnect], [RealtimeJoin] and [AiAccess] methods, e.g. "RealtimeConnect Realtime.Who":
    /// the hook, then the class and method. For display, and what decides whether the site's hub
    /// and AI ask the functions at all (see <c>FunctionAccessHooks.Declares</c>), so the Functions
    /// card and the gate never disagree.
    /// </summary>
    public List<string> Hooks { get; set; } = [];

    /// <summary>
    /// True when the functions have work of their own to do, background services or jobs, so the
    /// server loads them at startup and straight after anything changes them rather than on the
    /// first request. Worked out from the lists above; written to site.json and the API for
    /// reading only.
    /// </summary>
    public bool NeedsEagerLoad => BackgroundServices.Count > 0 || Jobs.Count > 0;

    /// <summary>
    /// Where each handler, middleware, [ConfigureServices], [BackgroundService], job and hook method
    /// was written, keyed by the same text as its entry in <see cref="Routes"/>, <see cref="Middleware"/>,
    /// <see cref="Services"/>, <see cref="BackgroundServices"/>, <see cref="Jobs"/> or <see cref="Hooks"/>.
    /// One is missing when the build carried no debug information for it.
    /// </summary>
    public Dictionary<string, FunctionRouteSource> RouteSources { get; set; } = [];

    /// <summary>Compiler warnings only; the host's "info" notes are not counted.</summary>
    public int Warnings { get; set; }

    /// <summary>
    /// The version of StaticSiteHost.Abstractions the bundle was compiled against, which is the
    /// version of the server that built it, whatever the files asked for. Null when no file uses
    /// the package.
    /// </summary>
    public string? AbstractionsVersion { get; set; }
}

/// <summary>The uploaded file a handler, middleware, job or hook method is in, and the line it is declared on.</summary>
public sealed class FunctionRouteSource
{
    public string File { get; set; } = "";
    public int Line { get; set; }
}

/// <summary>
/// Persisted as config/functions.json: the functions every site answers with. Keeps the
/// previous bundle on disk too, so requests still running on it when a new one goes live
/// can finish loading what they need.
/// </summary>
public sealed class GlobalFunctionsRecord
{
    public FunctionBundle? Current { get; set; }
    public string? PreviousId { get; set; }
}
