using System.Xml.Linq;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

public sealed record FunctionReadResult(FunctionSource? Source, string? Error)
{
    public static FunctionReadResult Failed(string error) => new(null, error);
    public static FunctionReadResult Ok(FunctionSource source) => new(source, null);
}

/// <summary>
/// Reads an uploaded function file into a <see cref="FunctionSource"/>.
///
/// Two formats, one result. A <c>.cs</c> file is a .NET 10 file-based app whose leading
/// <c>#:</c> directives have to be stripped, because those directives are a compile error
/// (CS9298) in an ordinary project — the SDK only accepts them for <c>dotnet run file.cs</c>.
/// A <c>.linq</c> file carries the same information in an XML header instead.
///
/// Neither format is trusted to be well formed: administrators paste these by hand.
/// </summary>
public sealed class FunctionSourceReader
{
    /// <summary>A handler file is a page or two of code, not a data file.</summary>
    public const long MaxSourceBytes = 1024 * 1024;

    private const string QueryOpenTag = "<Query";
    private const string QueryCloseTag = "</Query>";

    private readonly ILogger<FunctionSourceReader> _logger;

    public FunctionSourceReader(ILogger<FunctionSourceReader> logger) => _logger = logger;

    /// <summary>
    /// Picks the parser from the file extension, falling back to sniffing for the
    /// <c>&lt;Query&gt;</c> element so a .linq renamed to .cs still works.
    /// </summary>
    public FunctionReadResult Read(string fileName, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return FunctionReadResult.Failed("The file is empty.");

        // A BOM would otherwise sit in front of '<Query' or '#:' and defeat both parsers.
        text = text.TrimStart('﻿');

        var extension = Path.GetExtension(fileName);
        var looksLikeQuery = text.TrimStart().StartsWith(QueryOpenTag, StringComparison.Ordinal);

        return extension.Equals(".linq", StringComparison.OrdinalIgnoreCase) || looksLikeQuery
            ? ReadLinqPadQuery(text)
            : ReadCSharpFile(text);
    }

    /// <summary>
    /// Strips the leading <c>#:</c> / <c>#!</c> directive block off a file-based app.
    ///
    /// Only the block before the first line of real code is examined, which is the same rule
    /// the SDK applies. That matters: a <c>#:package</c> sitting inside a string literal later
    /// in the file is code, not a directive, and must not be pulled out of it.
    /// </summary>
    private FunctionReadResult ReadCSharpFile(string text)
    {
        var packages = new List<FunctionPackage>();
        var properties = new List<KeyValuePair<string, string>>();
        var kept = new List<string>();
        var needsAspNet = false;
        var inHeader = true;
        var stripped = 0;

        foreach (var line in SplitLines(text))
        {
            var trimmed = line.TrimStart();

            if (inHeader && (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)))
            {
                kept.Add(line);
                continue;
            }

            if (inHeader && trimmed.StartsWith("#!", StringComparison.Ordinal))
            {
                stripped++;
                continue;
            }

            if (inHeader && trimmed.StartsWith("#:", StringComparison.Ordinal))
            {
                stripped++;

                var (directive, argument) = SplitDirective(trimmed);

                switch (directive)
                {
                    case "#:package":
                        if (TryParsePackage(argument, out var package)) packages.Add(package);
                        else return FunctionReadResult.Failed($"Could not read the package reference '{argument}'.");
                        break;

                    case "#:sdk":
                        // The generated project is a library, so the Web SDK becomes the
                        // framework reference that SDK would have added.
                        needsAspNet |= argument.Contains("Web", StringComparison.OrdinalIgnoreCase);
                        break;

                    case "#:property":
                        var split = argument.IndexOf('=');
                        if (split <= 0)
                            return FunctionReadResult.Failed($"Could not read the property '{argument}'. Expected name=value.");
                        properties.Add(new(argument[..split].Trim(), argument[(split + 1)..].Trim()));
                        break;

                    case "#:project":
                        return FunctionReadResult.Failed(
                            "#:project references a local project, which the server cannot resolve. " +
                            "Use #:package instead.");

                    default:
                        return FunctionReadResult.Failed($"Unsupported directive '{directive}'.");
                }

                continue;
            }

            inHeader = false;
            kept.Add(line);
        }

