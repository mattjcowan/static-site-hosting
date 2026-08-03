using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;

namespace StaticSiteHost.Services;

/// <summary>Single source of truth for the layout of the data volume.</summary>
public sealed class DataPaths
{
    public DataPaths(IOptions<SiteHostingOptions> options)
    {
        Root = Path.GetFullPath(options.Value.DataRoot);
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(SitesDir);
        Directory.CreateDirectory(TempDir);
    }

    public string Root { get; }

    public string ConfigDir => Path.Combine(Root, "config");
    public string SitesDir => Path.Combine(Root, "sites");
    public string TempDir => Path.Combine(Root, "tmp");

    public string UsersFile => Path.Combine(ConfigDir, "users.json");
    public string ApiKeysFile => Path.Combine(ConfigDir, "apikeys.json");
    public string AuditFile => Path.Combine(ConfigDir, "audit.log");
    public string BootstrapPasswordFile => Path.Combine(ConfigDir, "bootstrap-password.txt");
    public string DataProtectionDir => Path.Combine(ConfigDir, "keys");

    public string SiteDir(string domain) => Path.Combine(SitesDir, domain);
    public string SiteMetaFile(string domain) => Path.Combine(SiteDir(domain), "site.json");
    public string ReleasesDir(string domain) => Path.Combine(SiteDir(domain), "releases");
    public string ReleaseDir(string domain, string releaseId) => Path.Combine(ReleasesDir(domain), releaseId);
    public string StagingDir(string domain) => Path.Combine(SiteDir(domain), ".staging");
}
