using StaticSiteHost.Models;
using StaticSiteHost.Services;

namespace StaticSiteHost.Tests;

/// <summary>Who may save what, and how it is stored: <see cref="SiteVariableService.Flags"/>.</summary>
public class VariableRulesTests
{
    private static VariableDefinition Declared(bool isPublic = false, bool isSecret = false) =>
        new() { Name = "KEY", Public = isPublic, Secret = isSecret };

    private static SiteVariable Held(string value, bool isPublic = false, bool isSecret = false) =>
        new() { Name = "KEY", Value = value, Public = isPublic, Secret = isSecret };

    private static (bool Public, bool Secret, string? Error) Flags(
        VariableDefinition? definition = null, SiteVariable? existing = null, string value = "plain",
        bool? isPublic = null, bool? isSecret = null, bool isAdministrator = true) =>
        SiteVariableService.Flags("KEY", definition, existing, value, isPublic, isSecret, isAdministrator);

    [Fact]
    public void A_release_can_make_a_value_more_private_never_less()
    {
        Assert.Equal((false, true, (string?)null), Flags(Declared(isSecret: true), isSecret: false));
        Assert.Equal((false, true, (string?)null), Flags(Declared(isPublic: true), isSecret: true));
        Assert.Equal((false, false, (string?)null), Flags(Declared(), isPublic: true));
        Assert.Equal((true, false, (string?)null), Flags(Declared(isPublic: true)));
    }

    [Fact]
    public void A_value_that_reads_the_environment_is_public_only_when_asked_in_so_many_words()
    {
        const string env = "${env:SMTP_PASSWORD}";

        // A release declaring it public is not enough, nor is a flag left out, nor what was stored before.
        Assert.Equal((false, false, (string?)null), Flags(Declared(isPublic: true), value: env));
        Assert.Equal((false, false, (string?)null), Flags(Declared(isPublic: true), Held("x", isPublic: true), env));
        Assert.Equal((false, false, (string?)null), Flags(Declared(isPublic: true), value: env, isPublic: false));
        Assert.Equal((true, false, (string?)null), Flags(Declared(isPublic: true), value: env, isPublic: true));
        Assert.Equal((false, false, (string?)null), Flags(value: env, isPublic: false));
        Assert.Equal((true, false, (string?)null), Flags(value: env, isPublic: true));
    }

    [Fact]
    public void An_undeclared_value_keeps_its_flags_unless_told_otherwise()
    {
        Assert.Equal((true, false, (string?)null), Flags(existing: Held("x", isPublic: true)));
        Assert.Equal((false, true, (string?)null), Flags(existing: Held("dp:x", isSecret: true)));
        Assert.Equal((false, false, (string?)null), Flags(existing: Held("x", isPublic: true), isPublic: false));
        Assert.Contains("cannot be both public and secret", Flags(isPublic: true, isSecret: true).Error);
    }

    [Fact]
    public void A_member_sets_plain_values_only()
    {
        Assert.Equal((false, false, (string?)null), Flags(isAdministrator: false));
        Assert.Equal((true, false, (string?)null), Flags(Declared(isPublic: true), Held("old"), isAdministrator: false));

        Assert.Contains("is a secret", Flags(isSecret: true, isAdministrator: false).Error);
        Assert.Contains("is a secret", Flags(Declared(isSecret: true), isAdministrator: false).Error);
        Assert.Contains("only administrators can use ${env:…}", Flags(value: "${env:HOME}", isAdministrator: false).Error);
    }

    [Fact]
    public void A_member_cannot_overwrite_what_an_administrator_holds()
    {
        Assert.Contains("only an administrator can change", Flags(existing: Held("dp:x", isSecret: true), isAdministrator: false).Error);
        Assert.Contains("only an administrator can change", Flags(existing: Held("${env:HOME}"), isAdministrator: false).Error);
        Assert.Contains("only an administrator can change",
            Flags(Declared(isSecret: true), Held("still plain"), isAdministrator: false).Error);
    }

    [Fact]
    public void An_administrator_may_do_all_of_it()
    {
        Assert.Null(Flags(Declared(isSecret: true), Held("dp:x", isSecret: true)).Error);
        Assert.Null(Flags(existing: Held("${env:HOME}"), value: "${env:PATH}").Error);
    }

    [Theory]
    [InlineData("plain", false, false, false)]
    [InlineData("dp:ciphertext", true, false, true)]
    [InlineData("${env:SMTP}", false, false, true)]
    [InlineData("smtp://${env:HOST}:25", false, false, true)]
    [InlineData("plain", false, true, true)]
    public void Knows_what_only_an_administrator_may_touch(string value, bool storedSecret, bool declaredSecret, bool expected) =>
        Assert.Equal(expected, SiteVariableService.IsAdministratorsOnly(
            Held(value, isSecret: storedSecret), declaredSecret ? Declared(isSecret: true) : null));
}