        // Handlers take an HttpContext, so the reference is needed whether or not the author
        // remembered the directive. Adding it costs nothing: it is already in the runtime.
        return FunctionReadResult.Ok(new FunctionSource(
            FunctionSourceKind.CSharpFile,
            string.Join('\n', kept),
            stripped,
            packages,
            [],
            properties,
            NeedsAspNetCore: true));
    }

    /// <summary>
    /// Splits a .linq file into its XML header and the C# after it.
    ///
    /// The body is left exactly as written. For <c>Kind="Program"</c> that body is class
    /// members — an <c>async Task Main()</c> sitting at namespace scope, which is not legal
    /// C# on its own. It compiles only because the template wraps the harness in
    /// <c>#if LINQPAD</c> and the server never defines that symbol, so the method disappears
    /// before the compiler sees it. A .linq file whose Main is unguarded will not build, and
    /// says so with the author's own line numbers.
    /// </summary>
    private FunctionReadResult ReadLinqPadQuery(string text)
    {
        var close = text.IndexOf(QueryCloseTag, StringComparison.Ordinal);

        // A self-closing '<Query ... />' carries no references, which is legal and means the
        // query needs nothing but the framework.
        var headerEnd = close >= 0
            ? close + QueryCloseTag.Length
            : FindSelfClosingHeaderEnd(text);

        if (headerEnd < 0)
            return FunctionReadResult.Failed("The <Query> header is not closed.");

        var header = text[..headerEnd];

        // Skip the newlines between the header and the first line of code, and measure the
        // offset from what was actually consumed rather than from the header's own length.
        // The number of blank lines after </Query> varies, so anything derived from the
        // header alone drifts by however many there were.
        var bodyStart = headerEnd;
        while (bodyStart < text.Length && text[bodyStart] is '\r' or '\n') bodyStart++;

        var body = text[bodyStart..];

        XElement query;
        try
        {
            query = XElement.Parse(header);
        }
        catch (System.Xml.XmlException ex)
        {
            _logger.LogDebug(ex, "Rejected a .linq upload with an unreadable header");
            return FunctionReadResult.Failed($"The <Query> header is not valid XML: {ex.Message}");
        }

        var kind = query.Attribute("Kind")?.Value;
        if (kind is not null && kind is not ("Program" or "Statements"))
        {
            return FunctionReadResult.Failed(
                $"A '{kind}' query cannot be hosted. Save it as a C# Program and put your " +
                "handlers in a Handlers class.");
        }

        // LINQPad records a package by name only, so the version floats. Pinning is the
        // author's job via the generated project if they need a specific one.
        var packages = query.Elements("NuGetReference")
            .Select(e => e.Value.Trim())
            .Where(name => name.Length > 0)
            .Select(name => new FunctionPackage(name, "*"))
            .ToList();

        var usings = query.Elements("Namespace")
            .Select(e => e.Value.Trim())
            .Where(ns => ns.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var headerLines = CountLines(text[..bodyStart]);

        return FunctionReadResult.Ok(new FunctionSource(
            FunctionSourceKind.LinqPadQuery,
            body,
            headerLines,
            packages,
            usings,
            [],
            // As with a .cs upload: a handler takes an HttpContext, so the reference is
            // required whether or not the author ticked IncludeAspNet in LINQPad.
            NeedsAspNetCore: true));
    }

    private static int FindSelfClosingHeaderEnd(string text)
    {
        var open = text.IndexOf(QueryOpenTag, StringComparison.Ordinal);
        if (open < 0) return -1;

        var end = text.IndexOf("/>", open, StringComparison.Ordinal);
        return end < 0 ? -1 : end + 2;
    }

    private static (string Directive, string Argument) SplitDirective(string line)
    {
        var space = line.IndexOfAny([' ', '\t']);
        return space < 0 ? (line.Trim(), "") : (line[..space], line[(space + 1)..].Trim());
    }

    /// <summary>Reads "Name@Version", "Name@*" or a bare "Name".</summary>
    private static bool TryParsePackage(string argument, out FunctionPackage package)
    {
        package = new FunctionPackage("", "*");
        if (argument.Length == 0) return false;

        var at = argument.LastIndexOf('@');

        var name = at > 0 ? argument[..at].Trim() : argument.Trim();
        var version = at > 0 ? argument[(at + 1)..].Trim() : "*";

        if (name.Length == 0 || version.Length == 0) return false;

        // The name lands in a csproj attribute and a NuGet request, so keep it to what a
        // package id can actually contain.
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return false;
        // Whitespace means something trailed the version, typically a comment, which the
        // SDK does not allow on a directive line; NuGet would reject the whole string later
        // with a far less obvious message.
        if (version.Any(c => char.IsWhiteSpace(c) || c is '"' or '<' or '>' or '&')) return false;

        package = new FunctionPackage(name, version);
        return true;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static int CountLines(string text) => text.Count(c => c == '\n');
}
