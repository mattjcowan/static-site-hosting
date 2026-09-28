using Microsoft.AspNetCore.Http;
using StaticSiteHost.Functions;
using StaticSiteHost.Services;

namespace StaticSiteHost.Tests;

/// <summary>A middleware method's <c>next</c> runs the rest of the request once.</summary>
public class MiddlewareNextTests
{
    /// <summary>Middleware as a function file would declare it; the router finds it by its attribute.</summary>
    public static class Twice
    {
        [Middleware]
        public static async Task CallsNextTwice(HttpContext context, Func<Task> next)
        {
            await next();
            await next();
        }
    }

    [Fact]
    public async Task A_second_call_throws_and_the_rest_ran_once()
    {
        var router = FunctionRouter.Discover(typeof(Twice).Assembly);
        var ran = 0;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.InvokeMiddlewareAsync(new DefaultHttpContext(), () =>
            {
                ran++;
                return Task.CompletedTask;
            }));

        Assert.StartsWith("next was already called", error.Message);
        Assert.Equal(1, ran);
    }
}
