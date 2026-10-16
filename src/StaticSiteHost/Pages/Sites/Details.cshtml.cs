using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Pages.Shared;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Ai;
using StaticSiteHost.Services.Realtime;

namespace StaticSiteHost.Pages.Sites;

/// <summary>
/// A rule a person can drop straight into an editor on the site page. The point is that
/// nobody has to work the syntax out from a description before their first rule. The label and
/// the hint are string keys, shown in the viewer's language; the snippet is the rule itself.
/// </summary>
public sealed record RuleExample(string LabelKey, string HintKey, string Snippet);

/// <summary>A ready-to-run command shown on a site's detail page. The title and the hint are string keys.</summary>
public sealed record DeploySnippet(string Id, string TitleKey, string HintKey, string Command)
{
    public int Lines => Command.Count(c => c == '\n') + 1;
}

/// <summary>One form on the Proxy card: a saved route, or the empty one for adding.</summary>
/// <param name="Id">Prefix for the form's element ids.</param>
/// <param name="OriginalPath">The path the route is saved under, or null for a new one.</param>
/// <param name="HeadersText">The request headers, in the text form the box shows.</param>
/// <param name="Errors">
/// Why the last save of this form was refused: the services' messages as they are, or
/// <see cref="ProxyRouteInput.BodyLimitErrorKey"/>, which the editor shows in the viewer's language.
/// </param>
/// <param name="TestResult">What the last test of this route found, and whether it got an answer.</param>
/// <param name="Domain">The site the route is for, or null for a server-wide route (Admin → Proxy).</param>
/// <param name="OriginalPath">
/// What the saved route is known by: its path for a site's route, its id for a server-wide one.
/// Null for a new route.
/// </param>
public sealed record ProxyRouteEditor(
    string Id,
    string? Domain,
    ProxyRoute Route,
    string? OriginalPath,
    string HeadersText,
    IReadOnlyList<string> Errors,
    (string Message, bool Answered)? TestResult = null)
{
    public bool IsNew => OriginalPath is null;

    /// <summary>A server-wide route, which has a Sites pattern and is known by its id.</summary>
    public bool IsServer => Route is ServerProxyRoute;

    /// <summary>What was typed into the body limit when a save was refused, shown back as it was.</summary>
    public string? BodyLimitText { get; init; }
}

/// <summary>
/// What a Proxy card form posts. The body limit is in megabytes here, fractions allowed, and in
/// bytes everywhere else. It arrives as text and is read with the invariant culture, since a
/// number field always sends a dot whatever language the server runs in.
/// </summary>
public sealed class ProxyRouteInput
{
    public string? Path { get; set; }
    public string? Target { get; set; }

    /// <summary>A server-wide route's Sites pattern; unused for a site's own route.</summary>
    public string? Sites { get; set; }
    public bool Enabled { get; set; }
    public bool StripPath { get; set; }
    public bool WebSockets { get; set; }
    public bool CrossSiteWebSockets { get; set; }
    public bool Streaming { get; set; }
    public bool GrpcWeb { get; set; }
    public string? HttpVersion { get; set; }
    public int TimeoutSeconds { get; set; } = 100;
    public string? MaxRequestBodyMegabytes { get; set; }
    public bool KeepHost { get; set; }
    public bool ForwardedHeaders { get; set; }
    public string? RequestHeaders { get; set; }
    public string? CookieMode { get; set; }
    public string? CookieParentDomain { get; set; }
    public bool RewriteRedirects { get; set; }
    public bool FilesFirst { get; set; }
    public bool AcceptAnyCertificate { get; set; }

    private const decimal Megabyte = 1024 * 1024;

    /// <summary>A byte count as the form shows it: megabytes, with up to three decimals; empty for none.</summary>
    public static string Megabytes(long? bytes) =>
        bytes is { } b ? (b / Megabyte).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>The string key of the message that says the body limit typed cannot be used.</summary>
    public const string BodyLimitErrorKey = "site.route.bodyLimitError";

    /// <summary>
    /// Why the body limit typed cannot be used, as the key of its message (<see cref="BodyLimitErrorKey"/>),
    /// or null. It goes into the editor's errors, and _ProxyRoute shows it in the viewer's language.
    /// </summary>
    public string? BodyLimitError =>
        string.IsNullOrWhiteSpace(MaxRequestBodyMegabytes) || BytesOf(MaxRequestBodyMegabytes) is not null
            ? null
            : BodyLimitErrorKey;

    /// <summary>Megabytes as typed, in bytes; null when empty or not a number above 0.</summary>
    public static long? BytesOf(string? megabytes) =>
        // A dot for the decimal point and nothing else: no thousands separators, so "1,5" is refused
        // rather than read as 15.
        decimal.TryParse(megabytes?.Trim(), System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var mb) &&
        mb > 0 && mb < long.MaxValue / Megabyte
            ? (long)Math.Round(mb * Megabyte)
            : null;

    /// <summary>The route as a server-wide one, keeping <paramref name="id"/> (null for a new route).</summary>
    public ServerProxyRoute ToServerRoute(List<ProxyHeader> headers, string? id)
    {
        var route = Fill(new ServerProxyRoute(), headers);
        route.Id = id ?? "";
        route.Sites = Sites ?? "";
        return route;
    }

    public ProxyRoute ToRoute(List<ProxyHeader> headers) => Fill(new ProxyRoute(), headers);

