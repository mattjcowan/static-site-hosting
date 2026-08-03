namespace StaticSiteHost.Models;

public static class Roles
{
    public const string Administrator = "Administrator";
    public const string Member = "Member";

    public static readonly string[] All = [Administrator, Member];

    public static bool IsValid(string? role) => role is Administrator or Member;
}
