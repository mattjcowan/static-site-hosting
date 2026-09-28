using System.Diagnostics.CodeAnalysis;

namespace StaticSiteHost.Functions.Testing;

/// <summary>
/// An <see cref="ISiteVariables"/> holding whatever you <see cref="Set"/>, for
/// <see cref="FakeSite.Variables"/>.
/// </summary>
public sealed class FakeSiteVariables : ISiteVariables
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _public = new(StringComparer.Ordinal);

    /// <summary>Starts empty.</summary>
    public FakeSiteVariables() => All = _values.AsReadOnly();

    /// <summary>Adds a variable or replaces its value, and whether it is public.</summary>
    /// <returns>This instance, so calls can be chained.</returns>
    public FakeSiteVariables Set(string name, string value, bool isPublic = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        _values[name] = value;
        if (isPublic) _public.Add(name);
        else _public.Remove(name);

        return this;
    }

    /// <inheritdoc />
    public string? this[string name] => _values.GetValueOrDefault(name);

    /// <inheritdoc />
    public bool TryGet(string name, [MaybeNullWhen(false)] out string value) => _values.TryGetValue(name, out value);

    /// <inheritdoc />
    public string Get(string name, string fallback = "") =>
        _values.TryGetValue(name, out var value) && value.Length > 0 ? value : fallback;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> All { get; }

    /// <inheritdoc />
    public bool IsPublic(string name) => _public.Contains(name);
}