    private T Fill<T>(T route, List<ProxyHeader> headers) where T : ProxyRoute
    {
        route.Path = Path ?? "";
        route.Target = Target ?? "";
        route.Enabled = Enabled;
        route.StripPath = StripPath;
        route.WebSockets = WebSockets;
        route.CrossSiteWebSockets = CrossSiteWebSockets;
        route.Streaming = Streaming;
        route.GrpcWeb = GrpcWeb;
        route.HttpVersion = HttpVersion ?? ProxyHttpVersions.Auto;
        route.TimeoutSeconds = TimeoutSeconds;
        route.MaxRequestBodyBytes = BytesOf(MaxRequestBodyMegabytes);
        route.KeepHost = KeepHost;
        route.ForwardedHeaders = ForwardedHeaders;
        route.RequestHeaders = headers;
        route.CookieMode = CookieMode ?? ProxyCookieModes.Host;
        route.CookieParentDomain = CookieParentDomain;
        route.RewriteRedirects = RewriteRedirects;
        route.FilesFirst = FilesFirst;
        route.AcceptAnyCertificate = AcceptAnyCertificate;
        return route;
    }
}

public class DetailsModel : PageModel
{
    private readonly SiteStore _sites;
    private readonly ZipDeploymentService _deployer;
    private readonly SiteContentServer _content;
    private readonly SitePasscodeGate _passcodes;
    private readonly ApiKeyStore _apiKeys;
    private readonly SiteRuleService _rules;
    private readonly SiteProxyService _proxy;
    private readonly SiteVariableService _variables;
    private readonly AuditLog _audit;
    private readonly FunctionHost _functions;
    private readonly FunctionDeploymentService _functionDeployer;
    private readonly AiProviderStore _aiProviders;
    private readonly SiteAiChatFactory _aiChats;
    private readonly SiteAiSettingsService _aiSettings;
    private readonly RealtimeRegistry _realtime;
    private readonly SiteHostingOptions _options;
    private readonly IStringLocalizer<Strings> _strings;

    public DetailsModel(
        SiteStore sites,
        ZipDeploymentService deployer,
        SiteContentServer content,
        SitePasscodeGate passcodes,
        ApiKeyStore apiKeys,
        SiteRuleService rules,
        SiteProxyService proxy,
        SiteVariableService variables,
        AuditLog audit,
        FunctionHost functions,
        FunctionDeploymentService functionDeployer,
        AiProviderStore aiProviders,
        SiteAiChatFactory aiChats,
        SiteAiSettingsService aiSettings,
        RealtimeRegistry realtime,
        StaticSiteHost.Tls.CertificateManager certificates,
        BrandingService branding,
        IOptions<SiteHostingOptions> options,
        IStringLocalizer<Strings> strings)
    {
        _strings = strings;
        _branding = branding;
        _certificates = certificates;
        _functions = functions;
        _functionDeployer = functionDeployer;
        _aiProviders = aiProviders;
        _aiChats = aiChats;
        _aiSettings = aiSettings;
        _realtime = realtime;
        _sites = sites;
        _deployer = deployer;
        _content = content;
        _passcodes = passcodes;
        _apiKeys = apiKeys;
        _rules = rules;
        _proxy = proxy;
        _variables = variables;
        _audit = audit;
        _options = options.Value;
    }

    private readonly BrandingService _branding;

    public SiteRecord Site { get; private set; } = null!;

    /// <summary>The editor for this site's own branding of its visitor screens; administrators only.</summary>
    public BrandingEditor? Branding { get; private set; }

    private readonly StaticSiteHost.Tls.CertificateManager _certificates;

    /// <summary>Whether this server ends HTTPS itself (SiteHosting:Tls:Enabled), so certificates are managed here.</summary>
    public bool HttpsInServer => _certificates.Enabled;

    /// <summary>The site's certificate as the HTTPS card shows it, when HTTPS is in the server.</summary>
    public StaticSiteHost.Tls.HostCertificateStatus? Certificate { get; private set; }

    /// <summary>Why the last HTTPS change or upload was refused, shown on the card.</summary>
    public string? TlsError { get; private set; }

    public bool IsAdministrator => User.IsInRole(Roles.Administrator);

    /// <summary>The tab shown: the one a form's handler belongs to after a post, else the one the address names.</summary>
    public string Tab { get; private set; } = SiteTabs.Default;

    /// <summary>A number beside a tab's name, or null for none.</summary>
    public int? TabCount(string tab) => tab switch
    {
        "routing" => Site.Proxy.Count,
        "releases" => Site.Releases.Count,
        _ => null
    };

    public override void OnPageHandlerSelected(Microsoft.AspNetCore.Mvc.Filters.PageHandlerSelectedContext context)
    {
        var fromHandler = HttpMethods.IsPost(Request.Method) ? SiteTabs.ForHandler(context.HandlerMethod?.Name) : null;
        Tab = fromHandler ?? SiteTabs.Normalize(RouteData.Values["tab"] as string, IsAdministrator);
    }

    /// <summary>A form's redirect back to this page lands on its own tab, whatever address it was posted from.</summary>
    public override void OnPageHandlerExecuted(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutedContext context)
    {
        if (context.Result is RedirectToPageResult { PageName: null } redirect)
        {
            redirect.RouteValues ??= new Microsoft.AspNetCore.Routing.RouteValueDictionary();
            if (!redirect.RouteValues.ContainsKey("tab")) redirect.RouteValues["tab"] = Tab == SiteTabs.Default ? null : Tab;
        }
    }

    /// <summary>The largest text file the Files tab opens in its editor; larger ones can be downloaded and replaced.</summary>
    public const int MaxEditBytes = 5 * 1024 * 1024;

    /// <summary>How many recent releases the server keeps besides the live one (SiteHosting:ReleasesToKeep).</summary>
    public int ReleasesToKeep => Math.Max(1, _options.ReleasesToKeep);

    public int MinPasscodeLength => _passcodes.MinLength;

    /// <summary>The default asset max-age, quoted in the header rule editor.</summary>
    public int AssetCacheSeconds => _options.AssetCacheSeconds;

    /// <summary>How long an unlocked visitor stays unlocked, in words.</summary>
    public string PasscodeSession
    {
        get
        {
            var lifetime = _passcodes.SessionLifetime;
            return lifetime.TotalHours < 48
                ? _strings.Plural("site.visibility.hours", (long)Math.Round(lifetime.TotalHours))
                : _strings.Plural("site.visibility.days", (long)Math.Round(lifetime.TotalDays));
        }
    }

