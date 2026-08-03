namespace StaticSiteHost.Serving;

public static class PathHelpers
{
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> or sits underneath it.</summary>
    public static bool IsInside(string root, string candidate)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        candidate = Path.GetFullPath(candidate);

        return candidate.Equals(root, Comparison)
               || candidate.StartsWith(root + Path.DirectorySeparatorChar, Comparison);
    }
}
