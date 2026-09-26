namespace StaticSiteHost.Models;

/// <summary>Which of the two authoring formats an upload arrived in.</summary>
public enum FunctionSourceKind
{
    /// <summary>A .NET 10 file-based app: plain C# with leading <c>#:</c> directives.</summary>
    CSharpFile,

    /// <summary>A LINQPad query: an XML <c>&lt;Query&gt;</c> header followed by plain C#.</summary>
    LinqPadQuery
}

/// <summary>A package the source asked for.</summary>
/// <param name="Version">"*" when the author pinned nothing, which LINQPad never does.</param>
public sealed record FunctionPackage(string Name, string Version);

/// <summary>
/// An uploaded function file, taken apart into the C# the compiler sees and the references
/// the project file needs.
///
/// Both formats carry the same information in different places, so reading them is the only
/// thing that differs between a .cs upload and a .linq upload — everything downstream works
/// off this record.
/// </summary>
public sealed record FunctionSource(
    FunctionSourceKind Kind,

    /// <summary>
    /// The C# to compile, with the format's header removed. Line numbers no longer match the
    /// author's file, which is what <see cref="LineOffset"/> is for.
    /// </summary>
    string Code,

    /// <summary>
    /// How many lines were stripped off the front. Added back to compiler diagnostics so an
    /// error points at the line the author is actually looking at.
    /// </summary>
    int LineOffset,

    IReadOnlyList<FunctionPackage> Packages,

    /// <summary>Namespaces to import globally, from a .linq file's &lt;Namespace&gt; entries.</summary>
    IReadOnlyList<string> Usings,

    /// <summary>MSBuild properties from <c>#:property</c> directives.</summary>
    IReadOnlyList<KeyValuePair<string, string>> Properties,

    /// <summary>
    /// True when the source wants the ASP.NET Core framework reference — <c>#:sdk
    /// Microsoft.NET.Sdk.Web</c> or <c>&lt;IncludeAspNet&gt;true&lt;/IncludeAspNet&gt;</c>.
    /// Handlers taking an HttpContext need it, so in practice it is always on.
    /// </summary>
    bool NeedsAspNetCore);

/// <summary>One function file as uploaded: the name picks the reader, the text is read as-is.</summary>
public sealed record FunctionFile(string Name, string Text);