    /// <summary>Commands for this domain, already filled in. See <see cref="BuildDeploySnippets"/>.</summary>
    public IReadOnlyList<DeploySnippet> DeploySnippets { get; private set; } = [];

    /// <summary>False when the snippets would fail for want of a key the user has not made yet.</summary>
    public bool HasUsableApiKey { get; private set; }

    /// <summary>The site's own rules, in the text form each editor shows.</summary>
    public string HeaderRules { get; private set; } = "";

    public string RedirectRules { get; private set; } = "";

    /// <summary>Rules that came in with the current release, shown but not editable here.</summary>
    public IReadOnlyList<HeaderRule> ReleaseHeaderRules => Site.Current?.Headers ?? [];

    public IReadOnlyList<RedirectRule> ReleaseRedirectRules => Site.Current?.Redirects ?? [];

    public IReadOnlyList<string> HeaderRuleErrors { get; private set; } = [];

    public IReadOnlyList<string> RedirectRuleErrors { get; private set; } = [];

    /// <summary>The Proxy card's forms: one per saved route, in path order, then the one for adding.</summary>
    public IReadOnlyList<ProxyRouteEditor> ProxyEditors { get; private set; } = [];

    /// <summary>The server-wide routes whose Sites match this site, and whether each is in effect here or why not.</summary>
    public IReadOnlyList<(ServerProxyRoute Route, string State)> ServerRoutes { get; private set; } = [];

    /// <summary>Environment variables a route names with <c>${env:…}</c> that the server does not have.</summary>
    public IReadOnlyList<string> ProxyUnsetEnvironment { get; private set; } = [];

    /// <summary>Where the site's live functions stand: loaded and running their jobs, failed and why, or not loaded yet. Null without functions.</summary>
    public FunctionScopeStatus? FunctionsStatus { get; private set; }

    /// <summary>The Variables card's rows: what the release declares, then what only the site holds.</summary>
    public IReadOnlyList<VariableView> Variables { get; private set; } = [];

    /// <summary>Required variables with an empty value.</summary>
    public IReadOnlyList<string> MissingVariables { get; private set; } = [];

    /// <summary>Environment variables some value names with <c>${env:…}</c> that the server does not have.</summary>
    public IReadOnlyList<string> UnsetEnvironment { get; private set; } = [];

    /// <summary>Why the last change to a variable was refused, shown beside the form it came from.</summary>
    public string? VariableError { get; private set; }

    /// <summary>The row <see cref="VariableError"/> belongs to, or null for the "Add a variable" form.</summary>
    public string? VariableErrorFor { get; private set; }

    /// <summary>What was typed into "Add a variable", put back when it is refused. Never a secret's value.</summary>
    public string? NewVariableName { get; private set; }

    public string? NewVariableValue { get; private set; }
    public bool NewVariablePublic { get; private set; }
    public bool NewVariableSecret { get; private set; }

    /// <summary>Every AI provider, for the AI card's select.</summary>
    public IReadOnlyList<AiProviderRecord> AiProviders { get; private set; } = [];

    /// <summary>The provider the site's settings name, or null when it has none or it was deleted.</summary>
    public AiProviderRecord? AiProvider { get; private set; }

    /// <summary>Why the last change to the AI settings was refused, shown beside the form.</summary>
    public string? AiError { get; private set; }

    /// <summary>What the AI form shows: the saved settings, or what was typed when a save was refused.</summary>
    public string? AiProviderId { get; private set; }

    public string? AiModelName { get; private set; }
    public string? AiSystemPrompt { get; private set; }

    /// <summary>
    /// The saved "let every visitor chat" setting, or what was typed. Consulted only while the live
    /// functions declare no <c>[AiAccess]</c> hook (<see cref="AiAccessHook"/>).
    /// </summary>
    public bool AiAllowVisitors { get; private set; }

    /// <summary>
    /// The live functions' <c>[AiAccess]</c> method, such as <c>Ai.Writers</c>, when they declare
    /// one: it decides which browsers may chat, and the "let every visitor chat" setting is not
    /// consulted. Null without one.
    /// </summary>
    public string? AiAccessHook { get; private set; }

    public int AiMaxSystemPromptBytes => SiteAiSettings.MaxSystemPromptBytes;

    /// <summary>The per-address limit on visitors' chats (0 for none), for the warning beside the checkbox.</summary>
    public int AiVisitorRequestsPerMinute => _options.AiVisitorRequestsPerMinute;

    /// <summary>How a page on the site chats, for the snippet under the AI form.</summary>
    public string AiSnippet { get; private set; } = "";

    /// <summary>Pages connected to the site's realtime hub as the page was rendered.</summary>
    public int RealtimeConnections { get; private set; }

    /// <summary>The site's realtime groups with their member counts, in name order.</summary>
    public IReadOnlyList<(string Name, int Members)> RealtimeGroups { get; private set; } = [];

    /// <summary>The most groups the Realtime card lists; the rest are counted.</summary>
    public const int RealtimeGroupsShown = 50;

    /// <summary>How a page listens, for the Realtime card.</summary>
    public string RealtimeSnippet { get; private set; } = "";

    /// <summary>How a script or a CI job tells the site's pages something, for the Realtime card.</summary>
    public string RealtimePublishSnippet { get; private set; } = "";

    /// <summary>Why the last function upload was refused, shown in the Functions card.</summary>
    public string? FunctionError { get; private set; }

    /// <summary>The compiler's messages for a refused upload, with the author's line numbers.</summary>
    public IReadOnlyList<FunctionDiagnostic> FunctionDiagnostics { get; private set; } = [];

