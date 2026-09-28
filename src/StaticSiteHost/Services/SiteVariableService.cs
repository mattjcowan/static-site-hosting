using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <summary>One variable as the site page and the API show it.</summary>
/// <param name="HasValue">True when the value, once <c>${env:…}</c> is expanded, is not empty.</param>
/// <param name="Value">
/// The value in force as it was saved, with <c>${env:…}</c> left as written so the page never
/// shows what the server's environment holds. Null for a secret, and when there is no value.
/// </param>
/// <param name="Default">The release's default. Null for a secret, and when there is none.</param>
/// <param name="Source">
/// <see cref="SiteVariableService.SourceSite"/>, <see cref="SiteVariableService.SourceDefault"/> or
/// <see cref="SiteVariableService.SourceNone"/>.
/// </param>
/// <param name="UnsetEnvironment">Environment variables the value names that the server does not have.</param>
/// <param name="IsAdministratorsOnly">
/// Only an administrator may set, change or remove it: it is a secret, or the value the site holds
/// reads the server's environment. See <see cref="SiteVariableService"/>.
/// </param>
public sealed record VariableView(
    string Name,
    string Description,
    bool IsPublic,
    bool IsSecret,
    bool IsRequired,
    bool IsDeclared,
    bool HasValue,
    string? Value,
    string? Default,
    string Source,
    IReadOnlyList<string> UnsetEnvironment,
    bool IsAdministratorsOnly = false);

/// <summary>
/// A site's variables as a request sees them: secrets in the clear and <c>${env:…}</c> expanded.
/// Names are case-sensitive, like environment variables on Linux. The dictionaries are frozen,
/// so a function that casts one back to a mutable type still cannot change what the next
/// request sees.
/// </summary>
/// <param name="All">Every variable, secrets included. What functions get.</param>
/// <param name="Public">The public ones. What the browser gets.</param>
/// <param name="Missing">Required variables whose value is empty.</param>
/// <param name="UnsetEnvironment">Environment variables some value names that the server does not have.</param>
public sealed record ResolvedVariables(
    IReadOnlyDictionary<string, string> All,
    IReadOnlyDictionary<string, string> Public,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> UnsetEnvironment);

/// <summary>
/// Everything about a site's variables: the names a release declares in <c>_variables.json</c>,
/// the values the site sets, and what a request finally sees. The site page, the API, deploys and
/// the request pipeline all come through here, so they validate, protect and resolve alike.
///
/// A variable's value is the site's if it set one (even an empty one), else the release's
/// default. <c>${env:NAME}</c> anywhere in it is replaced by that environment variable when it is
/// read; an unset one becomes empty and is reported. Secrets are stored with Data Protection behind
/// a <c>dp:</c> prefix, so site.json never holds one, and are never shown again, only whether one
/// is set.
///
/// Secrets, and values that read the server's environment with <c>${env:…}</c>, are
/// administrators' to set, change and remove; members, who can do everything else here, set and
/// remove plain values only. A secret is usually a credential an administrator was trusted with,
/// and a member who could overwrite it could swap it for one of their own; the environment holds
/// the server's own secrets (the same line <c>_functions/</c> draws). So a member cannot save a
/// variable that is secret, by the release's declaration, by what is stored or by asking, nor a
/// value that reads the environment, nor change or remove a value the site holds that is either;
/// and a member's whole-list replace leaves every such value exactly as it is, silently, so the
/// list only ever touches plain values.
///
/// Anyone who can deploy can declare a name public or secret, so the release may make a value more
/// private than the caller asked, never less: it is secret if the release or the caller says so,
/// and public only if the release says so and it is not secret. A value saved as a secret stays
/// secret until it is saved again, and one saved in the clear is encrypted once a deploy or a
/// rollback makes its declaration secret (<see cref="ProtectDeclaredSecretsAsync"/>). A value that
/// reads the environment is saved public only when whoever saved it asked for that in so many
/// words, and otherwise never reaches the browser, whatever a later release declares.
///
/// Resolution is cached per domain. Each entry remembers the value list and the release
/// definitions it was built from, and both are replaced rather than edited on every change, so an
/// entry that a racing save made stale is rebuilt on the next read. <see cref="Evict"/> is how
/// callers say a domain changed.
/// </summary>
public sealed partial class SiteVariableService
{
    /// <summary>The most variables a release may declare, and the most a site may set.</summary>
    public const int MaxVariables = 200;

