using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using StaticSiteHost.Functions;
using StaticSiteHost.Services.Ai;
using StaticSiteHost.Services.Realtime;

namespace StaticSiteHost.Services;

/// <summary>
/// The host's <see cref="ISite"/>: what one set of functions sees of the site it is running for.
/// There are two kinds.
///
/// A request's: <see cref="FunctionHost"/> makes one per set that runs and puts it in
/// <c>HttpContext.Items</c>, where <c>context.Site()</c> and <see cref="FunctionRouter"/>'s
/// parameter binding find it. It is made per request, and cheaply: a thin wrapper over the
/// variables the host has already resolved and cached for the site, the request's scope of the
/// functions' services, and the site's realtime side and AI, which are small host objects made
/// once per request.
///
/// A set's own (<see cref="ForSet"/>): what code outside any request sees, from a
/// <c>[ConfigureServices]</c> method to a job, and what the functions' services hand out as
/// <see cref="ISite"/>, <see cref="IRealtime"/> and <see cref="IAiChat"/>. Its domain is the set's
/// (<c>global</c> for the global functions), and its variables and AI settings are read afresh on
/// every access, since it lives as long as the functions do and they may change meanwhile. The
/// global functions belong to no site outside a request, so theirs has no realtime side and no AI,
/// and reading either throws, saying so.
///
/// Either refers only to host and framework types. Its <see cref="Services"/> is the functions'
/// own provider, which the set owns and disposes when it retires; the host drops every reference
/// to it then, so a context that a function keeps holds nothing but what is its own.
///
/// A set's own context is retired with its functions (<see cref="Retire"/>), once they have been
/// given their time to stop and their requests their time to finish, just before the build is
/// unloaded. From then on it hands out nothing: <see cref="Data"/>, <see cref="Variables"/>,
/// <see cref="Realtime"/>, <see cref="Ai"/> and <see cref="Services"/> throw, and so do the
/// variables, realtime side and AI it gave out before, which a service may have kept. A job or a
/// background service that ignored its token would otherwise go on reading the site by its domain,
/// and after a delete that domain can belong to a new site; and it would recreate the data folder
/// the delete had just removed. A request's context follows its set's for the data folder only:
/// one still running after its set was unloaded cannot recreate the folder either. Anything handed
/// out before, such as a <see cref="DirectoryInfo"/> already bound, is the code's own to keep.
/// </summary>
public sealed class SiteContext : ISite
{
    /// <summary>What the global functions report as their domain outside a request, and what the host calls them in its log.</summary>
    public const string GlobalDomain = "global";

    private static readonly ResolvedVariables NoVariables = new(
        FrozenDictionary<string, string>.Empty, FrozenDictionary<string, string>.Empty, [], []);

    private readonly string _dataDir;
    private readonly ISiteVariables _variables;
    private readonly IRealtime? _realtime;
    private readonly IAiChat? _ai;

    /// <summary>For a request's context, the set's own, whose retirement it follows for the data folder; null for a set's own.</summary>
    private readonly SiteContext? _set;

    private DirectoryInfo? _data;
    private IServiceProvider? _services;
    private volatile bool _retired;

    /// <summary>A request's.</summary>
    /// <param name="domain">The site the request is for, which global functions report as theirs too.</param>
    /// <param name="dataDir">The data folder of the functions running: the site's, or the global one.</param>
    /// <param name="variables">The site's variables, as resolved for this request.</param>
    /// <param name="services">The functions' services, scoped to the request.</param>
    /// <param name="realtime">The realtime side of the site the request is for.</param>
    /// <param name="ai">The AI of the site the request is for.</param>
    /// <param name="set">
    /// The running set's own context. Once it is retired this one hands out no data folder either;
    /// null where no set outlives the request, as in the editor's test.
    /// </param>
    public SiteContext(
        string domain, string dataDir, ResolvedVariables variables, IServiceProvider services, IRealtime realtime, IAiChat ai,
        SiteContext? set = null)
        : this(domain, dataDir, () => variables, realtime, ai)
    {
        _services = services;
        _set = set;
    }

    private SiteContext(string domain, string dataDir, Func<ResolvedVariables> variables, IRealtime? realtime, IAiChat? ai)
    {
        Domain = domain;
        _dataDir = dataDir;
        _realtime = realtime;
        _ai = ai;
        _variables = new ResolvedSiteVariables(variables);
    }

