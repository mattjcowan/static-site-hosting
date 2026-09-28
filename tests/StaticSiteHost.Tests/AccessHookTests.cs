using System.Reflection;
using Microsoft.AspNetCore.Http;
using StaticSiteHost.Functions;
using StaticSiteHost.Services;
using Kind = StaticSiteHost.Services.FunctionAccessHooks.Kind;

namespace StaticSiteHost.Tests;

public class AccessHookTests
{
    /// <summary>Methods to check, as a function file would declare them. Only the signatures matter.</summary>
    public static class Candidates
    {
        public static bool Allow(HttpContext context) => true;
        public static Task<bool> AllowLater(ISite site, CancellationToken ct) => Task.FromResult(true);
        public static ValueTask<string?> Who(HttpContext context, DirectoryInfo data) => ValueTask.FromResult<string?>("ada");
        public static string? WhoNow() => null;
        public static bool Join(HttpContext context, string group) => true;
        public static bool JoinNoGroup(HttpContext context) => true;
        public static bool JoinNumberedGroup(int group) => true;
        public static bool Page(HttpContext context, int page) => true;
        public static bool Next(Func<Task> next) => true;
        public static int Status() => 200;
        public static Task Nothing() => Task.CompletedTask;
        public static bool Generic<T>() => true;
        internal static bool Hidden() => true;
    }

    private static MethodInfo Method(string name) =>
        typeof(Candidates).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

    [Theory]
    [InlineData(nameof(Candidates.Allow), Kind.AiAccess)]
    [InlineData(nameof(Candidates.AllowLater), Kind.AiAccess)]
    [InlineData(nameof(Candidates.Allow), Kind.RealtimeConnect)]
    [InlineData(nameof(Candidates.Who), Kind.RealtimeConnect)]
    [InlineData(nameof(Candidates.WhoNow), Kind.RealtimeConnect)]
    [InlineData(nameof(Candidates.Join), Kind.RealtimeJoin)]
    public void Accepts_the_signatures_each_hook_can_have(string method, Kind kind) =>
        Assert.Null(FunctionAccessHooks.Check(Method(method), kind));

    [Theory]
    [InlineData(nameof(Candidates.Who), Kind.AiAccess, "Return bool, Task<bool> or ValueTask<bool>")]
    [InlineData(nameof(Candidates.Who), Kind.RealtimeJoin, "Return bool")]
    [InlineData(nameof(Candidates.Status), Kind.RealtimeConnect, "returns Int32. Return string?")]
    [InlineData(nameof(Candidates.Nothing), Kind.AiAccess, "returns Task")]
    [InlineData(nameof(Candidates.JoinNoGroup), Kind.RealtimeJoin, "has no string group parameter")]
    [InlineData(nameof(Candidates.JoinNumberedGroup), Kind.RealtimeJoin, "Make it string group")]
    [InlineData(nameof(Candidates.Join), Kind.RealtimeConnect, "a value a handler would take")]
    [InlineData(nameof(Candidates.Page), Kind.AiAccess, "takes Int32 page")]
    [InlineData(nameof(Candidates.Next), Kind.RealtimeConnect, "only [Middleware] is given")]
    [InlineData(nameof(Candidates.Generic), Kind.AiAccess, "is generic")]
    [InlineData(nameof(Candidates.Hidden), Kind.AiAccess, "not a public static method")]
    public void Refuses_the_rest_and_says_what_to_change(string method, Kind kind, string expected)
    {
        var problem = FunctionAccessHooks.Check(Method(method), kind);

        Assert.NotNull(problem);
        Assert.Contains(expected, problem);
        Assert.StartsWith($"Candidates.{method} is marked [{kind}]", problem);
    }

    [Fact]
    public void A_string_names_the_user_and_null_or_empty_refuses()
    {
        Assert.Equal((true, "ada"), FunctionAccessHooks.ReadAnswer("ada"));
        Assert.Equal((true, (string?)null), FunctionAccessHooks.ReadAnswer(true));
        Assert.Equal((false, (string?)null), FunctionAccessHooks.ReadAnswer(false));
        Assert.Equal((false, (string?)null), FunctionAccessHooks.ReadAnswer(null));
        Assert.Equal((false, (string?)null), FunctionAccessHooks.ReadAnswer(""));
    }

    /// <summary>Two gates of the same kind in one set, which the build refuses naming both.</summary>
    public static class TwoGates
    {
        [AiAccess]
        public static bool First(HttpContext context) => true;

        [AiAccess]
        public static bool Second(HttpContext context) => false;
    }

    [Fact]
    public void Refuses_a_second_hook_of_the_same_kind_naming_both()
    {
        var hooks = FunctionAccessHooks.Discover(typeof(TwoGates).Assembly);

        Assert.Null(hooks.Get(Kind.AiAccess));
        var problem = Assert.Single(hooks.Problems);
        Assert.StartsWith("TwoGates.First and TwoGates.Second are both marked [AiAccess]", problem);
    }
}