    /// <summary>Longest value or default, in UTF-8 bytes.</summary>
    public const int MaxValueBytes = 8 * 1024;

    public const int MaxDescriptionLength = 500;

    /// <summary>The site set the value.</summary>
    public const string SourceSite = "site";

    /// <summary>The site set nothing, so the release's default applies.</summary>
    public const string SourceDefault = "default";

    /// <summary>Neither the site nor the release gives a value.</summary>
    public const string SourceNone = "none";

    private const string ProtectorPurpose = "StaticSiteHost.SiteVariables";

    /// <summary>Marks protected text, so a plain value is never handed to Unprotect by mistake.</summary>
    private const string ProtectedPrefix = "dp:";

    /// <summary>A resolution, with the lists it was built from so a stale one can be recognised.</summary>
    private sealed record CacheEntry(
        List<SiteVariable>? Values,
        List<VariableDefinition>? Definitions,
        ResolvedVariables Resolved,
        IReadOnlyDictionary<string, string[]> UnsetByName);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Setting one variable is a read-modify-write of the site's list; two at once must not lose one.
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private readonly IDataProtector _protector;
    private readonly SiteStore _sites;
    private readonly AuditLog _audit;
    private readonly ILogger<SiteVariableService> _logger;

    public SiteVariableService(
        IDataProtectionProvider protection,
        SiteStore sites,
        AuditLog audit,
        ILogger<SiteVariableService> logger)
    {
        _protector = protection.CreateProtector(ProtectorPurpose);
        _sites = sites;
        _audit = audit;
        _logger = logger;
    }

    // \z rather than $, which would also accept a name with a newline after it.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}\z")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex EnvironmentReference();

    /// <summary>Why a variable name is refused, or null when it is fine.</summary>
    public static string? CheckName(string? name) =>
        string.IsNullOrEmpty(name) ? "A variable name is required."
        : NamePattern().IsMatch(name) ? null
        : $"'{name}' is not a valid variable name. Use letters, digits and underscores, starting with a " +
          "letter or an underscore, up to 64 characters.";

    /// <summary>Whether a value or default is within <see cref="MaxValueBytes"/>.</summary>
    public static bool IsValueTooLong(string? value) =>
        value is not null && Encoding.UTF8.GetByteCount(value) > MaxValueBytes;

    /// <summary>Whether text names an environment variable with <c>${env:…}</c>.</summary>
    public static bool ReferencesEnvironment(string? text) =>
        !string.IsNullOrEmpty(text) && EnvironmentReference().IsMatch(text);

    // ---- reading -------------------------------------------------------------

    /// <summary>What requests see for the site. Cached; see the class remarks.</summary>
    public ResolvedVariables Resolve(SiteRecord site) => Entry(site).Resolved;

    /// <summary>
    /// One row per variable the live release declares, in the order it declares them, then one
    /// per variable only the site holds. For a declared variable the release decides the
    /// description, whether it is required, and (subject to the rules in the class remarks) who
    /// may see it; for the others the site's own flags do.
    /// </summary>
    public IReadOnlyList<VariableView> Describe(SiteRecord site)
    {
        var entry = Entry(site);
        var rows = new List<VariableView>();

        foreach (var (name, definition, value) in Entries(entry.Definitions, entry.Values))
        {
            var (isPublic, isSecret) = Visibility(definition, value);
            var source = value is not null ? SourceSite
                : definition?.Default is not null ? SourceDefault
                : SourceNone;

            rows.Add(new VariableView(
                name,
                definition?.Description ?? "",
                isPublic,
                isSecret,
                IsRequired: definition?.Required == true,
                IsDeclared: definition is not null,
                HasValue: entry.Resolved.All.TryGetValue(name, out var resolved) && resolved.Length > 0,
                Value: isSecret ? null : value?.Value ?? definition?.Default,
                Default: isSecret ? null : definition?.Default,
                source,
                entry.UnsetByName.GetValueOrDefault(name) ?? [],
                IsAdministratorsOnly: isSecret || (value is not null && IsAdministratorsOnly(value, definition))));
        }

        return rows;
    }

