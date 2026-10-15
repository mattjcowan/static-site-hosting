using System.Reflection;

namespace StaticSiteHost.Tools;

/// <summary>
/// The README, built into the server so <c>read_docs</c> can hand it to an agent wherever the
/// server runs, the container included, where the repository is not. Split at its <c>##</c> and
/// <c>###</c> headings.
/// </summary>
public static class Manual
{
    public sealed record Section(string Title, string Text);

    private static readonly Lazy<IReadOnlyList<Section>> Loaded = new(Load);

    public static IReadOnlyList<Section> Sections => Loaded.Value;

    private static IReadOnlyList<Section> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("README.md");
        if (stream is null) return [];

        using var reader = new StreamReader(stream);
        var sections = new List<Section>();
        string? title = null;
        var text = new System.Text.StringBuilder();
        var inCode = false;

        foreach (var line in reader.ReadToEnd().Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal)) inCode = !inCode;

            var heading = !inCode && (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("### ", StringComparison.Ordinal));
            if (heading)
            {
                if (title is not null) sections.Add(new Section(title, text.ToString().Trim()));
                title = line.TrimStart('#').Trim();
                text.Clear();
                continue;
            }

            if (title is not null) text.AppendLine(line.TrimEnd('\r'));
        }

        if (title is not null) sections.Add(new Section(title, text.ToString().Trim()));
        return sections;
    }
}
