using Microsoft.Extensions.DependencyInjection;

namespace StaticSiteHost.Functions.Testing;

/// <summary>
/// An <see cref="ISite"/> for running a handler outside the server, in a LINQPad
/// <c>#if LINQPAD</c> harness or a unit test:
/// <code>
/// var context = new DefaultHttpContext();
/// context.UseSite(new FakeSite { Domain = "demo.localhost" });
/// </code>
/// Everything a handler can reach through it is in memory: its variables, its realtime side and
/// its AI are fakes you fill in and look at afterwards.
/// </summary>
public sealed class FakeSite : ISite
{
    private DirectoryInfo? _data;
    private IServiceProvider? _services;
    private FakeRealtime? _realtime;
    private FakeAiChat? _ai;

    /// <inheritdoc />
    /// <remarks>Defaults to <c>test.localhost</c>.</remarks>
    public string Domain { get; set; } = "test.localhost";

    /// <inheritdoc />
    /// <remarks>
    /// Unless you set one, a new empty folder under the system's temp directory, created on
    /// first use and left there afterwards so you can look at what the handler wrote.
    /// </remarks>
    public DirectoryInfo Data
    {
        get => _data ??= Directory.CreateTempSubdirectory("FakeSite-");
        set => _data = value;
    }

    /// <summary>The site's variables. Add them with <see cref="FakeSiteVariables.Set"/>.</summary>
    public FakeSiteVariables Variables { get; set; } = new();

    ISiteVariables ISite.Variables => Variables;

    /// <inheritdoc />
    /// <remarks>
    /// Unless you set one, an empty provider, made on first use. To hand a handler the services
    /// your <c>[ConfigureServices]</c> method registers, build them yourself:
    /// <code>
    /// var services = new ServiceCollection();
    /// Setup.Configure(services, site);
    /// site.Services = services.BuildServiceProvider();
    /// </code>
    /// </remarks>
    public IServiceProvider Services
    {
        get => _services ??= new ServiceCollection().BuildServiceProvider();
        set => _services = value;
    }

    /// <summary>
    /// The site's realtime side: connections you make with <see cref="FakeRealtime.Connect"/>, and
    /// a record of what the handler published. A new, empty one unless you set one.
    /// </summary>
    public FakeRealtime Realtime
    {
        get => _realtime ??= new FakeRealtime();
        set => _realtime = value;
    }

    IRealtime ISite.Realtime => Realtime;

    /// <summary>
    /// The site's AI, which answers with the replies you queue with <see cref="FakeAiChat.Reply"/>.
    /// A new one, with nothing queued, unless you set one.
    /// </summary>
    public FakeAiChat Ai
    {
        get => _ai ??= new FakeAiChat();
        set => _ai = value;
    }

    IAiChat ISite.Ai => Ai;
}
