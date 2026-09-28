using System.Text.Json;
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

    private const string KeyList = "default, description, public, secret or required";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

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
                ? $"Line {line + 1}: this is not valid JSON near character {ex.BytePositionInLine + 1}."
                : "This is not valid JSON.");
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problems.Add("The file must be a JSON object keyed by variable name, such as { \"SITE_TITLE\": \"My site\" }.");
                return false;
            }

            var count = root.EnumerateObject().Count();
            if (count > SiteVariableService.MaxVariables)
            {
                problems.Add($"A release can declare at most {SiteVariableService.MaxVariables} variables; this declares {count}.");
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
                    problems.Add($"{name}: declared more than once.");
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
                        problems.Add($"{name}: '{setting.Name}' is given more than once.");
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
                            problems.Add($"{name}: unknown key '{setting.Name}'. Use {KeyList}.");
                            break;
                    }
                }

                break;

            default:
                problems.Add($"{name}: expected a string (the default) or an object, found {Describe(value.ValueKind)}." +
                             (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                                 ? " Put the value in quotes."
                                 : ""));
                return null;
        }

        if (definition.Public && definition.Secret)
            problems.Add($"{name}: cannot be both public and secret, because a secret never reaches the browser.");

        if (definition.Description.Length > SiteVariableService.MaxDescriptionLength)
            problems.Add($"{name}: the description is longer than {SiteVariableService.MaxDescriptionLength} characters.");

        if (SiteVariableService.IsValueTooLong(definition.Default))
            problems.Add($"{name}: the default is larger than {SiteVariableService.MaxValueBytes / 1024} KB.");

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
                problems.Add($"{name}: '{setting.Name}' must be a string, found {Describe(setting.Value.ValueKind)}." +
                             (setting.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                                 ? " Put it in quotes."
                                 : ""));
                return null;
        }
    }

    private static bool ReadBoolean(string name, JsonProperty setting, List<string> problems)
    {
        if (setting.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) return setting.Value.GetBoolean();

        problems.Add($"{name}: '{setting.Name}' must be true or false, found {Describe(setting.Value.ValueKind)}.");
        return false;
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "true or false",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "a list",
        JsonValueKind.Object => "an object",
        JsonValueKind.String => "a string",
        _ => "nothing"
    };
}
