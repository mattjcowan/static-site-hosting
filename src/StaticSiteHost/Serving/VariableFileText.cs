using System.Text.Json;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Services;

namespace StaticSiteHost.Serving;

/// <summary>
/// The <c>_variables.json</c> file at the root of a deployed archive, which declares the variables
/// a build expects. A JSON object keyed by variable name; each value is either the default, as a
/// string, or an object saying more:
///
/// <code>
/// {
///   "SITE_TITLE": "My site",
///   "SUPPORT_EMAIL": { "description": "Shown in the footer", "public": true, "required": true },
///   "STRIPE_KEY": { "secret": true }
/// }
/// </code>
///
/// Inside a variable's object the keys are <c>default</c>, <c>description</c>, <c>public</c>,
/// <c>secret</c> and <c>required</c>, in any case. Comments and trailing commas are tolerated,
/// since the file is written by hand.
/// </summary>
public static class VariableFileText
{
    /// <summary>Name of the file read out of the root of a deployed archive.</summary>
    public const string FileName = "_variables.json";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Declarations written back as the file, for a release's download. A secret's default is left
    /// out: it is stored encrypted, and a download is no place for it. Deploying the file again
    /// declares the secret with no default, and the value the site holds still applies.
    /// </summary>
    public static string Format(IEnumerable<VariableDefinition> definitions)
    {
        var root = new System.Text.Json.Nodes.JsonObject();
        foreach (var d in definitions)
        {
            var entry = new System.Text.Json.Nodes.JsonObject();
            if (d.Description.Length > 0) entry["description"] = d.Description;
            if (d.Default is not null && !d.Secret) entry["default"] = d.Default;
            if (d.Public) entry["public"] = true;
            if (d.Secret) entry["secret"] = true;
            if (d.Required) entry["required"] = true;
            root[d.Name] = entry;
        }

        return root.Count == 0 ? "" : root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    public static bool TryParse(string? text, out List<VariableDefinition> definitions, out IReadOnlyList<string> errors)
    {
        definitions = [];
        var problems = new List<string>();
        errors = problems;

        if (string.IsNullOrWhiteSpace(text)) return true;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, DocumentOptions);
        }
        catch (JsonException ex)
        {
            problems.Add(ex.LineNumber is { } line
                ? ServerText.Get("site.variables.error.notJsonAt", new { line = line + 1, character = ex.BytePositionInLine + 1 })
                : ServerText.Get("site.variables.error.notJson"));
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problems.Add(ServerText.Get("site.variables.error.notObject"));
                return false;
            }

            var count = root.EnumerateObject().Count();
            if (count > SiteVariableService.MaxVariables)
            {
                problems.Add(ServerText.Get("site.variables.error.declaresTooMany", new { max = SiteVariableService.MaxVariables, count }));
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in root.EnumerateObject())
            {
                var name = property.Name;

                if (SiteVariableService.CheckName(name) is { } nameError)
                {
                    problems.Add(nameError);
                    continue;
                }

                if (!seen.Add(name))
                {
                    problems.Add(ServerText.Get("site.variables.error.declaredTwice", new { name }));
                    continue;
                }

                if (TryReadDefinition(name, property.Value, problems) is { } definition) definitions.Add(definition);
            }
        }

        return problems.Count == 0;
    }

    /// <summary>One variable's entry, or null after adding the reasons it is wrong to <paramref name="problems"/>.</summary>
    private static VariableDefinition? TryReadDefinition(string name, JsonElement value, List<string> problems)
    {
        var definition = new VariableDefinition { Name = name };
        var before = problems.Count;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                definition.Default = value.GetString();
                break;

            case JsonValueKind.Object:
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var setting in value.EnumerateObject())
                {
                    if (!keys.Add(setting.Name))
                    {
                        problems.Add(ServerText.Get("site.variables.error.keyTwice", new { name, key = setting.Name }));
                        continue;
                    }

                    switch (setting.Name.ToLowerInvariant())
                    {
                        case "default":
                            definition.Default = ReadString(name, setting, problems);
                            break;
                        case "description":
                            definition.Description = ReadString(name, setting, problems) ?? "";
                            break;
                        case "public":
                            definition.Public = ReadBoolean(name, setting, problems);
                            break;
                        case "secret":
                            definition.Secret = ReadBoolean(name, setting, problems);
                            break;
                        case "required":
                            definition.Required = ReadBoolean(name, setting, problems);
                            break;
                        default:
                            problems.Add(ServerText.Get("site.variables.error.unknownKey", new { name, key = setting.Name }));
                            break;
                    }
                }

                break;

            default:
                problems.Add(ServerText.Get(value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    ? "site.variables.error.entryKindQuote"
                    : "site.variables.error.entryKind", new { name, found = Describe(value.ValueKind) }));
                return null;
        }

        if (definition.Public && definition.Secret)
            problems.Add(ServerText.Get("site.variables.error.declaredPublicAndSecret", new { name }));

        if (definition.Description.Length > SiteVariableService.MaxDescriptionLength)
            problems.Add(ServerText.Get("site.variables.error.descriptionLong", new { name, max = SiteVariableService.MaxDescriptionLength }));

        if (SiteVariableService.IsValueTooLong(definition.Default))
            problems.Add(ServerText.Get("site.variables.error.defaultLong", new { name, size = SiteVariableService.MaxValueBytes / 1024 }));

        return problems.Count == before ? definition : null;
    }

    private static string? ReadString(string name, JsonProperty setting, List<string> problems)
    {
        switch (setting.Value.ValueKind)
        {
            case JsonValueKind.String:
                return setting.Value.GetString();
            case JsonValueKind.Null:
                return null;
            default:
                problems.Add(ServerText.Get(setting.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    ? "site.variables.error.keyStringQuote"
                    : "site.variables.error.keyString", new { name, key = setting.Name, found = Describe(setting.Value.ValueKind) }));
                return null;
        }
    }

    private static bool ReadBoolean(string name, JsonProperty setting, List<string> problems)
    {
        if (setting.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) return setting.Value.GetBoolean();

        problems.Add(ServerText.Get("site.variables.error.keyBoolean", new { name, key = setting.Name, found = Describe(setting.Value.ValueKind) }));
        return false;
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Number => ServerText.Get("site.variables.kind.number"),
        JsonValueKind.True or JsonValueKind.False => ServerText.Get("site.variables.kind.boolean"),
        JsonValueKind.Null => ServerText.Get("site.variables.kind.null"),
        JsonValueKind.Array => ServerText.Get("site.variables.kind.list"),
        JsonValueKind.Object => ServerText.Get("site.variables.kind.object"),
        JsonValueKind.String => ServerText.Get("site.variables.kind.string"),
        _ => ServerText.Get("site.variables.kind.nothing")
    };
}