    /// <summary>How a release's functions read in the releases table and the rollback choice.</summary>
    public string BundleLabel(string? bundleId) =>
        Site.FindBundle(bundleId) is { } bundle ? FunctionUploads.Label(bundle, _strings) : _strings.Text("site.releases.noFunctions");

    /// <summary>Starting points offered under the header rule editor.</summary>
    public static IReadOnlyList<RuleExample> HeaderExamples { get; } =
    [
        new("site.headers.example.noCacheJson", "site.headers.example.noCacheJsonHint",
            "/*.json\n  Cache-Control: no-cache"),

        new("site.headers.example.fingerprinted", "site.headers.example.fingerprintedHint",
            "/assets/**\n  Cache-Control: public, max-age=31536000, immutable"),

        new("site.headers.example.noStore", "site.headers.example.noStoreHint",
            "/private/**\n  Cache-Control: no-store"),

        new("site.headers.example.security", "site.headers.example.securityHint",
            "/**\n  X-Frame-Options: DENY\n  Referrer-Policy: strict-origin-when-cross-origin\n  Permissions-Policy: geolocation=(), camera=(), microphone=()"),

        new("site.headers.example.cors", "site.headers.example.corsHint",
            "/data/**\n  Access-Control-Allow-Origin: *"),

        new("site.headers.example.feed", "site.headers.example.feedHint",
            "/feed.xml\n  Cache-Control: public, max-age=300")
    ];

    /// <summary>Starting points offered under the redirect editor.</summary>
    public static IReadOnlyList<RuleExample> RedirectExamples { get; } =
    [
        new("site.redirects.example.moved", "site.redirects.example.movedHint",
            "/old-page  /new-page  301"),

        new("site.redirects.example.section", "site.redirects.example.sectionHint",
            "/blog/*  /articles/:1  301"),

        new("site.redirects.example.otherSite", "site.redirects.example.otherSiteHint",
            "/docs/**  https://docs.example.com/:1  302"),

        new("site.redirects.example.folder", "site.redirects.example.folderHint",
            "/app/**  /app/index.html  200"),

        new("site.redirects.example.retire", "site.redirects.example.retireHint",
            "/removed/**  /404.html  404"),

        new("site.redirects.example.force", "site.redirects.example.forceHint",
            "/legacy/*  /new/:1  301!")
    ];

