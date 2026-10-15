using System.Diagnostics;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;

namespace StaticSiteHost.Services;

/// <summary>What <see cref="SiteProxyService.TestAsync"/> found.</summary>
/// <param name="Url">The address that was asked, <c>${env:…}</c> expanded.</param>
/// <param name="Status">The status the target answered with, or null when it did not answer.</param>
/// <param name="Version">The HTTP version the answer came over.</param>
/// <param name="Error">Why there was no answer.</param>
public sealed record ProxyTestResult(string? Url, int? Status, string? Version, TimeSpan Elapsed, string? Error)
{
    public bool Answered => Status is not null;

    public string Describe() => Answered
        ? $"GET {Url} answered {Status} in {Elapsed.TotalMilliseconds:0} ms over HTTP/{Version}."
        : Url is null ? $"No request was sent: {Error}" : $"GET {Url} failed after {Elapsed.TotalMilliseconds:0} ms: {Error}";
}

/// <summary>
/// Validates and stores a site's proxy routes, and tests them. Shared by the site page and the API
/// so both reject the same things and leave the same trail. Callers check that the person is an
/// administrator: a route makes the server send requests wherever it points.
/// </summary>
public sealed class SiteProxyService
{
    /// <summary>The longest a test waits, whatever the route's own timeout.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private readonly SiteStore _sites;
    private readonly ProxyInvokers _invokers;
    private readonly AuditLog _audit;

    /// <summary>
    /// One change at a time, so two people saving different routes at once both keep theirs: each
    /// change reads the list, edits a copy and saves it.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SiteProxyService(SiteStore sites, ProxyInvokers invokers, AuditLog audit)
    {
        _sites = sites;
        _invokers = invokers;
        _audit = audit;
    }

