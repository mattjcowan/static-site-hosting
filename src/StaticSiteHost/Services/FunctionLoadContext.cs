using System.Reflection;
using System.Runtime.Loader;

namespace StaticSiteHost.Services;

/// <summary>
/// Loads one site's compiled functions in isolation, so a redeploy can reclaim the previous
/// build instead of accumulating an assembly per deploy.
///
/// Two rules make that work, and both are easy to get wrong:
///
///   * The assembly is loaded from bytes, never from a path. Loading by path pins the file,
///     and the next deploy then cannot overwrite it — an outright failure on Windows.
///   * Framework and ASP.NET assemblies deliberately fall through to the default context.
///     That is what lets an uploaded handler's <c>IResult</c> be the same type as the host's;
///     a private copy would produce two unrelated types and every cast would fail.
///
/// Unload is a request, not an action: the context disappears only once nothing reachable
/// refers to anything inside it. See <see cref="FunctionSet"/> for what that costs in practice.
/// </summary>
public sealed class FunctionLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public FunctionLoadContext(string mainAssemblyPath, string name)
        : base(name, isCollectible: true) =>
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Null for anything the publish output does not carry, which is every framework
        // assembly — the runtime then resolves it from the default context.
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <summary>
    /// Native dependencies of packages such as ScottPlot (libSkiaSharp) live under
    /// <c>runtimes/&lt;rid&gt;/native</c>. A collectible context does no probing of its own,
    /// so without this they are simply never found.
    /// </summary>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
