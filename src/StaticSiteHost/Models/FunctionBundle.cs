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
    /// Where each route's handler was written, keyed by the same text as <see cref="Routes"/>.
    /// A route is missing when the build carried no debug information for it.
    /// </summary>
    public Dictionary<string, FunctionRouteSource> RouteSources { get; set; } = [];

    public int Warnings { get; set; }
}

/// <summary>The uploaded file a handler is in, and the line its body starts on.</summary>
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
