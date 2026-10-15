using System.Collections.Frozen;

namespace StaticSiteHost.Services;

/// <summary>
/// Runs one request against a site's functions, or the global ones: the live build, or a draft
/// from a check. Shared by the function editor and the API, so a test means the same thing in
/// both. See <see cref="FunctionTestRunner"/> for how the request runs.
/// </summary>
public sealed class FunctionTestService
{
    private static readonly ResolvedVariables NoVariables = new(
        FrozenDictionary<string, string>.Empty, FrozenDictionary<string, string>.Empty, [], []);

    private readonly FunctionDeploymentService _functions;
    private readonly FunctionTestRunner _runner;
    private readonly SiteStore _sites;
    private readonly SiteVariableService _variables;

    public FunctionTestService(
        FunctionDeploymentService functions, FunctionTestRunner runner, SiteStore sites, SiteVariableService variables)
    {
        _functions = functions;
        _runner = runner;
        _sites = sites;
        _variables = variables;
    }

    /// <param name="domain">The site whose functions run, or null for the global ones.</param>
    /// <param name="services">The calling request's services, for what a handler resolves from the host.</param>
    public async Task<FunctionTestResult> RunAsync(
        string? domain, FunctionTestRequest request, IServiceProvider services, CancellationToken ct)
    {
        var binDir = await _functions.ResolveBuildAsync(domain, request.Target);
        if (binDir is null)
        {
            return new FunctionTestResult(0, null, [], [], 0, false, false,
                request.Target is null or "live"
                    ? "Nothing is live to test. Check the files, then test the checked build."
                    : "That checked build has expired. Check the files again.");
        }

        var host = domain ?? (string.IsNullOrWhiteSpace(request.Host) ? "localhost" : request.Host.Trim());

        // The variables a live request to that host would carry: global functions answer for
        // whichever site the host names, and for a host with no site there are none.
        var variables = _sites.TryGet(SiteStore.NormalizeDomain(host).Domain) is { } site
            ? _variables.Resolve(site)
            : NoVariables;

        return await _runner.RunAsync(binDir, host, _functions.DataDirectory(domain), variables, request, services, ct);
    }
}
