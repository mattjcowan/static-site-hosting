using StaticSiteHost.Configuration;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <summary>
/// A site's other domains (<see cref="SiteRecord.Aliases"/>): more names it answers at, beside its
/// domain. Each is a domain, or <c>*.</c> and a domain, which answers every name one level under it,
/// as a wildcard certificate covers: <c>*.app.example.com</c> answers <c>abc.app.example.com</c>, but
/// not <c>x.abc.app.example.com</c> and not <c>app.example.com</c>. So every name an alias answers can
/// have a certificate.
///
/// A request goes to the site at its domain, else to the site that has it as an alias, else to the
/// site with a wildcard over it (<see cref="SiteStore.Resolve"/>). A site of its own therefore keeps
/// its name under another site's wildcard.
/// </summary>
public static class SiteAliases
{
    /// <summary>The most other domains one site may have. A wildcard answers many names at once.</summary>
    public const int MaxPerSite = 50;

    public static bool IsWildcard(string alias) => alias.StartsWith("*.", StringComparison.Ordinal);

    /// <summary>Whether an alias answers a name: the name itself, or for a wildcard, a name one level under it.</summary>
    public static bool Covers(string alias, string name)
    {
        if (alias.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        if (!IsWildcard(alias)) return false;

        var dot = name.IndexOf('.');
        return dot > 0 && name[(dot + 1)..].Equals(alias[2..], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An alias as it is stored: the domain as <see cref="SiteStore.NormalizeDomain"/> makes it, with
    /// <c>*.</c> kept in front of a wildcard's. A wildcard needs two labels or more after it, so
    /// <c>*.com</c> is refused. Null and why, when it cannot be one.
    /// </summary>
    public static (string? Alias, string? Error) Normalize(string? input)
    {
        var value = input?.Trim() ?? "";
        var wildcard = IsWildcard(value);
        var (domain, error) = SiteStore.NormalizeDomain(wildcard ? value[2..] : value);
        if (domain is null) return (null, ServerText.Get("sites.error.aliasInvalid", new { value, error }));
        if (wildcard && !domain.Contains('.'))
            return (null, ServerText.Get("sites.error.aliasTooBroad", new { value }));

        return (wildcard ? "*." + domain : domain, null);
    }

    /// <summary>
    /// The other domains a site may have, normalized, in the order given and without repeats; or every
    /// reason they cannot be. <paramref name="managementHost"/> is the name the management app is being
    /// used at, which no alias may take while no management hosts are set: the management app then
    /// answers at any name that has no site, and the administrator would be locked out.
    /// </summary>
    public static (List<string> Aliases, List<string> Errors) Check(
        SiteRecord site, IEnumerable<string?> inputs, SiteStore sites, SiteHostingOptions options, string? managementHost)
    {
        var aliases = new List<string>();
        var errors = new List<string>();
        managementHost = managementHost?.TrimEnd('.').ToLowerInvariant();

        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input)) continue;

            var (alias, error) = Normalize(input);
            if (alias is null)
            {
                errors.Add(error!);
                continue;
            }

            if (aliases.Contains(alias)) continue;

            var wildcard = IsWildcard(alias);
            if (alias == site.Domain)
                errors.Add(ServerText.Get("sites.error.aliasOwnDomain", new { alias }));
            else if (!wildcard && sites.Exists(alias))
                errors.Add(ServerText.Get("sites.error.aliasOtherSite", new { alias }));
            // An exact alias may take a name from under another site's wildcard: the exact name wins.
            else if (sites.AliasOwner(alias) is { } owner && owner.Domain != site.Domain && (wildcard || !owner.Wildcard))
                errors.Add(ServerText.Get("sites.error.aliasTaken", new { alias, site = owner.Domain }));
            // A wildcard may cover a management host: management hosts are matched before any site.
            else if (!wildcard && options.IsManagementHost(alias))
                errors.Add(ServerText.Get("sites.error.aliasManagement", new { alias }));
            else if (!options.HasManagementHosts && managementHost is { Length: > 0 } && Covers(alias, managementHost))
                errors.Add(ServerText.Get("sites.error.aliasTakesManagement", new { alias, host = managementHost }));
            else
                aliases.Add(alias);
        }

        if (aliases.Count > MaxPerSite)
            errors.Add(ServerText.Get("sites.error.aliasTooMany", new { max = MaxPerSite, count = aliases.Count }));

        return (aliases, errors);
    }

    /// <summary>
    /// Why a new site, or a site being renamed, may not take a name another site's alias answers;
    /// null when it may. Another site's exact alias is never taken. A name under another site's
    /// wildcard may be, since the exact name wins, but only by an administrator: visitors at a name
    /// that already answers would get another site.
    /// </summary>
    /// <param name="self">The site being renamed, whose own aliases it may take; null for a new site.</param>
    public static string? Taken(SiteStore sites, string name, bool administrator, string? self = null)
    {
        if (sites.AliasOwner(name) is not { } owner || owner.Domain == self) return null;

        if (!owner.Wildcard)
            return ServerText.Get("sites.error.nameIsAlias", new { name, site = owner.Domain });

        return administrator
            ? null
            : ServerText.Get("sites.error.nameUnderWildcard", new { name, site = owner.Domain, wildcard = "*." + name[(name.IndexOf('.') + 1)..] });
    }
}
