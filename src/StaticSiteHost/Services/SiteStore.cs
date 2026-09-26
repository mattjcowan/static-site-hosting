using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <summary>
/// In-memory index of every hosted site, backed by one site.json per domain.
/// Request serving reads from the index, so a page view never touches the metadata files.
/// </summary>
public sealed class SiteStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<string, SiteRecord> _sites = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly DataPaths _paths;
    private readonly ILogger<SiteStore> _logger;

    public SiteStore(DataPaths paths, ILogger<SiteStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public void Load()
    {
        _sites.Clear();
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
                _logger.LogError(ex, "Skipping site {Domain}: site.json could not be parsed", domain);
            }
        }

        _logger.LogInformation("Loaded {Count} hosted site(s) from {Dir}", _sites.Count, _paths.SitesDir);
    }

    public IReadOnlyList<SiteRecord> List() =>
        _sites.Values.OrderBy(s => s.Domain, StringComparer.OrdinalIgnoreCase).ToList();

    public SiteRecord? TryGet(string? domain) =>
        !string.IsNullOrEmpty(domain) && _sites.TryGetValue(domain, out var site) ? site : null;

    public bool Exists(string domain) => _sites.ContainsKey(domain);

    /// <summary>Absolute path of the directory currently being served for a domain, if any.</summary>
    public string? GetCurrentReleasePath(string domain)
    {
        var site = TryGet(domain);
        if (site?.CurrentRelease is not { } release) return null;

        var dir = _paths.ReleaseDir(site.Domain, release);
        return Directory.Exists(dir) ? dir : null;
    }

    public async Task SaveAsync(SiteRecord site)
    {
        await _writeGate.WaitAsync();
        try
        {
            await WriteMetaAsync(site);
            _sites[site.Domain] = site;
        }
        finally
        {
            _writeGate.Release();
        }
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
            if (!_sites.TryGetValue(from, out var site)) return (false, $"No site is published at '{from}'.");
            if (_sites.ContainsKey(to)) return (false, $"A site is already published at '{to}'.");

            var target = _paths.SiteDir(to);
            if (Directory.Exists(target))
            {
                return (false,
                    $"'{to}' still has files on disk from an earlier site. Remove {target} and try again.");
            }

            try
            {
                Directory.Move(_paths.SiteDir(from), target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not move {From} to {To}", from, to);
                return (false, "The site's files could not be moved. Nothing was changed.");
            }

            site.Domain = to;
            _sites[to] = site;
            _sites.TryRemove(from, out _);

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
        if (string.IsNullOrWhiteSpace(input)) return (null, "A domain is required.");

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
        if (value.Length == 0) return (null, "A domain is required.");

        try
        {
            value = new IdnMapping { AllowUnassigned = false, UseStd3AsciiRules = true }.GetAscii(value);
        }
        catch (ArgumentException)
        {
            return (null, "That is not a valid domain name.");
        }

        value = value.ToLowerInvariant();

        if (value.Length > 253) return (null, "Domain names must be 253 characters or fewer.");

        foreach (var label in value.Split('.'))
        {
            if (label.Length is 0 or > 63)
                return (null, "Each part of a domain name must be between 1 and 63 characters.");
            if (label[0] == '-' || label[^1] == '-')
                return (null, "Parts of a domain name cannot start or end with a hyphen.");
            foreach (var c in label)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
                    return (null, "Domain names may only contain letters, digits, hyphens and dots.");
            }
        }

        return (value, null);
    }
}
