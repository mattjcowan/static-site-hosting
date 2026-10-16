using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;

namespace StaticSiteHost.Tests;

/// <summary>
/// Changing a release's files where they are, as the Files tab, PATCH …/files and the file tools do:
/// what is written, refused, moved and copied, and what the release's record says afterwards.
/// </summary>
public sealed class ReleaseEditorTests : IDisposable
{
    private const string Domain = "edit.test";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ssh-edit-tests-" + Guid.NewGuid().ToString("n"));
    private readonly DataPaths _paths;
    private readonly ReleaseEditor _editor;
    private readonly SiteRecord _site;

    public ReleaseEditorTests()
    {
        var options = Options.Create(new SiteHostingOptions { DataRoot = _root, MaxEntryBytes = 1024, MaxExtractedBytes = 4096 });
        _paths = new DataPaths(options);
        var sites = new SiteStore(_paths, NullLogger<SiteStore>.Instance);
        var variables = new SiteVariableService(new EphemeralDataProtectionProvider(), sites, new AuditLog(_paths, NullLogger<AuditLog>.Instance),
            NullLogger<SiteVariableService>.Instance);
        _editor = new ReleaseEditor(_paths, variables, options);

        _site = new SiteRecord { Domain = Domain };
        AddRelease("r1", new() { ["index.html"] = "<h1>Hi</h1>", ["css/site.css"] = "body{}", ["css/print.css"] = "@media print{}" }, live: true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ReleaseRecord AddRelease(string id, Dictionary<string, string> files, bool live)
    {
        var dir = _paths.ReleaseDir(Domain, id);
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        var release = new ReleaseRecord
        {
            Id = id, FileCount = files.Count, TotalBytes = files.Values.Sum(v => (long)Encoding.UTF8.GetByteCount(v)), HasRootIndex = files.ContainsKey("index.html"),
            Headers = [new HeaderRule { For = "/assets/**", Set = { ["Cache-Control"] = "no-cache" } }]
        };
        _site.Releases.Insert(0, release);
        if (live) _site.CurrentRelease = id;
        return release;
    }

    private string Live(string path) => File.ReadAllText(Path.Combine(_paths.ReleaseDir(Domain, _site.CurrentRelease!), path));

    private Task<FileEditResult> Write(string path, string text, string? release = null, bool createOnly = false, bool administrator = false) =>
        _editor.EditAsync(_site, release, [new(path, Encoding.UTF8.GetBytes(text))], [], "ann", "web", administrator, createOnly, default);

    private Task<FileEditResult> Delete(params string[] paths) =>
        _editor.EditAsync(_site, null, [], paths, "ann", "web", false, false, default);

    [Fact]
    public async Task A_write_changes_the_live_release_itself_and_keeps_its_record_in_step()
    {
        var result = await Write("index.html", "<h1>Hello there</h1>");

        Assert.True(result.Ok);
        Assert.True(result.Live);
        Assert.Equal("r1", result.Release!.Id);
        Assert.Single(_site.Releases);
        Assert.Equal("<h1>Hello there</h1>", Live("index.html"));
        Assert.Equal(3, result.Release.FileCount);
        Assert.Equal(20 + 6 + 14, result.Release.TotalBytes);
        Assert.Equal("ann", result.Release.EditedBy);
        Assert.NotNull(result.Release.EditedUtc);

        Assert.True((await Write("blog/post.html", "x")).Ok);
        Assert.Equal(4, _site.Current!.FileCount);
        Assert.Empty(Directory.GetFiles(_paths.StagingDir(Domain)));
    }

    [Fact]
    public async Task Deleting_a_folder_removes_everything_in_it_and_the_folders_it_leaves_empty()
    {
        var result = await Delete("css");

        Assert.True(result.Ok);
        Assert.Equal(["css"], result.Deleted);
        Assert.False(Directory.Exists(Path.Combine(_paths.ReleaseDir(Domain, "r1"), "css")));
        Assert.Equal(1, _site.Current!.FileCount);
        Assert.Equal("site.files.error.notFound", (await Delete("nothing-here.html")).Error?.Key);
    }

    [Theory]
    [InlineData("../outside.html", "site.files.error.badPath")]
    [InlineData("_functions/Handlers.cs", "site.files.error.functions")]
    [InlineData(".env", "site.files.error.badPath")]
    [InlineData("index.html/inside.html", "site.files.error.parentIsFile")]
    [InlineData("css", "site.files.error.isFolder")]
    public async Task A_file_goes_only_where_a_deploy_could_put_one(string path, string error)
    {
        Assert.Equal(error, (await Write(path, "x")).Error?.Key);
        Assert.Null(_site.Current!.EditedUtc);
    }

    [Fact]
    public async Task Limits_and_create_only_are_kept()
    {
        Assert.Equal("site.files.error.exists", (await Write("index.html", "", createOnly: true)).Error?.Key);
        Assert.Equal("site.files.error.tooLarge", (await Write("big.bin", new string('x', 2048))).Error?.Key);

        for (var i = 0; i < 4; i++) Assert.True((await Write($"part{i}.txt", new string('x', 1000))).Ok);
        Assert.Equal("site.files.error.siteTooLarge", (await Write("part4.txt", new string('x', 1000))).Error?.Key);
        Assert.False(File.Exists(Path.Combine(_paths.ReleaseDir(Domain, "r1"), "part4.txt")));
    }

    [Fact]
    public async Task Rules_files_are_the_releases_rules_and_one_that_does_not_read_is_refused()
    {
        var bad = await Write(HeaderRuleText.FileName, "Cache-Control: no-cache\n");
        Assert.Equal("site.files.error.unreadable", bad.Error?.Key);
        Assert.NotEmpty(bad.Error!.Details!);
        Assert.Single(_site.Current!.Headers);

        Assert.True((await Write(RedirectRuleText.FileName, "/old /new 301\n")).Ok);
        Assert.Equal("/old", Assert.Single(_site.Current.Redirects).From);
        Assert.False(File.Exists(Path.Combine(_paths.ReleaseDir(Domain, "r1"), RedirectRuleText.FileName)));

        Assert.True((await Delete(HeaderRuleText.FileName)).Ok);
        Assert.Empty(_site.Current.Headers);
    }

    [Fact]
    public async Task A_members_variables_cannot_read_the_servers_environment()
    {
        var result = await Write(VariableFileText.FileName, """{ "HOME": { "public": true, "default": "${env:HOME}" } }""");

        Assert.True(result.Ok);
        Assert.Null(Assert.Single(_site.Current!.Variables).Default);
        Assert.Contains(result.Warnings!, w => w.Contains("${env:"));

        await Write(VariableFileText.FileName, """{ "HOME": { "public": true, "default": "${env:HOME}" } }""", administrator: true);
        Assert.Equal("${env:HOME}", _site.Current.Variables[0].Default);
    }

    [Fact]
    public async Task Another_release_can_be_edited_and_the_live_one_is_left_alone()
    {
        AddRelease("r0", new() { ["index.html"] = "<h1>Old</h1>" }, live: false);

        var result = await Write("index.html", "<h1>Older still</h1>", release: "r0");

        Assert.True(result.Ok);
        Assert.False(result.Live);
        Assert.Equal("<h1>Older still</h1>", File.ReadAllText(Path.Combine(_paths.ReleaseDir(Domain, "r0"), "index.html")));
        Assert.Equal("<h1>Hi</h1>", Live("index.html"));
        Assert.Equal("site.files.error.noRelease", (await Write("index.html", "x", release: "gone")).Error?.Key);
    }

    [Fact]
    public async Task A_duplicate_is_a_copy_that_is_not_live_and_does_not_change_with_the_original()
    {
        var copy = _editor.Duplicate(_site, "r1", "ann");

        Assert.True(copy.Ok);
        Assert.False(copy.Live);
        Assert.Equal("r1", _site.CurrentRelease);
        Assert.Equal("r1", copy.Release!.CopiedFrom);
        Assert.Equal(ReleaseRecord.CopySource, copy.Release.Source);
        Assert.Equal(3, copy.Release.FileCount);
        Assert.Single(copy.Release.Headers);

        await Write("index.html", "<h1>Changed live</h1>");
        Assert.Equal("<h1>Hi</h1>", File.ReadAllText(Path.Combine(_paths.ReleaseDir(Domain, copy.Release.Id), "index.html")));
    }

    [Fact]
    public async Task Moving_a_folder_takes_everything_in_it_and_refuses_what_would_collide()
    {
        var moved = await _editor.MoveAsync(_site, null, "css", "styles", "ann", false, default);
        Assert.True(moved.Ok);
        Assert.Equal(["styles"], moved.Paths);
        Assert.Equal("body{}", Live("styles/site.css"));

        Assert.Equal("site.files.error.intoItself", (await _editor.MoveAsync(_site, null, "styles", "styles/old", "ann", false, default)).Error?.Key);
        Assert.Equal("site.files.error.exists", (await _editor.MoveAsync(_site, null, "styles/site.css", "index.html", "ann", false, default)).Error?.Key);
        Assert.Equal("blog/new.html", (await _editor.MoveAsync(_site, null, "index.html", @" blog\new.html ", "ann", false, default)).Paths![0]);
        Assert.False(_site.Current!.HasRootIndex);
    }

    [Fact]
    public async Task A_file_moved_to_a_rules_name_becomes_the_rules()
    {
        await Write("redirects.txt", "/a /b 302\n");
        var moved = await _editor.MoveAsync(_site, null, "redirects.txt", RedirectRuleText.FileName, "ann", false, default);

        Assert.True(moved.Ok);
        Assert.Equal("/a", Assert.Single(_site.Current!.Redirects).From);
        Assert.False(File.Exists(Path.Combine(_paths.ReleaseDir(Domain, "r1"), "redirects.txt")));
    }

    [Fact]
    public async Task A_site_with_no_release_gets_its_first_from_a_write()
    {
        var site = new SiteRecord { Domain = "new.test" };
        var result = await _editor.EditAsync(site, null, [new("index.html", Encoding.UTF8.GetBytes("<h1>First</h1>"))], [], "ann", "web", false, false, default);

        Assert.True(result.Ok);
        Assert.True(result.Live);
        Assert.Equal(result.Release!.Id, site.CurrentRelease);
        Assert.True(result.Release.HasRootIndex);
    }

    [Fact]
    public void A_secret_declared_again_with_no_default_keeps_the_live_one()
    {
        List<VariableDefinition> live =
        [
            new() { Name = "API_KEY", Secret = true, Default = "dp:protected" },
            new() { Name = "TITLE", Default = "Old" }
        ];
        List<VariableDefinition> next =
        [
            new() { Name = "API_KEY", Secret = true },
            new() { Name = "TITLE" },
            new() { Name = "OTHER", Secret = true }
        ];

        SiteVariableService.KeepSecretDefaults(next, live);

        Assert.Equal("dp:protected", next[0].Default);
        Assert.Null(next[1].Default);
        Assert.Null(next[2].Default);
    }
}
