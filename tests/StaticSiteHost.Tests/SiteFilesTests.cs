using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;

namespace StaticSiteHost.Tests;

public sealed class SiteFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ssh-files-tests-" + Guid.NewGuid().ToString("n"));
    private readonly DataPaths _paths;
    private readonly SiteFiles _files;
    private readonly SiteRecord _site;
    private readonly ReleaseRecord _release;

    public SiteFilesTests()
    {
        _paths = new DataPaths(Options.Create(new SiteHostingOptions { DataRoot = _root }));
        _files = new SiteFiles(_paths);
        _release = new ReleaseRecord
        {
            Id = "r1",
            Headers = [new HeaderRule { For = "/assets/**", Set = { ["Cache-Control"] = "no-cache" } }],
            Redirects = [new RedirectRule { From = "/old", To = "/new", Status = 301 }],
            Variables =
            [
                new VariableDefinition { Name = "SITE_TITLE", Default = "Hello", Public = true, Description = "The title" },
                new VariableDefinition { Name = "API_KEY", Default = "dp:encrypted-secret", Secret = true, Required = true }
            ]
        };
        _site = new SiteRecord { Domain = "files.test", CurrentRelease = "r1", Releases = [_release] };

        var dir = Directory.CreateDirectory(_paths.ReleaseDir("files.test", "r1")).FullName;
        File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>Hi</h1>");
        Directory.CreateDirectory(Path.Combine(dir, "css"));
        File.WriteAllText(Path.Combine(dir, "css", "site.css"), "body{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Lists_the_served_files_and_the_configuration_written_back_as_files()
    {
        var list = _files.List(_site, _release);

        Assert.Equal(["_headers", "_redirects", "_variables.json", "css/site.css", "index.html"], list.Select(f => f.Path));
        Assert.True(list[0].Configuration);
        Assert.False(list[^1].Configuration);
        Assert.Equal(11, list.Single(f => f.Path == "index.html").Size);
    }

    [Fact]
    public async Task Reads_a_file_or_a_configuration_file_and_refuses_a_path_outside_the_release()
    {
        Assert.Equal("body{}", Encoding.UTF8.GetString((await _files.ReadAsync(_site, _release, "/css/site.css", default))!));
        Assert.Contains("/old", Encoding.UTF8.GetString((await _files.ReadAsync(_site, _release, "_redirects", default))!));
        Assert.Null(await _files.ReadAsync(_site, _release, "missing.html", default));
        Assert.Null(await _files.ReadAsync(_site, _release, "../../config/users.json", default));
    }

    [Fact]
    public async Task A_download_deploys_back_as_it_was_but_never_carries_a_secret_default()
    {
        await using var zipFile = await _files.CreateZipAsync(_site, _release, default);
        using var zip = new ZipArchive(zipFile, ZipArchiveMode.Read);

        Assert.Equal(["_headers", "_redirects", "_variables.json", "css/site.css", "index.html"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));

        using var reader = new StreamReader(zip.GetEntry("_variables.json")!.Open());
        var variables = await reader.ReadToEndAsync();
        Assert.DoesNotContain("encrypted-secret", variables);
        Assert.DoesNotContain("dp:", variables);

        // What it says parses back to the same declarations, less the secret's default.
        Assert.True(VariableFileText.TryParse(variables, out var parsed, out _));
        var title = parsed.Single(v => v.Name == "SITE_TITLE");
        Assert.Equal(("Hello", true, "The title"), (title.Default, title.Public, title.Description));
        var key = parsed.Single(v => v.Name == "API_KEY");
        Assert.Equal((null, true, true), (key.Default, key.Secret, key.Required));

        Assert.True(HeaderRuleText.TryParse(new StreamReader(zip.GetEntry("_headers")!.Open()).ReadToEnd(), out var headers, out _));
        Assert.Equal("/assets/**", Assert.Single(headers).For);
    }

    [Fact]
    public void The_live_release_is_the_default_and_an_unknown_one_is_none()
    {
        Assert.Same(_release, SiteFiles.ReleaseOf(_site, null));
        Assert.Same(_release, SiteFiles.ReleaseOf(_site, "current"));
        Assert.Same(_release, SiteFiles.ReleaseOf(_site, "r1"));
        Assert.Null(SiteFiles.ReleaseOf(_site, "r9"));
    }
}
