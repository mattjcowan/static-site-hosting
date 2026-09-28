using Microsoft.AspNetCore.Http;

namespace StaticSiteHost.Functions;

/// <summary>
/// The site a request is for, as the functions answering it see it: its domain, its data
/// folder, its variables, the functions' services, the pages connected to it and its AI. Reach
/// it from any code that has the request with <see cref="SiteHttpContextExtensions.Site(HttpContext)"/>.
/// Code that runs outside a request (a job, a background service, a <c>[ConfigureServices]</c>
/// method) takes it as a parameter, and sees the functions' own site instead.
/// </summary>
/// <remarks>
/// <para>
/// Only Static Site Host implements this interface, and <see cref="Testing.FakeSite"/> stands
/// in for it outside the server. Later versions add members, so do not implement it in your
/// own code: such a class would stop compiling when the server moves to a newer version.
/// </para>
/// <para>
/// The functions' own site, the one code outside a request sees, is retired with them. Once
/// they have been replaced, or their site deleted or renamed, and they have had their time to
/// stop, every member but <see cref="Domain"/> throws <see cref="InvalidOperationException"/>, and
/// so do the variables, realtime side and AI it handed out: code that goes on after its
/// <see cref="CancellationToken"/> is cancelled reaches nothing of a site that may no longer be
/// its own. A request's site hands out no <see cref="Data"/> folder once the functions it is
/// running on have been unloaded.
/// </para>
/// </remarks>
public interface ISite
{
    /// <summary>
    /// The domain the request is for, such as <c>demo.example.com</c>. Global functions answer
    /// on every site, so for them it is the host the request arrived on. Outside a request it is
    /// the domain whose functions are running, or <c>global</c> for the global functions.
    /// </summary>
    string Domain { get; }

    /// <summary>
    /// The data folder of the functions answering the request: the site's own for a site's
    /// functions, one shared folder for the global functions. It is never served, deploys and
    /// rollbacks leave it alone, a rename moves it with the site and deleting the site deletes
    /// it. It is created on first use, so it always exists once you have it.
    /// </summary>
    DirectoryInfo Data { get; }

    /// <summary>
    /// The site's variables, secrets included. In a request they are read once, as the request
    /// starts; outside one each read sees the latest values. The global functions have none of
    /// their own outside a request.
    /// </summary>
    ISiteVariables Variables { get; }

    /// <summary>
    /// The functions' services: what their <c>[ConfigureServices]</c> methods registered, plus
    /// what the server provides (<c>ILoggerFactory</c>, <c>ILogger&lt;T&gt;</c>, a non-generic
    /// <c>ILogger</c>, <c>IDataProtectionProvider</c>, <c>IHttpClientFactory</c>,
    /// <see cref="TimeProvider"/>, <see cref="ISite"/>, <see cref="ISiteVariables"/>,
    /// <see cref="IRealtime"/> and <see cref="IAiChat"/>). Always there, with just those when the
    /// functions register nothing.
    /// </summary>
    /// <remarks>
    /// In a request it is a scope made for the request and disposed when the request ends, so
    /// scoped services resolve from it. Outside a request, in a job or a background service, it is
    /// the root provider, which lives as long as the functions do; scoped services must then come
    /// from a scope you create (a job's own <see cref="IServiceProvider"/> parameter is one).
    /// While <c>[ConfigureServices]</c> methods are still running there is nothing to resolve
    /// yet, and reading it throws <see cref="InvalidOperationException"/>.
    /// </remarks>
    IServiceProvider Services { get; }

    /// <summary>
    /// The pages open on the site that are connected to its realtime hub, and the way to send
    /// them events: see <see cref="IRealtime"/>.
    /// </summary>
    /// <remarks>
    /// In a request it is the realtime side of the site the request is for, for global functions
    /// too. In a job or a background service it is the functions' own site's. The global functions
    /// belong to no site outside a request, so there reading it throws
    /// <see cref="InvalidOperationException"/>: use it inside a request, where it is the request's
    /// site's.
    /// </remarks>
    IRealtime Realtime { get; }

    /// <summary>
    /// Chat with the AI provider an administrator chose for the site, with the site's model and
    /// pinned system prompt: see <see cref="IAiChat"/>. Whether the site lets its visitors chat
    /// makes no difference here; that decides only what browsers may do.
    /// </summary>
    /// <remarks>
    /// In a request it is the AI of the site the request is for, for global functions too. In a
    /// job or a background service it is the functions' own site's. The global functions belong to
    /// no site outside a request, so there reading it throws <see cref="InvalidOperationException"/>:
    /// use it inside a request, where it is the request's site's.
    /// </remarks>
    IAiChat Ai { get; }
}