    /// <summary>Forgets a domain's resolution. Call after anything that changes its values or its live release.</summary>
    public void Evict(string domain) => _cache.TryRemove(domain, out _);

    private CacheEntry Entry(SiteRecord site)
    {
        var values = site.Variables;
        var definitions = site.Current?.Variables;

        if (_cache.TryGetValue(site.Domain, out var cached) &&
            ReferenceEquals(cached.Values, values) &&
            ReferenceEquals(cached.Definitions, definitions))
        {
            return cached;
        }

        var built = Build(site.Domain, values, definitions);
        _cache[site.Domain] = built;
        return built;
    }

    private CacheEntry Build(string domain, List<SiteVariable>? values, List<VariableDefinition>? definitions)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        var visible = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        var unsetByName = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var (name, definition, value) in Entries(definitions, values))
        {
            var text = value is not null ? Reveal(domain, name, value.Value, value.Secret)
                : definition?.Default is { } fallback ? Reveal(domain, name, fallback, definition.Secret)
                : "";

            var unset = new List<string>();
            var expanded = Expand(text, unset);

            all[name] = expanded;
            if (Visibility(definition, value).Public) visible[name] = expanded;
            if (definition?.Required == true && expanded.Length == 0) missing.Add(name);
            unsetByName[name] = [.. unset];
        }

        var resolved = new ResolvedVariables(
            all.ToFrozenDictionary(StringComparer.Ordinal),
            visible.ToFrozenDictionary(StringComparer.Ordinal),
            missing,
            unsetByName.Values.SelectMany(names => names).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());

        return new CacheEntry(values, definitions, resolved, unsetByName);
    }

    /// <summary>The variables the live release declares, in its order, then the ones only the site holds.</summary>
    private static IEnumerable<(string Name, VariableDefinition? Definition, SiteVariable? Value)> Entries(
        List<VariableDefinition>? definitions, List<SiteVariable>? values)
    {
        var byName = new Dictionary<string, SiteVariable>(StringComparer.Ordinal);
        foreach (var value in values ?? []) byName.TryAdd(value.Name, value);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in definitions ?? [])
        {
            if (seen.Add(definition.Name)) yield return (definition.Name, definition, byName.GetValueOrDefault(definition.Name));
        }

        foreach (var value in values ?? [])
        {
            if (seen.Add(value.Name)) yield return (value.Name, null, value);
        }
    }

    /// <summary>
    /// Who may see a variable: the release decides for a declared one and the site for the rest,
    /// except that a value saved as a secret stays secret, and a value that reads the environment
    /// stays off the browser unless it was saved as public. See the class remarks for why.
    /// </summary>
    private static (bool Public, bool Secret) Visibility(VariableDefinition? definition, SiteVariable? value)
    {
        var secret = definition?.Secret == true || value?.Secret == true;
        var wantsPublic = definition?.Public ?? value?.Public ?? false;
        var heldBack = value is { Public: false } && ReferencesEnvironment(value.Value);

        return (!secret && wantsPublic && !heldBack, secret);
    }

    /// <summary>
    /// Replaces each <c>${env:NAME}</c> with that environment variable, collecting the names that
    /// are not set. One pass: text an environment variable brings in is not expanded again.
    /// </summary>
    private static string Expand(string text, List<string> unset) =>
        !text.Contains("${env:", StringComparison.Ordinal)
            ? text
            : EnvironmentReference().Replace(text, match =>
            {
                var name = match.Groups[1].Value;
                var value = Environment.GetEnvironmentVariable(name);
                if (value is null && !unset.Contains(name)) unset.Add(name);
                return value ?? "";
            });

    /// <summary>The plain text of a stored value. Only text saved as a secret, behind the prefix, is unprotected.</summary>
    private string Reveal(string domain, string name, string stored, bool secret)
    {
        if (!secret || !stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) return stored;

        try
        {
            return _protector.Unprotect(stored[ProtectedPrefix.Length..]);
        }
        catch (CryptographicException ex)
        {
            // The key ring was lost or replaced, so the value is gone. Say so, and read it as empty.
            _logger.LogWarning(ex, "The secret {Name} on {Domain} could not be decrypted and reads as empty. Set it again.",
                name, domain);
            return "";
        }
    }

    private string Protect(string value) => ProtectedPrefix + _protector.Protect(value);

    // ---- writing -------------------------------------------------------------

    /// <summary>
    /// Sets one variable. An empty value is stored as such: it is the site's answer, and it
    /// overrides the release's default. A null value keeps the one the site holds, a secret's
    /// included, so the flags alone can be changed. A flag left null keeps what the variable had,
    /// or false for a new one; for a declared variable the release has its say too (see the class
    /// remarks and <see cref="Flags"/>).
    /// </summary>
    /// <param name="isAdministrator">Whether the caller is an administrator, who alone may touch secrets and <c>${env:…}</c>.</param>
    public async Task<(bool Ok, string? Error)> SetAsync(
        SiteRecord site, string name, string? value, bool? isPublic, bool? isSecret, string actor, bool isAdministrator)
    {
        name = name.Trim();

        await _writeGate.WaitAsync();
        try
        {
            var existing = site.Variables.FirstOrDefault(v => v.Name == name);

            if (value is null)
            {
                if (existing is null)
                    return (false, $"{name} has no value set here to keep. Send a value, even an empty one.");

                value = Reveal(site.Domain, name, existing.Value, existing.Secret);
            }

            var error = TryPrepare(site, name, value, isPublic, isSecret, existing, actor, isAdministrator, out var stored);
            if (error is not null) return (false, error);

            if (existing is null && site.Variables.Count >= MaxVariables)
                return (false, $"A site can hold at most {MaxVariables} variables. Remove one first.");

            site.Variables = existing is null
                ? [.. site.Variables, stored!]
                : site.Variables.Select(v => ReferenceEquals(v, existing) ? stored! : v).ToList();

            await _sites.SaveAsync(site);
        }
        finally
        {
            _writeGate.Release();
        }

        Evict(site.Domain);
        await _audit.WriteAsync("site.variables.set", actor, new { domain = site.Domain, name });
        return (true, null);
    }

    /// <summary>
    /// Drops the site's value, so a declared variable falls back to its default. Removed is false,
    /// with no error, when the site held no value by that name, and false with the reason when this
    /// caller may not remove it (see the class remarks).
    /// </summary>
    /// <param name="isAdministrator">Whether the caller is an administrator, who alone may remove secrets and values that read the environment.</param>
    public async Task<(bool Removed, string? Error)> RemoveAsync(SiteRecord site, string name, string actor, bool isAdministrator)
    {
        name = name.Trim();

        await _writeGate.WaitAsync();
        try
        {
            if (site.Variables.FirstOrDefault(v => v.Name == name) is not { } existing) return (false, null);

            if (!isAdministrator && IsAdministratorsOnly(existing, DefinitionOf(site, name)))
                return (false, HeldByAdministrators(name));

            site.Variables = site.Variables.Where(v => v.Name != name).ToList();
            await _sites.SaveAsync(site);
        }
        finally
        {
            _writeGate.Release();
        }

        Evict(site.Domain);
        await _audit.WriteAsync("site.variables.remove", actor, new { domain = site.Domain, name });
        return (true, null);
    }

    /// <summary>
    /// Replaces every value the site holds. Values arrive in the clear and secrets are protected on
    /// the way in. Nothing is saved unless every one of them is acceptable. For a member, every
    /// secret and every value that reads the environment the site holds is kept exactly as it is,
    /// whether the list names it or not, and the list replaces only the rest (see the class remarks).
    /// </summary>
    /// <param name="isAdministrator">Whether the caller is an administrator, who alone may touch secrets and <c>${env:…}</c>.</param>
    public async Task<(bool Ok, IReadOnlyList<string> Errors)> ReplaceAsync(
        SiteRecord site, IReadOnlyList<SiteVariable> variables, string actor, bool isAdministrator)
    {
        if (variables.Count > MaxVariables)
            return (false, [$"A site can hold at most {MaxVariables} variables; this is {variables.Count}."]);

        await _writeGate.WaitAsync();
        try
        {
            var errors = new List<string>();
            var replacement = new List<SiteVariable>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // What a member's list cannot touch, kept as it is.
            var held = isAdministrator
                ? []
                : site.Variables.Where(v => IsAdministratorsOnly(v, DefinitionOf(site, v.Name))).ToList();
            var heldNames = held.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);

            for (var i = 0; i < variables.Count; i++)
            {
                var candidate = variables[i];
                var name = candidate.Name?.Trim() ?? "";

                if (name.Length > 0 && !seen.Add(name))
                {
                    errors.Add($"Variable {i + 1}: {name} appears more than once.");
                    continue;
                }

                if (heldNames.Contains(name)) continue;

                var error = TryPrepare(site, name, candidate.Value, candidate.Public, candidate.Secret,
                    site.Variables.FirstOrDefault(v => v.Name == name), actor, isAdministrator, out var stored);

                if (error is not null) errors.Add($"Variable {i + 1}: {error}");
                else replacement.Add(stored!);
            }

            if (errors.Count > 0) return (false, errors);

            if (replacement.Count + held.Count > MaxVariables)
            {
                return (false, [$"A site can hold at most {MaxVariables} variables; with the {held.Count} only an " +
                                $"administrator can change, this would be {replacement.Count + held.Count}."]);
            }

            site.Variables = [.. replacement, .. held];
            await _sites.SaveAsync(site);
        }
        finally
        {
            _writeGate.Release();
        }

        Evict(site.Domain);
        await _audit.WriteAsync("site.variables.replace", actor, new { domain = site.Domain, variables = variables.Count });
        return (true, []);
    }

    /// <summary>Checks one value and builds what is stored for it, or says why it cannot be stored.</summary>
    /// <param name="requestedPublic">The caller's flag as sent: null when it said nothing.</param>
    /// <param name="requestedSecret">The caller's flag as sent: null when it said nothing.</param>
    /// <param name="existing">The value the site holds under the name now, if any.</param>
    private string? TryPrepare(
        SiteRecord site, string name, string? value, bool? requestedPublic, bool? requestedSecret, SiteVariable? existing,
        string actor, bool isAdministrator, out SiteVariable? stored)
    {
        stored = null;
        value ??= "";

        if (CheckName(name) is { } nameError) return nameError;
        if (IsValueTooLong(value)) return $"{name}: a value can be at most {MaxValueBytes / 1024} KB.";

        var (isPublic, isSecret, error) = Flags(
            name, DefinitionOf(site, name), existing, value, requestedPublic, requestedSecret, isAdministrator);
        if (error is not null) return error;

        stored = new SiteVariable
        {
            Name = name,
            Value = isSecret ? Protect(value) : value,
            Public = isPublic,
            Secret = isSecret,
            SetUtc = DateTimeOffset.UtcNow,
            SetBy = actor
        };
        return null;
    }

    /// <summary>
    /// The rules of the class remarks, for one value about to be saved: whether it is stored public
    /// and secret, or why this caller may not save it.
    /// </summary>
    /// <param name="definition">The live release's declaration of the name, if it declares it.</param>
    /// <param name="existing">The value the site holds under the name now, if any.</param>
    /// <param name="value">The new value, in the clear.</param>
    /// <param name="requestedPublic">The caller's flag as sent: null when it said nothing, which keeps what <paramref name="existing"/> had.</param>
    /// <param name="requestedSecret">The same, for secret.</param>
    public static (bool Public, bool Secret, string? Error) Flags(
        string name, VariableDefinition? definition, SiteVariable? existing, string value,
        bool? requestedPublic, bool? requestedSecret, bool isAdministrator)
    {
        var askedSecret = requestedSecret ?? existing?.Secret ?? false;
        var askedPublic = requestedPublic ?? existing?.Public ?? false;

        bool isPublic, isSecret;
        if (definition is not null)
        {
            // The release can make a value more private than the caller asked, never less.
            isSecret = definition.Secret || askedSecret;
            isPublic = definition.Public && !isSecret;
        }
        else
        {
            if (askedPublic && askedSecret)
                return (false, false, $"{name} cannot be both public and secret: a secret never reaches the browser.");

            (isPublic, isSecret) = (askedPublic, askedSecret);
        }

        var readsEnvironment = ReferencesEnvironment(value);

        if (!isAdministrator)
        {
            if (existing is not null && IsAdministratorsOnly(existing, definition)) return (false, false, HeldByAdministrators(name));

            if (isSecret)
            {
                return (false, false,
                    $"{name} is a secret, and only administrators can set, change or remove secrets: they are usually " +
                    "credentials an administrator was trusted with. Ask an administrator to set it.");
            }

            if (readsEnvironment)
            {
                return (false, false,
                    $"{name}: only administrators can use ${{env:…}}, because the server's environment holds its " +
                    "own secrets. Ask an administrator to set this value.");
            }
        }

        // What reads the environment reaches the browser only when whoever saved it said so in so many words.
        if (readsEnvironment && requestedPublic != true) isPublic = false;

        return (isPublic, isSecret, null);
    }

    /// <summary>
    /// True for a value the site holds that only an administrator may change or remove: a secret, by
    /// how it was stored or by the release's declaration, or a value that reads the environment.
    /// </summary>
    public static bool IsAdministratorsOnly(SiteVariable stored, VariableDefinition? definition) =>
        stored.Secret || definition?.Secret == true || ReferencesEnvironment(stored.Value);

    private static string HeldByAdministrators(string name) =>
        $"{name} holds a value only an administrator can change or remove: it is a secret, or it reads the server's " +
        "environment. Ask an administrator.";

    private static VariableDefinition? DefinitionOf(SiteRecord site, string name) =>
        site.Current?.Variables.FirstOrDefault(d => d.Name == name);

    /// <summary>
    /// After a deploy or a rollback: encrypts every value the site saved in the clear whose name the
    /// live release now declares secret, so site.json never holds it in the clear. It is a secret
    /// from then on, like any value saved as one, whatever a later release declares. The caller saves
    /// the site.
    /// </summary>
    public async Task ProtectDeclaredSecretsAsync(SiteRecord site)
    {
        var secrets = site.Current?.Variables.Where(d => d.Secret).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        if (secrets is not { Count: > 0 }) return;

        List<string> protectedNames;
        await _writeGate.WaitAsync();
        try
        {
            protectedNames = [.. site.Variables.Where(v => !v.Secret && secrets.Contains(v.Name)).Select(v => v.Name)];
            if (protectedNames.Count == 0) return;

            site.Variables = site.Variables
                .Select(v => v.Secret || !secrets.Contains(v.Name)
                    ? v
                    : new SiteVariable
                    {
                        Name = v.Name,
                        Value = Protect(v.Value),
                        Public = false,
                        Secret = true,
                        SetUtc = v.SetUtc,
                        SetBy = v.SetBy
                    })
                .ToList();
        }
        finally
        {
            _writeGate.Release();
        }

        Evict(site.Domain);
        _logger.LogInformation("Encrypted {Names} on {Domain}: the live release declares them secret, and they were saved in the clear",
            string.Join(", ", protectedNames), site.Domain);
    }

    /// <summary>
    /// Readies the definitions a deploy read from <c>_variables.json</c> for the release record. A
    /// secret's default is protected like any other secret, so neither site.json nor the API ever
    /// holds it in the clear. A default that reads the server's environment is kept only when an
    /// administrator deploys; otherwise it is dropped, with a warning saying so.
    /// </summary>
    /// <param name="isAdministrator">Whether an administrator is deploying, and so may write <c>${env:…}</c>.</param>
    public void PrepareForRelease(List<VariableDefinition> definitions, bool isAdministrator, List<string> warnings)
    {
        var dropped = new List<string>();

        foreach (var definition in definitions)
        {
            if (definition.Default is null) continue;

            if (!isAdministrator && ReferencesEnvironment(definition.Default))
            {
                definition.Default = null;
                dropped.Add(definition.Name);
                continue;
            }

            if (definition.Secret) definition.Default = Protect(definition.Default);
        }

        if (dropped.Count > 0)
        {
            warnings.Add(
                $"The default for {string.Join(", ", dropped)} was dropped: only an administrator's deploy can use " +
                "${env:…}, because the server's environment holds its own secrets. Set the value under Variables on " +
                "the site page, or ask an administrator to deploy.");
        }
    }
}
