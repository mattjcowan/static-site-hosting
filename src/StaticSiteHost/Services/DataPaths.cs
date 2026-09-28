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

    /// <summary>The AI providers administrators have added. Their keys are stored protected.</summary>
    public string AiProvidersFile => Path.Combine(ConfigDir, "ai-providers.json");

    public string SiteDir(string domain) => Path.Combine(SitesDir, domain);
    public string SiteMetaFile(string domain) => Path.Combine(SiteDir(domain), "site.json");
    public string ReleasesDir(string domain) => Path.Combine(SiteDir(domain), "releases");
    public string ReleaseDir(string domain, string releaseId) => Path.Combine(ReleasesDir(domain), releaseId);
    public string StagingDir(string domain) => Path.Combine(SiteDir(domain), ".staging");

    // ---------------------------------------------------------------- functions
    //
    // Each upload is a bundle: "src" holds the file exactly as uploaded (what the page shows and
    // hands back for download), "build" the throwaway generated project, and "bin" the published
    // output that gets loaded. Bundles sit beside the releases rather than inside them, because a
    // release directory is served to visitors and source code must never be one rule away from
    // being downloadable.

    public string FunctionsDir(string domain) => Path.Combine(SiteDir(domain), "functions");

    public string FunctionBundleDir(string domain, string bundleId) => Path.Combine(FunctionsDir(domain), bundleId);

    /// <summary>
    /// Where a site's functions keep their own data: a database, uploads, anything that must
    /// outlive a deploy. Beside the releases, so it is never served and never replaced by a
    /// deploy or a rollback; inside the site's directory, so a rename moves it and a delete
    /// removes it along with everything else.
    /// </summary>
    public string SiteDataDir(string domain) => Path.Combine(SiteDir(domain), "data");

    /// <summary>The data directory of the global functions, shared by every site they answer for.</summary>
    public string GlobalFunctionsDataDir => Path.Combine(ConfigDir, "functions-data");

    /// <summary>Functions every site answers with. Same bundle layout as a site's.</summary>
    public string GlobalFunctionsDir => Path.Combine(ConfigDir, "functions");

    public string GlobalFunctionsFile => Path.Combine(ConfigDir, "functions.json");

    public string GlobalFunctionBundleDir(string bundleId) => Path.Combine(GlobalFunctionsDir, bundleId);

    public static string FunctionSourceDir(string bundleDir) => Path.Combine(bundleDir, "src");
    public static string FunctionBuildDir(string bundleDir) => Path.Combine(bundleDir, "build");
    public static string FunctionBinDir(string bundleDir) => Path.Combine(bundleDir, "bin");
}
