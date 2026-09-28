namespace StaticSiteHost.Services;

/// <summary>
/// Loads the functions that have background work of their own once the server is listening,
/// and asks that work to stop when the server stops.
///
/// Loading waits for <see cref="IHostApplicationLifetime.ApplicationStarted"/> and then runs on a
/// task of its own, so a slow <c>[ConfigureServices]</c> or a site with a large build never
/// delays the server answering; a request that arrives first for one of those sites simply loads
/// it itself. See <see cref="FunctionHost.WarmUpAsync"/> and <see cref="FunctionHost.StopAsync"/>.
/// </summary>
public sealed class FunctionWarmUp : IHostedService
{
    private readonly FunctionHost _functions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<FunctionWarmUp> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private CancellationTokenRegistration _started;
    private Task _warmUp = Task.CompletedTask;

    public FunctionWarmUp(FunctionHost functions, IHostApplicationLifetime lifetime, ILogger<FunctionWarmUp> logger)
    {
        _functions = functions;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _started = _lifetime.ApplicationStarted.Register(() => _warmUp = FunctionSet.RunDetached(WarmUpAsync));
        return Task.CompletedTask;
    }

    private async Task WarmUpAsync()
    {
        try
        {
            await _functions.WarmUpAsync(_stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // The server began stopping first.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the functions with background work failed; each loads on its first request instead");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _started.DisposeAsync();
        await _stopping.CancelAsync();

        try
        {
            await _warmUp.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The server will not wait; the stop below is what matters.
        }

        await _functions.StopAsync(cancellationToken);
    }
}