    /// <summary>Replaces every route the site has.</summary>
    public async Task<(bool Ok, IReadOnlyList<string> Errors)> SetRoutesAsync(SiteRecord site, IReadOnlyList<ProxyRoute> routes, string actor)
    {
        await _gate.WaitAsync();
        try
        {
            return await SaveAsync(site, routes.Select(Normalize).ToList(), actor);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds a route, or replaces the one at <paramref name="originalPath"/>, which may be given a
    /// new path at the same time.
    /// </summary>
    public async Task<(bool Ok, IReadOnlyList<string> Errors)> SaveRouteAsync(
        SiteRecord site, string? originalPath, ProxyRoute route, string actor)
    {
        await _gate.WaitAsync();
        try
        {
            var routes = site.Proxy.ToList();
            var normalized = Normalize(route);

            var index = originalPath is null
                ? -1
                : routes.FindIndex(r => r.Path.Equals(ProxyRouteRules.NormalizePath(originalPath), StringComparison.OrdinalIgnoreCase));

            if (index >= 0) routes[index] = normalized;
            else routes.Add(normalized);

            return await SaveAsync(site, routes, actor);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes the route at a path. False when the site has none there.</summary>
    public async Task<bool> RemoveRouteAsync(SiteRecord site, string? path, string actor)
    {
        await _gate.WaitAsync();
        try
        {
            var normalized = ProxyRouteRules.NormalizePath(path);
            var routes = site.Proxy.Where(r => !r.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase)).ToList();
            if (routes.Count == site.Proxy.Count) return false;

            await SaveAsync(site, routes, actor);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(bool Ok, IReadOnlyList<string> Errors)> SaveAsync(SiteRecord site, List<ProxyRoute> routes, string actor)
    {
        var errors = ProxyRouteRules.Validate(site.Domain, routes);
        if (errors.Count > 0) return (false, errors);

        // A new list, never the old one edited, so SiteProxy compiles it afresh (see SiteRecord.Proxy).
        site.Proxy = routes.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
        await _sites.SaveAsync(site);

        await _audit.WriteAsync("site.proxy", actor, new
        {
            domain = site.Domain,
            routes = site.Proxy.Select(r => new { path = r.Path, target = r.Target, enabled = r.Enabled })
        });
        return (true, []);
    }

    /// <summary>
    /// Why a route could not be saved as the only route of a new site at <paramref name="domain"/>;
    /// empty when it could. For checking before the site is created.
    /// </summary>
    public static IReadOnlyList<string> CheckNewSiteRoute(string domain, ProxyRoute route) =>
        ProxyRouteRules.Validate(domain, [Normalize(route)]);

    /// <summary>A copy of a route in the form it is stored: path normalized, text trimmed, nulls filled in.</summary>
    private static ProxyRoute Normalize(ProxyRoute route)
    {
        var cookieMode = (route.CookieMode ?? ProxyCookieModes.Host).Trim().ToLowerInvariant();

        return new ProxyRoute
        {
            Path = ProxyRouteRules.NormalizePath(route.Path),
            Target = (route.Target ?? "").Trim(),
            StripPath = route.StripPath,
            Enabled = route.Enabled,
            WebSockets = route.WebSockets,
            CrossSiteWebSockets = route.CrossSiteWebSockets,
            Streaming = route.Streaming,
            HttpVersion = (route.HttpVersion ?? ProxyHttpVersions.Auto).Trim().ToLowerInvariant(),
            TimeoutSeconds = route.TimeoutSeconds,
            MaxRequestBodyBytes = route.MaxRequestBodyBytes is > 0 ? route.MaxRequestBodyBytes : null,
            KeepHost = route.KeepHost,
            ForwardedHeaders = route.ForwardedHeaders,
            RequestHeaders = (route.RequestHeaders ?? [])
                .Select(h => new ProxyHeader { Name = (h?.Name ?? "").Trim(), Value = (h?.Value ?? "").Trim() })
                .ToList(),
            CookieMode = cookieMode,
            CookieParentDomain = cookieMode == ProxyCookieModes.Parent
                ? SiteStore.NormalizeDomain(route.CookieParentDomain).Domain ?? route.CookieParentDomain?.Trim()
                : null,
            RewriteRedirects = route.RewriteRedirects,
            FilesFirst = route.FilesFirst,
            AcceptAnyCertificate = route.AcceptAnyCertificate
        };
    }

    /// <summary>
    /// Sends a GET to the route's target from the server, the way a request on the route would go:
    /// same client, certificate check, HTTP version and request headers. For the person setting the
    /// route up, so a mistyped container name shows at once rather than on a visitor's request.
    /// </summary>
    /// <param name="limit">The longest to wait, when shorter than the route's timeout and <see cref="TestTimeout"/>.</param>
    public async Task<ProxyTestResult> TestAsync(ProxyRoute route, CancellationToken ct, TimeSpan? limit = null)
    {
        var (target, problem) = ProxyRouteRules.ResolveTarget(route.Target);
        if (target is null) return new ProxyTestResult(null, null, null, TimeSpan.Zero, problem);

        var url = target.AbsoluteUri;
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        (request.Version, request.VersionPolicy) = route.HttpVersion switch
        {
            ProxyHttpVersions.Http11 => (System.Net.HttpVersion.Version11, HttpVersionPolicy.RequestVersionOrLower),
            ProxyHttpVersions.Http2 => (System.Net.HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact),
            _ => (System.Net.HttpVersion.Version20, HttpVersionPolicy.RequestVersionOrLower)
        };

        var unset = new List<string>();
        foreach (var header in route.RequestHeaders ?? [])
        {
            var value = SiteVariableService.Expand(header.Value, unset);
            if (value.Length > 0) request.Headers.TryAddWithoutValidation(header.Name, value);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var wait = TimeSpan.FromSeconds(Math.Min(route.TimeoutSeconds, TestTimeout.TotalSeconds));
        timeout.CancelAfter(limit is { } cap && cap < wait ? cap : wait);

        var clock = Stopwatch.StartNew();
        try
        {
            using var response = await _invokers.For(route.AcceptAnyCertificate).SendAsync(request, timeout.Token);
            return new ProxyTestResult(url, (int)response.StatusCode, response.Version.ToString(), clock.Elapsed, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProxyTestResult(url, null, null, clock.Elapsed, "no answer before the timeout");
        }
        catch (HttpRequestException ex)
        {
            return new ProxyTestResult(url, null, null, clock.Elapsed, Messages(ex));
        }
    }

    /// <summary>An exception's message and its causes', which is where "connection refused" usually is.</summary>
    private static string Messages(Exception ex)
    {
        var messages = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (!messages.Contains(current.Message)) messages.Add(current.Message);
        }

        return string.Join(" ", messages);
    }
}