    /// <summary>
    /// A set's own, for code outside any request. Its <see cref="Services"/> are attached with
    /// <see cref="UseServices"/> once they are built, which is after the <c>[ConfigureServices]</c>
    /// methods that may take this context have run. What it hands out checks, each time it is used,
    /// that the context has not been retired (see the class remarks).
    /// </summary>
    /// <param name="domain">
    /// The site the functions belong to, or null for the global functions, which have no variables,
    /// realtime side or AI of their own.
    /// </param>
    /// <param name="dataDir">The functions' data folder.</param>
    public static SiteContext ForSet(
        string? domain, string dataDir, SiteStore sites, SiteVariableService variables,
        SiteRealtimeFactory realtime, SiteAiChatFactory ai)
    {
        // The checks refer to the context they belong to, which does not exist until it is made.
        SiteContext? self = null;
        void Check() => self!.ThrowIfRetired();

        self = domain is null
            ? new SiteContext(GlobalDomain, dataDir, () => { Check(); return NoVariables; }, realtime: null, ai: null)
            : new SiteContext(domain, dataDir,
                () =>
                {
                    Check();
                    return sites.TryGet(domain) is { } site ? variables.Resolve(site) : NoVariables;
                },
                realtime.Create(domain, Check), ai.Create(domain, Check));

        return self;
    }

    public string Domain { get; }

    /// <summary>
    /// Created on first use, as a handler's <see cref="DirectoryInfo"/> parameter is. Refused once
    /// the functions are retired (see the class remarks), so nothing recreates a folder a delete removed.
    /// </summary>
    public DirectoryInfo Data
    {
        get
        {
            if (IsRetired) throw RetiredError();
            return _data ??= Directory.CreateDirectory(_dataDir);
        }
    }

    public ISiteVariables Variables
    {
        get
        {
            ThrowIfRetired();
            return _variables;
        }
    }

    public IServiceProvider Services
    {
        get
        {
            ThrowIfRetired();
            return _services ?? throw new InvalidOperationException(
                "The functions' services are still being set up, so ISite.Services has nothing in it yet: it can be used once " +
                "every [ConfigureServices] method has returned. To register a service that needs another, register a factory, " +
                "as in services.AddSingleton(provider => new Store(provider.GetRequiredService<ILogger<Store>>())).");
        }
    }

    public IRealtime Realtime
    {
        get
        {
            ThrowIfRetired();
            return _realtime ?? throw OutsideRequest(nameof(Realtime), "realtime side");
        }
    }

    public IAiChat Ai
    {
        get
        {
            ThrowIfRetired();
            return _ai ?? throw OutsideRequest(nameof(Ai), "AI");
        }
    }

    /// <summary>
    /// True once the functions this context belongs to have been retired: for a set's own, since
    /// <see cref="Retire"/>; for a request's, since its set's was retired.
    /// </summary>
    public bool IsRetired => _retired || _set is { _retired: true };

    /// <summary>Attaches a set's built services; see <see cref="ForSet"/>.</summary>
    internal void UseServices(IServiceProvider services) => _services = services;

    /// <summary>
    /// Retires a set's own context as its functions are unloaded, after which it hands out nothing.
    /// See the class remarks. Doing it again does nothing.
    /// </summary>
    internal void Retire() => _retired = true;

    /// <summary>Only a set's own context is ever retired itself; a request's follows its set's for the data folder alone.</summary>
    private void ThrowIfRetired()
    {
        if (_retired) throw RetiredError();
    }

    /// <summary>What anything retired throws, saying why.</summary>
    internal InvalidOperationException RetiredError() => new(
        $"The functions for {Domain} that this ISite belongs to have been retired: they were replaced by a deploy, or " +
        "their site was deleted or renamed, and their hold on the site went with them. Code that outlives its functions, " +
        "such as a background service or a job that goes on after its CancellationToken is cancelled, should stop when " +
        "the token says so.");

    /// <summary>For the global functions' own context, which belongs to no site.</summary>
    /// <param name="member">The member read, as <c>ISite</c> names it.</param>
    /// <param name="what">What it is, in words.</param>
    private static InvalidOperationException OutsideRequest(string member, string what) => new(
        $"The global functions answer for every site, so outside a request they have no site, and ISite.{member} " +
        $"has nothing to reach. Use it inside a request, where it is the {what} of the site the request is for; a job " +
        "or a background service that needs one belongs in that site's own functions.");

    /// <summary>
    /// <see cref="ISiteVariables"/> over the resolved dictionaries, which are frozen, so sharing
    /// them is safe. Each call asks <paramref name="resolved"/> for them, which for a request is
    /// the request's own and for a set is the site's latest, from the resolver's cache.
    /// </summary>
    private sealed class ResolvedSiteVariables(Func<ResolvedVariables> resolved) : ISiteVariables
    {
        public string? this[string name] => resolved().All.GetValueOrDefault(name);

        public bool TryGet(string name, [MaybeNullWhen(false)] out string value) => resolved().All.TryGetValue(name, out value);

        public string Get(string name, string fallback = "") =>
            resolved().All.TryGetValue(name, out var value) && value.Length > 0 ? value : fallback;

        public IReadOnlyDictionary<string, string> All => resolved().All;

        public bool IsPublic(string name) => resolved().Public.ContainsKey(name);
    }
}
