using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Models;
using StaticSiteHost.Pages.Shared;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Ai;
using StaticSiteHost.Services.Realtime;

namespace StaticSiteHost.Pages.Sites;

/// <summary>
/// A rule a person can drop straight into an editor on the site page. The point is that
/// nobody has to work the syntax out from a description before their first rule.
/// </summary>
public sealed record RuleExample(string Label, string Hint, string Snippet);

/// <summary>A ready-to-run command shown on a site's detail page.</summary>
public sealed record DeploySnippet(string Id, string Title, string Hint, string Command)
{
    public int Lines => Command.Count(c => c == '\n') + 1;
}

/// <summary>One form on the Proxy card: a saved route, or the empty one for adding.</summary>
/// <param name="Id">Prefix for the form's element ids.</param>
/// <param name="OriginalPath">The path the route is saved under, or null for a new one.</param>
/// <param name="HeadersText">The request headers, in the text form the box shows.</param>
/// <param name="Errors">Why the last save of this form was refused.</param>
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

    /// <summary>Why the body limit typed cannot be used, or null.</summary>
    public string? BodyLimitError =>
        string.IsNullOrWhiteSpace(MaxRequestBodyMegabytes) || BytesOf(MaxRequestBodyMegabytes) is not null
            ? null
            : "Largest request body: write a number of megabytes greater than 0 with a dot for decimals, such as 0.5 or 10, or leave it empty.";

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
        IOptions<SiteHostingOptions> options)
    {
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

    public SiteRecord Site { get; private set; } = null!;

    public bool IsAdministrator => User.IsInRole(Roles.Administrator);

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
                ? $"{Math.Round(lifetime.TotalHours):0} hour(s)"
                : $"{Math.Round(lifetime.TotalDays):0} day(s)";
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

    /// <summary>The per-address limit on visitors' chats, in words, for the warning beside the checkbox.</summary>
    public string AiVisitorLimit => _options.AiVisitorRequestsPerMinute > 0
        ? $"Each address is held to {_options.AiVisitorRequestsPerMinute} questions a minute on this site, which slows a stranger down but does not cap the bill."
        : "This server sets no per-address limit (SiteHosting:AiVisitorRequestsPerMinute is 0), so nothing slows a stranger down.";

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
        Site.FindBundle(bundleId)?.Label ?? "no functions";

    /// <summary>Starting points offered under the header rule editor.</summary>
    public static IReadOnlyList<RuleExample> HeaderExamples { get; } =
    [
        new("Stop caching JSON",
            "For data files rewritten in place on every deploy — the browser revalidates instead of reusing a stale copy.",
            "/*.json\n  Cache-Control: no-cache"),

        new("Cache fingerprinted assets forever",
            "Safe only when the filename changes with the content, as app.a1b2c3.js does.",
            "/assets/**\n  Cache-Control: public, max-age=31536000, immutable"),

        new("Never store a folder",
            "Stronger than no-cache: nothing is written to disk by the browser or any proxy.",
            "/private/**\n  Cache-Control: no-store"),

        new("Common security headers",
            "Sensible defaults for a site that embeds nothing and is embedded nowhere.",
            "/**\n  X-Frame-Options: DENY\n  Referrer-Policy: strict-origin-when-cross-origin\n  Permissions-Policy: geolocation=(), camera=(), microphone=()"),

        new("Allow cross-origin reads",
            "Lets another origin fetch these files — fonts and data a second site loads.",
            "/data/**\n  Access-Control-Allow-Origin: *"),

        new("Short cache on a feed",
            "A middle ground for something regenerated often but read constantly.",
            "/feed.xml\n  Cache-Control: public, max-age=300")
    ];

    /// <summary>Starting points offered under the redirect editor.</summary>
    public static IReadOnlyList<RuleExample> RedirectExamples { get; } =
    [
        new("A page moved",
            "The plainest rule there is. 301 tells browsers and search engines the move is permanent.",
            "/old-page  /new-page  301"),

        new("A whole section moved",
            ":1 stands for whatever the * matched, so /blog/hello lands on /articles/hello.",
            "/blog/*  /articles/:1  301"),

        new("Send visitors to another site",
            "** crosses slashes, so the rest of the path comes along. 302 keeps it temporary.",
            "/docs/**  https://docs.example.com/:1  302"),

        new("Serve one page for a whole subtree",
            "A rewrite: the address stays put and the file underneath changes. For a SPA mounted at /app.",
            "/app/**  /app/index.html  200"),

        new("Retire a page",
            "Answers 404 with your own page rather than pretending the URL still works.",
            "/removed/**  /404.html  404"),

        new("Apply even where a file exists",
            "The ! overrides the usual behaviour of standing aside for real content.",
            "/legacy/*  /new/:1  301!")
    ];

    public async Task<IActionResult> OnGetAsync(string domain)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        await LoadAsync(site);
        return Page();
    }

    private async Task LoadAsync(SiteRecord site)
    {
        Site = site;
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
                "Upload a zip you already have",
                "Point file=@ at your archive. The zip's contents land at the root of the site; a single wrapping folder is unwrapped for you.",
                $"""
                 curl -sS --fail-with-body -X POST \
                   -H "X-Api-Key: $SSH_KEY" \
                   -F "file=@site.zip" \
                   "{endpoint}"
                 """),

            new DeploySnippet(
                "deploy-folder",
                "Zip a folder and upload it in one command",
                "Streams the archive as it is built, so nothing is written to disk on your side. Change dist to your build output.",
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

        if (ok) TempData["StatusMessage"] = $"{site.Domain} is now serving release {releaseId}.";
        else TempData["ErrorMessage"] = error;

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
        else if (replacing) TempData["StatusMessage"] = $"The passcode for {site.Domain} was replaced. Anyone using the old one has to enter the new one.";
        else TempData["StatusMessage"] = $"{site.Domain} is now private — visitors must enter the passcode.";

        return RedirectToPage(new { domain = site.Domain });
    }

    public async Task<IActionResult> OnPostRemovePasscodeAsync(string domain)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        if (await _passcodes.ClearAsync(site, User.Identity?.Name ?? "unknown"))
            TempData["StatusMessage"] = $"{site.Domain} is public again — anyone with the link can read it.";

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

        TempData["StatusMessage"] = Saved(parsed.Count, "header rule", site.Domain);
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

        TempData["StatusMessage"] = Saved(parsed.Count, "redirect", site.Domain);
        return RedirectToPage(new { domain = site.Domain });
    }

    private static string Saved(int count, string noun, string domain) => count == 0
        ? $"The {noun}s for {domain} were removed."
        : $"{count} {noun}{(count == 1 ? "" : "s")} saved for {domain}.";

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
                    ? $"{site.Domain}{path} now forwards to {input.Target?.Trim()}."
                    : $"The proxy route at {path} was saved.";
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
                ? $"The server-wide route at {ProxyRouteRules.NormalizePath(path)} applies to {site.Domain} again."
                : $"The server-wide route at {ProxyRouteRules.NormalizePath(path)} is off for {site.Domain}.";

        return RedirectToPage(null, null, new { domain = site.Domain }, "proxy");
    }

    public async Task<IActionResult> OnPostRemoveProxyRouteAsync(string domain, string? path)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        if (await _proxy.RemoveRouteAsync(site, path, User.Identity?.Name ?? "unknown"))
            TempData["StatusMessage"] = $"The proxy route at {ProxyRouteRules.NormalizePath(path)} was removed from {site.Domain}.";
        else
            TempData["ErrorMessage"] = $"{site.Domain} has no proxy route at {ProxyRouteRules.NormalizePath(path)}.";

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
            TempData["ErrorMessage"] = $"{site.Domain} has no proxy route at {normalized}.";
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
            TempData["StatusMessage"] = $"{name} was left as it was: an empty box keeps a secret's value.";
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

        TempData["StatusMessage"] = $"{name} saved for {site.Domain}.";
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
            TempData["ErrorMessage"] = error ?? $"{site.Domain} has no value of its own for {name}.";
        else if (definition is null)
            TempData["StatusMessage"] = $"{name} was removed from {site.Domain}.";
        else if (definition.Default is not null)
            TempData["StatusMessage"] = $"{name} uses the release default again.";
        else
            TempData["StatusMessage"] = $"{name} no longer has a value: the release gives it no default.";

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
                ? $"{site.Domain} now chats through {provider?.Name}. Its [AiAccess] hook, {hook}, decides which visitors may use it."
                : allowVisitors
                    ? $"{site.Domain} now chats through {provider?.Name}, and every visitor can use it."
                    : $"{site.Domain} now chats through {provider?.Name}, for its functions only.";

        return RedirectToPage(new { domain = site.Domain });
    }

    /// <summary>Clears the AI settings, so neither the site's functions nor its visitors can chat.</summary>
    public async Task<IActionResult> OnPostRemoveAiAsync(string domain)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        if (await _aiSettings.RemoveAsync(site, User.Identity?.Name ?? "unknown"))
            TempData["StatusMessage"] = $"{site.Domain} no longer has AI.";

        return RedirectToPage(new { domain = site.Domain });
    }

    public async Task<IActionResult> OnPostFunctionsAsync(string domain, List<IFormFile> files, string? mode, CancellationToken ct)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        var (uploaded, readError) = await FunctionUploads.ReadAsync(files, ct);
        var result = uploaded is null
            ? FunctionDeployResult.Failed(readError!)
            : await _functionDeployer.DeployAsync(site.Domain, uploaded, User.Identity?.Name ?? "unknown", "web", replace: mode == "replace", ct);

        if (result.Ok)
        {
            TempData["StatusMessage"] = FunctionUploads.Describe(result, site.Domain);
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
            TempData["StatusMessage"] = $"Removed {fileName}. " + FunctionUploads.Describe(result, site.Domain);
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
        if (ok) TempData["StatusMessage"] = $"{site.Domain} no longer runs any functions. Earlier releases keep theirs.";
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
            ? $"{from} is now {to}. {from} no longer serves anything, and visitors have to enter the passcode again."
            : $"{from} is now {to}. {from} no longer serves anything.";

        return RedirectToPage(new { domain = to });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string domain)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        await _deployer.DeleteAsync(site.Domain, User.Identity?.Name ?? "unknown");

        TempData["StatusMessage"] = $"{site.Domain} and all of its content were removed.";
        return RedirectToPage("Index");
    }

    private SiteRecord? Resolve(string domain)
    {
        var (normalized, _) = SiteStore.NormalizeDomain(domain);
        if (normalized is null)
        {
            TempData["ErrorMessage"] = "That is not a valid domain name.";
            return null;
        }

        var site = _sites.TryGet(normalized);
        if (site is null) TempData["ErrorMessage"] = $"No site is published at '{normalized}'.";
        return site;
    }
}
