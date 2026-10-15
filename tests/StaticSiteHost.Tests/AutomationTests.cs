using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Models;
using StaticSiteHost.Services;
using StaticSiteHost.Tools;

namespace StaticSiteHost.Tests;

/// <summary>
/// What a script or an agent relies on to set a server up without the UI: the key from
/// configuration, the audit log, the user rules, the tool catalog and the built-in manual.
/// </summary>
public sealed class AutomationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ssh-automation-tests-" + Guid.NewGuid().ToString("n"));
    private readonly DataPaths _paths;

    public AutomationTests() =>
        _paths = new DataPaths(Options.Create(new SiteHostingOptions { DataRoot = _root }));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string ConfiguredKey = "sshost_bootstrap01_abcdefghijklmnopqrstuvwxyz0123456789ABCD";

    [Fact]
    public async Task A_key_from_configuration_works_until_it_is_changed_or_removed()
    {
        var keys = new ApiKeyStore(_paths);

        Assert.Null(await keys.EnsureConfiguredAsync("admin-id", ConfiguredKey));
        var record = await keys.ValidateAsync(ConfiguredKey);
        Assert.NotNull(record);
        Assert.True(record.FromConfiguration);
        Assert.Equal("admin-id", record.UserId);

        // Starting again with the same setting changes nothing.
        Assert.Null(await keys.EnsureConfiguredAsync("admin-id", ConfiguredKey));
        Assert.Single(await keys.ListForUserAsync("admin-id"));

        // A new key replaces it; the old one stops working.
        const string replacement = "sshost_bootstrap02_ZYXWVUTSRQPONMLKJIHGFEDCBA9876543210zyxw";
        Assert.Null(await keys.EnsureConfiguredAsync("admin-id", replacement));
        Assert.Null(await keys.ValidateAsync(ConfiguredKey));
        Assert.NotNull(await keys.ValidateAsync(replacement));

        // No setting: no configured key works.
        Assert.Null(await keys.EnsureConfiguredAsync("admin-id", null));
        Assert.Null(await keys.ValidateAsync(replacement));
    }

    [Fact]
    public async Task A_new_value_that_cannot_be_read_still_revokes_the_old_key()
    {
        var keys = new ApiKeyStore(_paths);
        Assert.Null(await keys.EnsureConfiguredAsync("admin-id", ConfiguredKey));

        // Rotating a leaked key, with a typo in the new one.
        Assert.Contains("secret must be", await keys.EnsureConfiguredAsync("admin-id", "sshost_bootstrap02_tooshort"));
        Assert.Null(await keys.ValidateAsync(ConfiguredKey));
    }

    [Fact]
    public async Task A_revoked_configured_key_stays_revoked_until_the_setting_names_a_new_one()
    {
        var keys = new ApiKeyStore(_paths);
        Assert.Null(await keys.EnsureConfiguredAsync("admin-id", ConfiguredKey));
        Assert.True(await keys.RevokeAsync("bootstrap01", "admin-id"));

        // A restart with the same setting does not bring it back, and says why.
        Assert.Contains("revoked", await keys.EnsureConfiguredAsync("admin-id", ConfiguredKey));
        Assert.Null(await keys.ValidateAsync(ConfiguredKey));

        // The same id with a new secret is a new key, and works.
        const string fresh = "sshost_bootstrap01_NEWSECRETabcdefghijklmnopqrstuvwxyz0123";
        Assert.Null(await keys.EnsureConfiguredAsync("admin-id", fresh));
        Assert.NotNull(await keys.ValidateAsync(fresh));
        Assert.Null(await keys.ValidateAsync(ConfiguredKey));
    }

    [Theory]
    [InlineData("not-a-key", "must look like")]
    [InlineData("sshost_short_abcdefghijklmnopqrstuvwxyz0123456789", "id must be")]
    [InlineData("sshost_bootstrap01_tooshort", "secret must be")]
    [InlineData("sshost_boot-strap_abcdefghijklmnopqrstuvwxyz0123456789", "id must be")]
    public async Task A_configured_key_that_cannot_be_read_is_refused(string key, string expected) =>
        Assert.Contains(expected, await new ApiKeyStore(_paths).EnsureConfiguredAsync("admin-id", key));

    [Fact]
    public async Task A_configured_key_cannot_take_over_a_key_made_in_the_ui()
    {
        var keys = new ApiKeyStore(_paths);
        var (made, _) = await keys.CreateAsync("someone", "theirs", null);

        var error = await keys.EnsureConfiguredAsync("admin-id", $"sshost_{made.Id}_abcdefghijklmnopqrstuvwxyz0123456789ABCD");
        Assert.Contains("already has that id", error);
    }

    [Fact]
    public async Task The_audit_log_reads_newest_first_and_filters_by_action_site_and_time()
    {
        var audit = new AuditLog(_paths, NullLogger<AuditLog>.Instance);
        await audit.WriteAsync("site.deploy", "ada", new { domain = "a.test" });
        await audit.WriteAsync("site.proxy", "ada", new { domain = "b.test" });
        await audit.WriteAsync("site.rename", "ada", new { from = "a.test", to = "c.test" });
        await audit.WriteAsync("sites.other", "ada", new { domain = "a.test" });
        await audit.WriteAsync("user.create", "ada", new { username = "bob" });

        static string Action(JsonElement entry) => entry.GetProperty("action").GetString()!;

        Assert.Equal(["user.create", "sites.other"], (await audit.ReadAsync(2)).Select(Action));
        Assert.Equal(["site.rename", "site.proxy", "site.deploy"], (await audit.ReadAsync(10, action: "site")).Select(Action));
        Assert.Equal(["sites.other", "site.rename", "site.deploy"], (await audit.ReadAsync(10, domain: "A.TEST")).Select(Action));
        Assert.Empty(await audit.ReadAsync(10, since: DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public async Task The_audit_log_reads_back_across_blocks_with_multi_byte_text_and_skips_a_half_written_line()
    {
        var audit = new AuditLog(_paths, NullLogger<AuditLog>.Instance);
        var padding = new string('é', 600); // two bytes each in UTF-8, so lines straddle the 64 KB blocks
        for (var i = 0; i < 400; i++) await audit.WriteAsync("site.deploy", "ada", new { domain = "a.test", n = i, padding });

        // A line still being written when the log is read.
        await File.AppendAllTextAsync(_paths.AuditFile, "{\"ts\":\"2026-");

        var entries = await audit.ReadAsync(500);
        Assert.Equal(400, entries.Count);
        Assert.Equal(399, entries[0].GetProperty("details").GetProperty("n").GetInt32());
        Assert.Equal(0, entries[^1].GetProperty("details").GetProperty("n").GetInt32());
        Assert.All(entries, e => Assert.Equal(padding, e.GetProperty("details").GetProperty("padding").GetString()));

        Assert.Equal(3, (await audit.ReadAsync(3)).Count);
    }

    [Fact]
    public async Task User_administration_keeps_its_guards()
    {
        var users = new UserStore(_paths, Options.Create(new SiteHostingOptions { DataRoot = _root }));
        var admin = new UserAdminService(users, new ApiKeyStore(_paths), new AuditLog(_paths, NullLogger<AuditLog>.Instance));

        var (first, _) = await users.CreateAsync("root", "Root", Roles.Administrator, "a-long-enough-password");
        var (created, error) = await admin.CreateAsync("ci-bot", null, "Member", temporaryPassword: false, "root");
        Assert.Null(error);
        Assert.NotNull(created!.InviteToken);
        Assert.Null(created.TemporaryPassword);

        Assert.Equal("You cannot change your own role.", await admin.SetRoleAsync(first!.Id, Roles.Member, first.Id, "root"));
        Assert.Equal("At least one active administrator must remain.", await admin.SetRoleAsync(first.Id, Roles.Member, created.User.Id, "ci-bot"));
        Assert.Equal("You cannot delete your own account.", (await admin.DeleteAsync(first.Id, first.Id, "root")).Error);

        Assert.Null(await admin.SetRoleAsync(created.User.Id, Roles.Administrator, first.Id, "root"));
        Assert.Equal("ci-bot", (await admin.DeleteAsync(created.User.Id, first.Id, "root")).Username);
    }

    [Fact]
    public void Every_tool_has_a_unique_name_a_description_and_described_parameters()
    {
        var tools = SiteHostTools.All;
        Assert.True(tools.Count >= 30);
        Assert.Equal(tools.Count, tools.Select(t => t.Function.Name).Distinct().Count());

        foreach (var tool in tools)
        {
            Assert.Matches("^[a-z][a-z_]+$", tool.Function.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Function.Description), tool.Function.Name);

            // The services and the cancellation token are the server's, never the model's to fill in.
            var properties = tool.Function.JsonSchema.GetProperty("properties");
            foreach (var property in properties.EnumerateObject())
            {
                Assert.NotEqual("services", property.Name);
                Assert.NotEqual("ct", property.Name);
                Assert.True(property.Value.TryGetProperty("description", out _), $"{tool.Function.Name}.{property.Name} has no description");
            }
        }

        Assert.Equal(ToolEffect.Destructive, tools.Single(t => t.Function.Name == "delete_site").Effect);
        Assert.Equal(ToolEffect.Read, tools.Single(t => t.Function.Name == "list_sites").Effect);
    }

    [Fact]
    public void The_manual_is_built_in_and_split_into_sections()
    {
        var titles = Manual.Sections.Select(s => s.Title).ToList();

        Assert.Contains("Proxy routes", titles);
        Assert.Contains("API", titles);
        Assert.Contains("**What it is.**", Manual.Sections.Single(s => s.Title == "Proxy routes").Text);
    }
}

public class FunctionAbstractionsReferenceTests
{
    private static FunctionSource Source(string code, params FunctionPackage[] packages) =>
        new(FunctionSourceKind.CSharpFile, code, 0, packages, [], [], NeedsAspNetCore: true);

    [Theory]
    [InlineData("using StaticSiteHost.Functions;\npublic static class A { }", true)]
    [InlineData("public static class A { [StaticSiteHost.Functions.Middleware] public static void M() { } }", true)]
    [InlineData("using Microsoft.AspNetCore.Http;\npublic static class A { }", false)]
    [InlineData("// MyStaticSiteHost.FunctionsHelper is not it\npublic static class A { }", false)]
    public void A_file_that_uses_the_abstractions_gets_them_without_asking(string code, bool expected)
    {
        var (merged, error) = FunctionProjectGenerator.Merge([("A.cs", Source(code))]);

        Assert.Null(error);
        Assert.Equal(expected, merged!.UsesAbstractions);
    }

    [Fact]
    public void Asking_for_the_package_still_works() =>
        Assert.True(FunctionProjectGenerator.Merge([("A.cs", Source("public static class A { }", new FunctionPackage("StaticSiteHost.Abstractions", "*")))]).Merged!.UsesAbstractions);
}

/// <summary>The fixes from the code review of the proxy, API and assistant work.</summary>
public sealed class ReviewFixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ssh-review-tests-" + Guid.NewGuid().ToString("n"));
    private readonly SiteStore _sites;

    public ReviewFixTests()
    {
        var paths = new DataPaths(Options.Create(new SiteHostingOptions { DataRoot = _root }));
        _sites = new SiteStore(paths, NullLogger<SiteStore>.Instance);
        _sites.SaveAsync(new SiteRecord { Domain = "shop.test" }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("SHOP.test", "shop.test", null)]
    [InlineData("https://shop example.com", null, "error")]
    [InlineData("other.test", null, "No site is published")]
    public void A_function_tool_acts_on_the_global_functions_only_when_no_domain_is_given(string? domain, string? scope, string? error)
    {
        var (actualScope, actualError) = SiteHostTools.FunctionScope(_sites, domain);

        Assert.Equal(scope, actualScope);
        if (error is null) Assert.Null(actualError);
        else Assert.NotNull(actualError);
        if (error is "No site is published") Assert.Contains(error, actualError);
    }

    [Theory]
    [InlineData("delete_site")]
    [InlineData("remove_functions")]
    [InlineData("remove_proxy_route")]
    [InlineData("remove_variable")]
    [InlineData("remove_passcode")]
    public void Removing_anything_asks_first(string tool) =>
        Assert.Equal(ToolEffect.Destructive, SiteHostTools.All.Single(t => t.Function.Name == tool).Effect);

    [Fact]
    public void A_route_that_cannot_be_saved_is_caught_before_its_site_is_made()
    {
        Assert.NotEmpty(SiteProxyService.CheckNewSiteRoute("new.test", new ProxyRoute { Path = "/", Target = "web:3000" }));
        Assert.Empty(SiteProxyService.CheckNewSiteRoute("new.test", new ProxyRoute { Path = "/", Target = "http://web:3000" }));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(524288L, "0.5")]
    [InlineData(1572864L, "1.5")]
    [InlineData(10485760L, "10")]
    [InlineData(1000L, "0.001")]
    public void The_body_limit_shows_in_megabytes_with_fractions(long? bytes, string shown) =>
        Assert.Equal(shown, StaticSiteHost.Pages.Sites.ProxyRouteInput.Megabytes(bytes));

    [Theory]
    [InlineData("0.5", 524288L)]
    [InlineData(" 1.5 ", 1572864L)]
    [InlineData("10", 10485760L)]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("1,5", null)]
    [InlineData("lots", null)]
    public void The_body_limit_reads_megabytes_the_same_in_any_culture(string typed, long? bytes)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Assert.Equal(bytes, StaticSiteHost.Pages.Sites.ProxyRouteInput.BytesOf(typed));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void A_body_limit_that_cannot_be_read_is_refused_with_a_reason()
    {
        Assert.NotNull(new StaticSiteHost.Pages.Sites.ProxyRouteInput { MaxRequestBodyMegabytes = "lots" }.BodyLimitError);
        Assert.Null(new StaticSiteHost.Pages.Sites.ProxyRouteInput { MaxRequestBodyMegabytes = "" }.BodyLimitError);
        Assert.Null(new StaticSiteHost.Pages.Sites.ProxyRouteInput { MaxRequestBodyMegabytes = "0.25" }.BodyLimitError);
    }
}
