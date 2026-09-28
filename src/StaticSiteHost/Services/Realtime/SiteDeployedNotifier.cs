using System.Text.Json;

namespace StaticSiteHost.Services.Realtime;

/// <summary>
/// Tells the pages open on a site that a new version of it is live. After a deploy, a rollback,
/// or the site's functions being deployed or removed, every connection to the site receives
/// <see cref="EventName"/> with <c>{ "release": "…", "functions": "…" or null, "source": "deploy" |
/// "rollback" | "functions" }</c>: the live release, the live functions' bundle, and what changed.
/// A page can then offer to reload, with <c>site.realtime.on('site.deployed', …)</c>.
///
/// Fire and forget. The event goes out on a task of its own, without the caller's ExecutionContext,
/// and a failure is logged as a warning and goes no further: telling the pages is a courtesy, and
/// must never fail a deploy, nor hold one up while SignalR writes to every page the site has open.
/// A site with no pages open costs nothing.
/// </summary>
public sealed class SiteDeployedNotifier
{
    /// <summary>The event every connection to the site receives.</summary>
    public const string EventName = "site.deployed";

    /// <summary>A content deploy went live, with or without functions of its own.</summary>
    public const string FromDeploy = "deploy";

    /// <summary>An earlier release was made live again.</summary>
    public const string FromRollback = "rollback";

    /// <summary>The site's functions were deployed or removed, and its content is unchanged.</summary>
    public const string FromFunctions = "functions";

    private readonly SiteStore _sites;
    private readonly SiteRealtimeFactory _realtime;
    private readonly RealtimeRegistry _registry;
    private readonly ILogger<SiteDeployedNotifier> _logger;

    public SiteDeployedNotifier(
        SiteStore sites, SiteRealtimeFactory realtime, RealtimeRegistry registry, ILogger<SiteDeployedNotifier> logger)
    {
        _sites = sites;
        _realtime = realtime;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Sends <see cref="EventName"/> to every page open on <paramref name="domain"/>, describing the
    /// site as it is now, and returns at once. Never throws.
    /// </summary>
    /// <param name="source"><see cref="FromDeploy"/>, <see cref="FromRollback"/> or <see cref="FromFunctions"/>.</param>
    public void Announce(string domain, string source)
    {
        try
        {
            if (_registry.ConnectionCount(domain) == 0 || _sites.TryGet(domain) is not { } site) return;

            var payload = JsonSerializer.SerializeToElement(new
            {
                release = site.CurrentRelease,
                functions = site.Current?.Functions,
                source
            });

            var realtime = _realtime.Create(site);
            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await realtime.PublishAsync(EventName, payload);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not tell the pages open on {Domain} that a new version is live", domain);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tell the pages open on {Domain} that a new version is live", domain);
        }
    }
}
