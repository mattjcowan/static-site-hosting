using System.Diagnostics.CodeAnalysis;

namespace StaticSiteHost.Functions;

/// <summary>
/// A site's variables as its functions see them: every one, secrets included, each with its
/// effective value, which is the value set on the site or else the default the site's
/// <c>_variables.json</c> declares.
/// </summary>
/// <remarks>
/// Only Static Site Host implements this interface, and
/// <see cref="Testing.FakeSiteVariables"/> stands in for it outside the server. Later versions
/// may add members.
/// </remarks>
public interface ISiteVariables
{
    /// <summary>The value of the variable called <paramref name="name"/>, or null when there is none.</summary>
    string? this[string name] { get; }

    /// <summary>Reads the variable called <paramref name="name"/>.</summary>
    /// <returns>False when the site has no such variable, and <paramref name="value"/> is then null.</returns>
    bool TryGet(string name, [MaybeNullWhen(false)] out string value);

    /// <summary>
    /// The value of the variable called <paramref name="name"/>, or <paramref name="fallback"/>
    /// when there is no such variable or its value is empty.
    /// </summary>
    string Get(string name, string fallback = "");

    /// <summary>Every variable by name, secrets included. Take care what you send back to a browser.</summary>
    IReadOnlyDictionary<string, string> All { get; }

    /// <summary>
    /// True when the variable is public, which means browsers can read it as well. False for a
    /// private or secret variable, and for a name the site does not have.
    /// </summary>
    bool IsPublic(string name);
}