    public async Task<IActionResult> OnGetAsync(string domain)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        await LoadAsync(site);
        return Page();
    }

    private async Task LoadAsync(SiteRecord site, IReadOnlyList<string>? brandingErrors = null, Branding? brandingShown = null)
    {
        Site = site;
        if (IsAdministrator)
        {
            var settings = await _branding.Store.ReadAsync(HttpContext.RequestAborted);
            Branding = new BrandingEditor(
                BrandingEditor.Filled(brandingShown ?? site.Branding, settings.Server), brandingShown ?? site.Branding ?? new Branding(), settings.Fonts,
                settings.AllowGoogleFonts, site.Domain, settings.Server, brandingErrors ?? [],
                Url.Page("/Sites/Details", "Branding", new { domain = site.Domain, tab = "settings" })!);
        }
        DeploySnippets = BuildDeploySnippets(site.Domain);
        HeaderRules = HeaderRuleText.Format(site.Headers);
        RedirectRules = RedirectRuleText.Format(site.Redirects);
        LoadProxy(site);
        ServerRoutes = ProxySites.ForSite(site.Domain, site.Proxy, await _proxy.ServerRoutesAsync(), site.DisabledServerRoutes).ToList();

        FunctionsStatus = site.CurrentFunctions is null ? null : await _functions.StatusAsync(site.Domain);
        Variables = _variables.Describe(site);
        var resolved = _variables.Resolve(site);
        MissingVariables = resolved.Missing;
        UnsetEnvironment = resolved.UnsetEnvironment;

        var keys = await _apiKeys.ListForUserAsync(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
        HasUsableApiKey = keys.Any(k => k.IsUsable);

        AiProviders = await _aiProviders.ListAsync();
        AiProvider = _aiChats.ProviderOf(site);
        AiProviderId = site.Ai?.ProviderId;
        AiModelName = site.Ai?.Model;
        AiSystemPrompt = site.Ai?.SystemPrompt;
        AiAllowVisitors = site.Ai?.AllowVisitors ?? false;
        AiAccessHook = AccessHookOf(site);
        AiSnippet = BuildAiSnippet(site.Domain);

        Certificate = _certificates.Enabled ? _certificates.StatusOf(site.Domain) : null;

        RealtimeConnections = _realtime.ConnectionCount(site.Domain);
        RealtimeGroups = _realtime.GroupSizes(site.Domain);
        RealtimeSnippet = BuildRealtimeSnippet();
        RealtimePublishSnippet = BuildRealtimePublishSnippet(site.Domain);
    }

    private void LoadProxy(SiteRecord site)
    {
        var testedPath = TempData["ProxyTestPath"] as string;
        (string, bool)? test = TempData["ProxyTestResult"] is string message
            ? (message, TempData["ProxyTestAnswered"] as bool? == true)
            : null;

        ProxyEditors =
        [
            .. site.Proxy.Select((route, i) => new ProxyRouteEditor(
                $"proxy-{i}", site.Domain, route, route.Path, ProxyRouteRules.FormatHeaders(route.RequestHeaders), [],
                route.Path == testedPath ? test : null)),
            new ProxyRouteEditor("proxy-new", site.Domain, new ProxyRoute(), null, "", [])
        ];

        var unset = new List<string>();
        foreach (var route in site.Proxy)
        {
            SiteVariableService.Expand(route.Target, unset);
            foreach (var header in route.RequestHeaders) SiteVariableService.Expand(header.Value, unset);
        }
        ProxyUnsetEnvironment = unset;
    }

    /// <summary>The live functions' <c>[AiAccess]</c> method, as their record names it ("AiAccess Ai.Writers" gives "Ai.Writers"), or null.</summary>
    private static string? AccessHookOf(SiteRecord site)
    {
        var prefix = FunctionAccessHooks.Kind.AiAccess + " ";
        return site.CurrentFunctions?.Hooks.FirstOrDefault(hook => hook.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    /// <summary>A page that offers to reload when the server says a new version is up (see <c>SiteDeployedNotifier</c>).</summary>
    private static string BuildRealtimeSnippet() =>
        """
        <script src="/_host/site.js"></script>
        <script>
          site.realtime.on('site.deployed', function () {
            if (confirm('A new version of this site is up. Reload?')) location.reload();
          });
        </script>
        """;

    /// <summary>
    /// A call that sends every open page an event of its own, pointed at this server and this
    /// domain. Not <c>site.deployed</c>, which the server sends itself whenever a new version goes
    /// live. The key stays a <c>$SSH_KEY</c> reference, as in the deploy snippets.
    /// </summary>
    private string BuildRealtimePublishSnippet(string domain) =>
        $$"""
          curl -sS --fail-with-body -X POST \
            -H "X-Api-Key: $SSH_KEY" \
            -H "Content-Type: application/json" \
            -d '{"event":"notice","payload":"Back in five minutes."}' \
            "{{Request.Scheme}}://{{Request.Host}}/api/v1/sites/{{domain}}/realtime/publish"
          """;

    /// <summary>
    /// The least a page needs to chat, and the endpoint it calls, for this site. Streams the answer
    /// into an element as it arrives.
    /// </summary>
    private string BuildAiSnippet(string domain) =>
        $$"""
          <script src="/_host/site.js"></script>
          <script>
            var answer = document.getElementById('answer');
            site.ai.chat('Suggest a first thing to read here.', {
              onText: function (text) { answer.textContent += text; }
            }).catch(function (error) { answer.textContent = error.message; });
          </script>

          <!-- It calls POST {{SiteLinks.For(Request, domain)}}_host/ai/chat -->
          """;

    /// <summary>
    /// The two deploy calls from the README, pointed at this server and this domain so they
    /// can go straight into a terminal or a CI job without being edited first. The key stays
    /// a <c>$SSH_KEY</c> reference — it is never rendered into the page.
    /// </summary>
    private IReadOnlyList<DeploySnippet> BuildDeploySnippets(string domain)
    {
        var endpoint = $"{Request.Scheme}://{Request.Host}/api/v1/sites/{domain}/deploy";

        return
        [
            new DeploySnippet(
                "deploy-zip",
                "site.cli.zipTitle",
                "site.cli.zipHint",
                $"""
                 curl -sS --fail-with-body -X POST \
                   -H "X-Api-Key: $SSH_KEY" \
                   -F "file=@site.zip" \
                   "{endpoint}"
                 """),

            new DeploySnippet(
                "deploy-folder",
                "site.cli.folderTitle",
                "site.cli.folderHint",
                $"""
                 (cd dist && zip -qr - .) | curl -sS --fail-with-body -X POST \
                   -H "X-Api-Key: $SSH_KEY" \
                   -H "Content-Type: application/zip" \
                   -H "X-Archive-Name: dist.zip" \
                   --data-binary @- \
                   "{endpoint}"
                 """)
        ];
    }

    public async Task<IActionResult> OnPostRollbackAsync(string domain, string releaseId, string? functions)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        var actor = User.Identity?.Name ?? "unknown";
        var keep = functions == "keep";
        var (ok, error) = await _deployer.RollbackAsync(site.Domain, releaseId, actor, keep);

        if (ok) TempData["StatusMessage"] = _strings.Text("site.releases.rolledBack", new { id = releaseId, domain = site.Domain });
        else TempData["ErrorMessage"] = error;

        return RedirectToPage(new { domain = site.Domain });
    }

    /// <summary>Copies a release as a new one that is not live: the way back before editing the live one.</summary>
    public async Task<IActionResult> OnPostDuplicateAsync(string domain, string releaseId)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        var result = await _deployer.DuplicateReleaseAsync(site.Domain, releaseId, User.Identity?.Name ?? "unknown");
        if (result.Error is { } error) TempData["ErrorMessage"] = _strings.Text(error.Key, error.Values);
        else TempData["StatusMessage"] = _strings.Text("site.files.duplicated", new { id = result.Release!.Id, from = releaseId });

        return RedirectToPage(new { domain = site.Domain });
    }

    public async Task<IActionResult> OnPostSetPasscodeAsync(string domain, string? passcode)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        var replacing = site.IsPasscodeProtected;
        var (ok, error) = await _passcodes.SetAsync(site, passcode, User.Identity?.Name ?? "unknown");

        // A new passcode shuts out everyone who entered the old one, the pages they have open included.
        if (ok) _realtime.DisconnectSite(site.Domain);

        if (!ok) TempData["ErrorMessage"] = error;
        else if (replacing) TempData["StatusMessage"] = _strings.Text("site.visibility.changed", new { domain = site.Domain });
        else TempData["StatusMessage"] = _strings.Text("site.visibility.madePrivate", new { domain = site.Domain });

        return RedirectToPage(new { domain = site.Domain });
    }

    public async Task<IActionResult> OnPostRemovePasscodeAsync(string domain)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        if (await _passcodes.ClearAsync(site, User.Identity?.Name ?? "unknown"))
            TempData["StatusMessage"] = _strings.Text("site.visibility.madePublic", new { domain = site.Domain });

        return RedirectToPage(new { domain = site.Domain });
    }

    public async Task<IActionResult> OnPostHeadersAsync(string domain, string? rules)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        // On a rejection the page is re-rendered rather than redirected to, so whatever was
        // typed comes back with the errors instead of being thrown away.
        if (!HeaderRuleText.TryParse(rules, out var parsed, out var errors))
            return await RejectAsync(site, headers: rules, redirects: null, errors, []);

        var (ok, failures) = await _rules.SetHeadersAsync(site, parsed, User.Identity?.Name ?? "unknown");
        if (!ok) return await RejectAsync(site, headers: rules, redirects: null, failures, []);

        TempData["StatusMessage"] = parsed.Count == 0
            ? _strings.Text("site.headers.removed", new { domain = site.Domain })
            : _strings.Plural("site.headers.saved", parsed.Count, new { domain = site.Domain });
        return RedirectToPage(new { domain = site.Domain });
    }

    public async Task<IActionResult> OnPostRedirectsAsync(string domain, string? rules)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        if (!RedirectRuleText.TryParse(rules, out var parsed, out var errors))
            return await RejectAsync(site, headers: null, redirects: rules, [], errors);

        var (ok, failures) = await _rules.SetRedirectsAsync(site, parsed, User.Identity?.Name ?? "unknown");
        if (!ok) return await RejectAsync(site, headers: null, redirects: rules, [], failures);

        TempData["StatusMessage"] = parsed.Count == 0
            ? _strings.Text("site.redirects.removed", new { domain = site.Domain })
            : _strings.Plural("site.redirects.saved", parsed.Count, new { domain = site.Domain });
        return RedirectToPage(new { domain = site.Domain });
    }

    /// <summary>Re-renders the page with the text that was rejected still in its box.</summary>
    private async Task<IActionResult> RejectAsync(
        SiteRecord site,
        string? headers,
        string? redirects,
        IReadOnlyList<string> headerErrors,
        IReadOnlyList<string> redirectErrors)
    {
        await LoadAsync(site);

        if (headers is not null) HeaderRules = headers;
        if (redirects is not null) RedirectRules = redirects;

        HeaderRuleErrors = headerErrors;
        RedirectRuleErrors = redirectErrors;
        return Page();
    }

    /// <summary>
    /// Adds a proxy route, or saves one under the path it had (<paramref name="originalPath"/>).
    /// Administrators only: a route makes the server send requests wherever it points.
    /// </summary>
    public async Task<IActionResult> OnPostSaveProxyRouteAsync(string domain, string? originalPath, ProxyRouteInput input)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        ProxyRouteRules.TryParseHeaders(input.RequestHeaders, out var headers, out var headerError);

        IReadOnlyList<string> errors;
        if (headerError is not null) errors = [headerError];
        else if (input.BodyLimitError is { } bodyLimitError) errors = [bodyLimitError];
        else
        {
            var (ok, failures) = await _proxy.SaveRouteAsync(site, originalPath, input.ToRoute(headers), User.Identity?.Name ?? "unknown");
            if (ok)
            {
                var path = ProxyRouteRules.NormalizePath(input.Path);
                TempData["StatusMessage"] = originalPath is null
                    ? _strings.Text("site.proxy.added", new { domain = site.Domain, path, target = input.Target?.Trim() })
                    : _strings.Text("site.proxy.saved", new { path });
                return RedirectToPage(null, null, new { domain = site.Domain }, "proxy");
            }

            errors = failures;
        }

        // Rendered in place, so what was typed comes back with the errors instead of being thrown away.
        await LoadAsync(site);
        var draft = input.ToRoute(headers);
        ProxyEditors = ProxyEditors
            .Select(editor => editor.OriginalPath == originalPath
                ? editor with { Route = draft, HeadersText = input.RequestHeaders ?? "", BodyLimitText = input.MaxRequestBodyMegabytes, Errors = errors }
                : editor)
            .ToList();
        return Page();
    }

    /// <summary>Turns a server-wide route off for this site, or back on. Administrators only, like the site's own routes.</summary>
    public async Task<IActionResult> OnPostServerRouteAsync(string domain, string? path, bool enabled)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        if (await _proxy.SetServerRouteForSiteAsync(site, path, enabled, User.Identity?.Name ?? "unknown") is { } error)
            TempData["ErrorMessage"] = error;
        else
            TempData["StatusMessage"] = enabled
                ? _strings.Text("site.proxy.serverOn", new { path = ProxyRouteRules.NormalizePath(path), domain = site.Domain })
                : _strings.Text("site.proxy.serverOff", new { path = ProxyRouteRules.NormalizePath(path), domain = site.Domain });

        return RedirectToPage(null, null, new { domain = site.Domain }, "proxy");
    }

    public async Task<IActionResult> OnPostRemoveProxyRouteAsync(string domain, string? path)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        if (await _proxy.RemoveRouteAsync(site, path, User.Identity?.Name ?? "unknown"))
            TempData["StatusMessage"] = _strings.Text("site.proxy.removed", new { path = ProxyRouteRules.NormalizePath(path), domain = site.Domain });
        else
            TempData["ErrorMessage"] = _strings.Text("site.proxy.notFound", new { domain = site.Domain, path = ProxyRouteRules.NormalizePath(path) });

        return RedirectToPage(null, null, new { domain = site.Domain }, "proxy");
    }

    /// <summary>Sends a GET to a saved route's target from the server and shows what came back beside the route.</summary>
    public async Task<IActionResult> OnPostTestProxyRouteAsync(string domain, string? path, CancellationToken ct)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        var normalized = ProxyRouteRules.NormalizePath(path);
        var route = site.Proxy.FirstOrDefault(r => r.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (route is null)
        {
            TempData["ErrorMessage"] = _strings.Text("site.proxy.notFound", new { domain = site.Domain, path = normalized });
            return RedirectToPage(null, null, new { domain = site.Domain }, "proxy");
        }

        var result = await _proxy.TestAsync(route, ct);
        TempData["ProxyTestPath"] = route.Path;
        TempData["ProxyTestResult"] = result.Describe();
        TempData["ProxyTestAnswered"] = result.Answered;
        return RedirectToPage(null, null, new { domain = site.Domain }, "proxy");
    }

    /// <summary>
    /// Saves one variable, from its row or from "Add a variable". Only the add form sends the two
    /// checkboxes (each with a hidden "false" behind it), so a row's Save leaves a variable's
    /// visibility as it was.
    /// </summary>
    public async Task<IActionResult> OnPostSetVariableAsync(string domain, string? name, string? value, bool? isPublic, bool? isSecret)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        name = name?.Trim() ?? "";
        var adding = isPublic is not null || isSecret is not null;

        // A secret's box is always empty, because its value never comes back to the page. Saving
        // it untouched must not wipe what is stored.
        if (string.IsNullOrEmpty(value) && _variables.Describe(site).Any(v => v.Name == name && v.IsSecret))
        {
            TempData["StatusMessage"] = _strings.Text("site.variables.secretKept", new { name });
            return RedirectToPage(new { domain = site.Domain });
        }

        var (ok, error) = await _variables.SetAsync(
            site, name, value, adding ? isPublic ?? false : null, adding ? isSecret ?? false : null,
            User.Identity?.Name ?? "unknown", IsAdministrator);

        if (!ok)
        {
            // Rendered in place, so the message sits beside the form and what was typed survives.
            await LoadAsync(site);
            VariableError = error;
            VariableErrorFor = adding ? null : name;

            if (adding)
            {
                NewVariableName = name;
                NewVariablePublic = isPublic == true;
                NewVariableSecret = isSecret == true;
                NewVariableValue = NewVariableSecret ? null : value;
            }

            return Page();
        }

        TempData["StatusMessage"] = _strings.Text("site.variables.saved", new { name, domain = site.Domain });
        return RedirectToPage(new { domain = site.Domain });
    }

    /// <summary>Drops the site's own value, so a declared variable goes back to the release's default.</summary>
    public async Task<IActionResult> OnPostRemoveVariableAsync(string domain, string? name)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        name = name?.Trim() ?? "";
        var definition = site.Current?.Variables.FirstOrDefault(d => d.Name == name);

        var (removed, error) = await _variables.RemoveAsync(site, name, User.Identity?.Name ?? "unknown", IsAdministrator);
        if (!removed)
            TempData["ErrorMessage"] = error ?? _strings.Text("site.variables.noOwnValue", new { domain = site.Domain, name });
        else if (definition is null)
            TempData["StatusMessage"] = _strings.Text("site.variables.removed", new { name, domain = site.Domain });
        else if (definition.Default is not null)
            TempData["StatusMessage"] = _strings.Text("site.variables.defaultAgain", new { name });
        else
            TempData["StatusMessage"] = _strings.Text("site.variables.noValue", new { name });

        return RedirectToPage(new { domain = site.Domain });
    }

    /// <summary>
    /// Saves the AI settings. Administrators only: the settings spend a provider's key, and
    /// letting visitors in lets anyone spend it.
    /// </summary>
    public async Task<IActionResult> OnPostAiAsync(
        string domain, string? providerId, string? model, string? systemPrompt, bool allowVisitors)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        var (ok, error) = await _aiSettings.SetAsync(site, providerId, model, systemPrompt, allowVisitors, User.Identity?.Name ?? "unknown");
        if (!ok)
        {
            // Rendered in place, so a long system prompt is not lost to a typo in the model.
            await LoadAsync(site);
            AiError = error;
            AiProviderId = providerId;
            AiModelName = model;
            AiSystemPrompt = systemPrompt;
            AiAllowVisitors = allowVisitors;
            return Page();
        }

        var provider = _aiChats.ProviderOf(site);
        TempData["StatusMessage"] =
            AccessHookOf(site) is { } hook
                ? _strings.Text("site.ai.savedHook", new { domain = site.Domain, provider = provider?.Name, hook })
                : allowVisitors
                    ? _strings.Text("site.ai.savedVisitors", new { domain = site.Domain, provider = provider?.Name })
                    : _strings.Text("site.ai.savedFunctions", new { domain = site.Domain, provider = provider?.Name });

        return RedirectToPage(new { domain = site.Domain });
    }

    /// <summary>The site's HTTPS choices: where its certificate comes from, the redirect, HSTS.</summary>
    public async Task<IActionResult> OnPostTlsAsync(string domain, string? source, bool redirectToHttps, bool hsts)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        if (await _certificates.SetSiteAsync(site, source, redirectToHttps, hsts, User.Identity?.Name ?? "unknown") is { } error)
        {
            await LoadAsync(site);
            TlsError = error;
            return Page();
        }

        TempData["StatusMessage"] = _certificates.Enabled
            ? _strings.Text("site.https.savedCertificateSoon", new { domain = site.Domain })
            : _strings.Text("site.https.saved", new { domain = site.Domain });
        return RedirectToPage(null, null, new { domain = site.Domain }, "https");
    }

    /// <summary>A certificate and key for this site, pasted as PEM; saving it makes it the site's choice.</summary>
    public async Task<IActionResult> OnPostUploadCertificateAsync(string domain, string? certificate, string? privateKey)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        if (await _certificates.UploadForSiteAsync(site, certificate, privateKey, User.Identity?.Name ?? "unknown") is { } error)
        {
            await LoadAsync(site);
            TlsError = error;
            return Page();
        }

        TempData["StatusMessage"] = _strings.Text("site.https.uploaded", new { domain = site.Domain });
        return RedirectToPage(null, null, new { domain = site.Domain }, "https");
    }

    public async Task<IActionResult> OnPostRemoveCertificateAsync(string domain)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        await _certificates.RemoveUploadedAsync(site, User.Identity?.Name ?? "unknown");
        TempData["StatusMessage"] = _strings.Text("site.https.removed", new { domain = site.Domain });
        return RedirectToPage(null, null, new { domain = site.Domain }, "https");
    }

    public IActionResult OnPostRenewCertificate(string domain)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        if (_certificates.Enabled)
        {
            _certificates.RenewNow(site.Domain);
            TempData["StatusMessage"] = _strings.Text("site.https.renewing", new { domain = site.Domain });
        }

        return RedirectToPage(null, null, new { domain = site.Domain }, "https");
    }

    /// <summary>Clears the AI settings, so neither the site's functions nor its visitors can chat.</summary>
    public async Task<IActionResult> OnPostRemoveAiAsync(string domain)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        if (await _aiSettings.RemoveAsync(site, User.Identity?.Name ?? "unknown"))
            TempData["StatusMessage"] = _strings.Text("site.ai.removed", new { domain = site.Domain });

        return RedirectToPage(new { domain = site.Domain });
    }

    public async Task<IActionResult> OnPostFunctionsAsync(string domain, List<IFormFile> files, string? mode, CancellationToken ct)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        var (uploaded, readError) = await FunctionUploads.ReadAsync(files, _strings, ct);
        var result = uploaded is null
            ? FunctionDeployResult.Failed(readError!)
            : await _functionDeployer.DeployAsync(site.Domain, uploaded, User.Identity?.Name ?? "unknown", "web", replace: mode == "replace", ct);

        if (result.Ok)
        {
            TempData["StatusMessage"] = FunctionUploads.Describe(result, site.Domain, _strings);
            return RedirectToPage(new { domain = site.Domain });
        }

        // Rendered in place rather than redirected, so the compiler's messages can be shown in full.
        await LoadAsync(site);
        FunctionError = result.Error;
        FunctionDiagnostics = result.Diagnostics ?? [];
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveFunctionFileAsync(string domain, string? fileName, CancellationToken ct)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        var result = await _functionDeployer.RemoveFileAsync(site.Domain, fileName ?? "", User.Identity?.Name ?? "unknown", "web", ct);
        if (result.Ok)
        {
            TempData["StatusMessage"] = FunctionUploads.DescribeRemoved(result, fileName ?? "", site.Domain, _strings);
            return RedirectToPage(new { domain = site.Domain });
        }

        await LoadAsync(site);
        FunctionError = result.Error;
        FunctionDiagnostics = result.Diagnostics ?? [];
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveFunctionsAsync(string domain)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        var (ok, error) = await _functionDeployer.RemoveAsync(site.Domain, User.Identity?.Name ?? "unknown");
        if (ok) TempData["StatusMessage"] = _strings.Text("site.functions.removed", new { domain = site.Domain });
        else TempData["ErrorMessage"] = error;

        return RedirectToPage(new { domain = site.Domain });
    }

    /// <summary>A release as a zip that deploys back as it was: its files, rules and variable declarations, no secrets.</summary>
    public async Task<IActionResult> OnGetDownloadReleaseAsync(string domain, string? releaseId, CancellationToken ct)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");
        if (SiteFiles.ReleaseOf(site, releaseId) is not { } release) return NotFound();

        var files = HttpContext.RequestServices.GetRequiredService<SiteFiles>();
        return File(await files.CreateZipAsync(site, release, ct), "application/zip", SiteFiles.ZipName(site, release));
    }

    public async Task<IActionResult> OnGetFunctionSourceAsync(string domain)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        return await _functionDeployer.DownloadAsync(site.Domain) is { } download
            ? File(download.Content, download.ContentType, download.FileName)
            : NotFound();
    }

    /// <summary>Saves this site's own branding for its visitor screens. Values the same as the server's are not kept, so later server changes show through.</summary>
    public async Task<IActionResult> OnPostBrandingAsync(string domain, BrandingInput input)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        var settings = await _branding.Store.ReadAsync(HttpContext.RequestAborted);
        var (branding, errors) = await input.ToBrandingAsync(site.Branding ?? new Branding(), settings.Server, _branding.Store, _strings, HttpContext.RequestAborted);
        if (errors.Count == 0) errors = await _branding.SetSiteAsync(site, branding, User.Identity?.Name ?? "unknown", HttpContext.RequestAborted);

        if (errors.Count > 0)
        {
            await LoadAsync(site, errors, branding);
            return Page();
        }

        TempData["StatusMessage"] = _strings.Text(site.Branding is null ? "site.branding.sameAsServer" : "site.branding.saved", new { domain = site.Domain });
        return RedirectToPage(null, null, new { domain = site.Domain }, "branding");
    }

    public async Task<IActionResult> OnPostRemoveBrandingAsync(string domain)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        await _branding.SetSiteAsync(site, null, User.Identity?.Name ?? "unknown", HttpContext.RequestAborted);
        TempData["StatusMessage"] = _strings.Text("site.branding.removed", new { domain = site.Domain });
        return RedirectToPage(null, null, new { domain = site.Domain }, "branding");
    }

    public async Task<IActionResult> OnPostRenameAsync(string domain, string? newDomain)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        var from = site.Domain;
        var (ok, error, to) = await _deployer.RenameAsync(from, newDomain, User.Identity?.Name ?? "unknown");

        if (!ok)
        {
            TempData["ErrorMessage"] = error;
            return RedirectToPage(new { domain = from });
        }

        TempData["StatusMessage"] = site.IsPasscodeProtected
            ? _strings.Text("site.rename.donePasscode", new { from, to })
            : _strings.Text("site.rename.done", new { from, to });

        return RedirectToPage(new { domain = to });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string domain)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        await _deployer.DeleteAsync(site.Domain, User.Identity?.Name ?? "unknown");

        TempData["StatusMessage"] = _strings.Text("site.delete.done", new { domain = site.Domain });
        return RedirectToPage("Index");
    }

    private SiteRecord? Resolve(string domain)
    {
        var (normalized, _) = SiteStore.NormalizeDomain(domain);
        if (normalized is null)
        {
            TempData["ErrorMessage"] = _strings.Text("site.lookup.invalidDomain");
            return null;
        }

        var site = _sites.TryGet(normalized);
        if (site is null) TempData["ErrorMessage"] = _strings.Text("site.lookup.notFound", new { domain = normalized });
        return site;
    }
}
