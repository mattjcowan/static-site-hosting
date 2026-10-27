using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <summary>
/// In-memory index of every hosted site, backed by one site.json per domain.
/// Request serving reads from the index, so a page view never touches the metadata files.
/// Requests find their site with <see cref="Resolve"/>, which knows the sites' other domains too.
/// </summary>
public sealed class SiteStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<string, SiteRecord> _sites = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// Every site's other domains, to the site's domain: exact ones by name, and wildcards by the name
    /// they cover the level under (<c>*.app.example.com</c> as <c>app.example.com</c>). Built again
    /// whole after every change and swapped in, so a request never sees half of one.
    /// </summary>
    private volatile AliasIndex _aliases = AliasIndex.Empty;
    private readonly Lock _indexLock = new();

    private sealed record AliasIndex(IReadOnlyDictionary<string, string> Exact, IReadOnlyDictionary<string, string> Wildcards)
    {
        public static readonly AliasIndex Empty = new(new Dictionary<string, string>(), new Dictionary<string, string>());
    }
    private readonly DataPaths _paths;
    private readonly ILogger<SiteStore> _logger;

    public SiteStore(DataPaths paths, ILogger<SiteStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Domains whose folder is on disk but whose site.json could not be read at the last load, so
    /// whatever follows the list of sites (the certificates) leaves their things alone.
    /// </summary>
    public IReadOnlySet<string> Unreadable => _unreadable;

    private HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);

    public void Load()
    {
        _sites.Clear();
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _unreadable = unreadable;
        if (!Directory.Exists(_paths.SitesDir)) return;

        foreach (var dir in Directory.EnumerateDirectories(_paths.SitesDir))
        {
            var domain = Path.GetFileName(dir);
            var metaFile = _paths.SiteMetaFile(domain);
            if (!File.Exists(metaFile)) continue;

            try
            {
                var record = JsonSerializer.Deserialize<SiteRecord>(File.ReadAllText(metaFile), SerializerOptions);
                if (record is null) continue;
                record.Domain = domain;
                _sites[domain] = record;
            }
            catch (JsonException ex)
            {
                unreadable.Add(domain);
                _logger.LogError(ex, "Skipping site {Domain}: site.json could not be parsed", domain);
            }
        }

        IndexAliases();
        _logger.LogInformation("Loaded {Count} hosted site(s) from {Dir}", _sites.Count, _paths.SitesDir);
    }

    public IReadOnlyList<SiteRecord> List() =>
        _sites.Values.OrderBy(s => s.Domain, StringComparer.OrdinalIgnoreCase).ToList();

    public SiteRecord? TryGet(string? domain) =>
        !string.IsNullOrEmpty(domain) && _sites.TryGetValue(domain, out var site) ? site : null;

    public bool Exists(string domain) => _sites.ContainsKey(domain);

    /// <summary>
    /// The site a request for a host goes to: the one at that domain, else the one with it as an
    /// other domain, else the one with a wildcard one level above it. The host is lowercase with no
    /// trailing dot, as <see cref="Serving.SiteHostingMiddleware"/> makes it.
    /// </summary>
    public SiteRecord? Resolve(string? host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        if (_sites.TryGetValue(host, out var site)) return site;
        return AliasOwner(host) is { } owner ? TryGet(owner.Domain) : null;
    }

    /// <summary>
    /// The domain of the site whose other domain answers a name, sites' own domains aside, and whether
    /// that is a wildcard: an exact one first, then a wildcard one level above the name. For a
    /// wildcard itself (<c>*.app.example.com</c>), the site that has that same wildcard.
    /// </summary>
    public (string Domain, bool Wildcard)? AliasOwner(string name)
    {
        var aliases = _aliases;
        if (aliases.Exact.TryGetValue(name, out var domain)) return (domain, false);

        var dot = name.IndexOf('.');
        return dot > 0 && aliases.Wildcards.TryGetValue(name[(dot + 1)..], out domain) ? (domain, true) : null;
    }

    private void IndexAliases()
    {
        lock (_indexLock)
        {
            var exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var wildcards = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // In domain order, so if a site.json edited by hand gives two sites the same name, the same one wins every time.
            foreach (var site in _sites.Values.OrderBy(s => s.Domain, StringComparer.Ordinal))
            {
                foreach (var alias in site.Aliases)
                {
                    if (SiteAliases.IsWildcard(alias)) wildcards.TryAdd(alias[2..], site.Domain);
                    else exact.TryAdd(alias, site.Domain);
                }
            }

            _aliases = new AliasIndex(exact, wildcards);
        }
    }

    /// <summary>Absolute path of the directory currently being served for a domain, if any.</summary>
    public string? GetCurrentReleasePath(string domain)
    {
        var site = TryGet(domain);
        if (site?.CurrentRelease is not { } release) return null;

        var dir = _paths.ReleaseDir(site.Domain, release);
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// Raised after a site is saved, renamed or deleted, for whatever follows the list of sites
    /// (the certificates). Handlers must be quick and must not throw.
    /// </summary>
    public event Action? Changed;

    public async Task SaveAsync(SiteRecord site)
    {
        await _writeGate.WaitAsync();
        try
        {
            await WriteMetaAsync(site);
            _sites[site.Domain] = site;
            IndexAliases();
        }
        finally
        {
            _writeGate.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Moves a site to another domain: its whole directory — every release, the rules and
    /// the passcode — is renamed in one step on the same volume, so nothing is copied and
    /// there is no moment where half the site lives under each name. Both domains must
    /// already be normalized; the caller holds the deploy gate so no release lands mid-move.
    /// </summary>
    public async Task<(bool Ok, string? Error)> RenameAsync(string from, string to)
    {
        await _writeGate.WaitAsync();
        try
        {
            if (!_sites.TryGetValue(from, out var site)) return (false, ServerText.Get("sites.error.noSite", new { domain = from }));
            if (_sites.ContainsKey(to)) return (false, ServerText.Get("sites.error.exists", new { domain = to }));

            var target = _paths.SiteDir(to);
            if (Directory.Exists(target))
            {
                return (false, ServerText.Get("sites.error.leftoverFiles", new { domain = to, path = target }));
            }

            try
            {
                Directory.Move(_paths.SiteDir(from), target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not move {From} to {To}", from, to);
                return (false, ServerText.Get("sites.error.moveFailed"));
            }

            site.Domain = to;
            _sites[to] = site;
            _sites.TryRemove(from, out _);
            IndexAliases();

            // The directory name is what Load trusts, so the move above is what counts; this
            // only brings the file's copy of the domain and its timestamp up to date.
            try
            {
                await WriteMetaAsync(site);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Moved {From} to {To} but could not rewrite site.json", from, to);
            }

            Changed?.Invoke();
            return (true, null);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WriteMetaAsync(SiteRecord site)
    {
        site.UpdatedUtc = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(_paths.SiteDir(site.Domain));

        var file = _paths.SiteMetaFile(site.Domain);
        var temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(site, SerializerOptions));
        File.Move(temp, file, overwrite: true);
    }

    public async Task<bool> DeleteAsync(string domain)
    {
        if (!_sites.TryRemove(domain, out _)) return false;
        IndexAliases();
        Changed?.Invoke();

        await _writeGate.WaitAsync();
        try
        {
            var dir = _paths.SiteDir(domain);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not fully remove the directory for {Domain}", domain);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ---- domain handling -------------------------------------------------

    /// <summary>
    /// Accepts what a person is likely to paste — "https://abc.def.com/", "ABC.DEF.COM:8080",
    /// a unicode domain — and returns the canonical lowercase (punycode) hostname.
    /// </summary>
    public static (string? Domain, string? Error) NormalizeDomain(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return (null, ServerText.Get("sites.error.domainRequired"));

        var value = input.Trim();

        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0) value = value[(schemeIndex + 3)..];

        var slashIndex = value.IndexOf('/');
        if (slashIndex >= 0) value = value[..slashIndex];

        var atIndex = value.IndexOf('@');
        if (atIndex >= 0) value = value[(atIndex + 1)..];

        var colonIndex = value.LastIndexOf(':');
        if (colonIndex > 0) value = value[..colonIndex];

        value = value.TrimEnd('.').Trim();
        if (value.Length == 0) return (null, ServerText.Get("sites.error.domainRequired"));

        try
        {
            value = new IdnMapping { AllowUnassigned = false, UseStd3AsciiRules = true }.GetAscii(value);
        }
        catch (ArgumentException)
        {
            return (null, ServerText.Get("sites.error.domainInvalid"));
        }

        value = value.ToLowerInvariant();

        if (value.Length > 253) return (null, ServerText.Get("sites.error.domainLong"));

        foreach (var label in value.Split('.'))
        {
            if (label.Length is 0 or > 63)
                return (null, ServerText.Get("sites.error.domainPartLength"));
            if (label[0] == '-' || label[^1] == '-')
                return (null, ServerText.Get("sites.error.domainHyphen"));
            foreach (var c in label)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
                    return (null, ServerText.Get("sites.error.domainCharacters"));
            }
        }

        return (value, null);
    }
}
