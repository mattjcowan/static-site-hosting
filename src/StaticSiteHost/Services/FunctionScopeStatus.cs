namespace StaticSiteHost.Services;

/// <summary>Where one scope's live functions stand; see <see cref="FunctionScopeStatus"/>.</summary>
public enum FunctionLoadState
{
    /// <summary>Nothing is loaded for the bundle live now: it loads on its first request, or there is no bundle.</summary>
    NotLoaded,

    /// <summary>It is being loaded right now.</summary>
    Loading,

    /// <summary>It is loaded and answering; its background services and jobs are running.</summary>
    Loaded,

    /// <summary>It could not be loaded, and is not retried until something changes; <see cref="FunctionScopeStatus.Message"/> says why.</summary>
    Failed,
}

/// <summary>What a background service is doing, for the Functions card.</summary>
public enum FunctionBackgroundState
{
    /// <summary>Its method is running.</summary>
    Running,

    /// <summary>It threw, and is waiting out its pause before it starts again.</summary>
    Restarting,

    /// <summary>It was stopped, as the functions are being replaced or the server is stopping.</summary>
    Stopped,

    /// <summary>It returned by itself, and does not run again until the functions next load.</summary>
    Finished,
}

/// <summary>
/// The live functions of a site (or the global ones) as <see cref="FunctionHost.StatusAsync"/>
/// reports them: whether they are loaded, why not if they failed, and how their background work
/// is going. A snapshot of plain values, so the page holding it holds nothing of the functions.
/// </summary>
/// <param name="Message">Why they failed to load, in a sentence; null unless <see cref="State"/> is <see cref="FunctionLoadState.Failed"/>.</param>
/// <param name="LoadedUtc">When they loaded; null unless <see cref="State"/> is <see cref="FunctionLoadState.Loaded"/>.</param>
public sealed record FunctionScopeStatus(
    FunctionLoadState State,
    string? Message,
    DateTimeOffset? LoadedUtc,
    IReadOnlyList<FunctionJobStatus> Jobs,
    IReadOnlyList<FunctionBackgroundStatus> BackgroundServices)
{
    public static readonly FunctionScopeStatus NotLoaded = new(FunctionLoadState.NotLoaded, null, null, [], []);
    public static readonly FunctionScopeStatus Loading = new(FunctionLoadState.Loading, null, null, [], []);

    public static FunctionScopeStatus Failed(string? message) => new(FunctionLoadState.Failed, message, null, [], []);
}

/// <summary>One <c>[Schedule]</c> or <c>[Every]</c> method's runs so far in the loaded functions.</summary>
/// <param name="Display">The same text as the bundle's <c>Jobs</c> entry, such as <c>every 5m Cache.Refresh on start</c>.</param>
/// <param name="Method">Class and method, such as <c>Cache.Refresh</c>.</param>
/// <param name="Schedule">The cron schedule, or <c>every 5m</c>.</param>
/// <param name="NextRunUtc">When it next comes due; null once it has stopped.</param>
/// <param name="LastError">The message of the most recent run that failed, even if later runs did not.</param>
/// <param name="Runs">Runs that have finished, failed ones included.</param>
/// <param name="Skipped">Times it came due while its previous run was still going, and so did not run.</param>
public sealed record FunctionJobStatus(
    string Display,
    string Method,
    string Schedule,
    DateTimeOffset? NextRunUtc,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastFinishedUtc,
    TimeSpan? LastDuration,
    string? LastError,
    DateTimeOffset? LastErrorUtc,
    int Runs,
    int Failures,
    bool IsRunning,
    int Skipped);

/// <summary>One <c>[BackgroundService]</c> method in the loaded functions.</summary>
/// <param name="Method">Class and method, such as <c>Worker.Run</c>.</param>
/// <param name="Restarts">Times it threw and was started again.</param>
/// <param name="LastError">The message of the last exception it threw.</param>
/// <param name="StartedUtc">When it last started.</param>
public sealed record FunctionBackgroundStatus(
    string Method,
    FunctionBackgroundState State,
    int Restarts,
    string? LastError,
    DateTimeOffset? StartedUtc);
