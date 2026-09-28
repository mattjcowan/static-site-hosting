using System.Diagnostics;
using System.Reflection;

namespace StaticSiteHost.Services;

/// <summary>
/// Runs a live set's background services and jobs, each on a task of its own, and keeps what the
/// Functions card shows about them. Owned by its <see cref="FunctionSet"/>, and nothing else
/// holds it: every task watches the set's stopping token, the set waits for them all when it
/// retires, and the state goes with the set.
///
/// A background service that throws is started again after a pause of a second, doubling to a
/// minute, which starts over once a run has lasted longer than that; one that returns is
/// finished. A job's loop works out when it is next due, waits for then, and starts the run on a
/// task of its own, so a run that overruns does not hold up the clock: an occurrence that comes
/// due while the previous run is still going is skipped and logged, never queued. Nothing is
/// made up for occurrences that passed while the server was down, busy or replacing the
/// functions. A failure is logged with whose functions and which method, and counted; it never
/// reaches anything else.
/// </summary>
internal sealed class FunctionJobRunner
{
    private static readonly TimeSpan FirstPause = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestPause = TimeSpan.FromMinutes(1);

    /// <summary>A long wait is taken in steps of this, checking the wall clock after each.</summary>
    private static readonly TimeSpan LongestStep = TimeSpan.FromHours(1);

    private readonly FunctionSet _set;
    private readonly JobBinding _binding;
    private readonly IServiceProvider _services;
    private readonly ILogger _log;
    private readonly string _owner;
    private readonly BackgroundState[] _background;
    private readonly JobState[] _scheduled;
    private readonly List<(string What, Task Task)> _tasks = [];

    /// <param name="services">The root provider, which background services are given.</param>
    public FunctionJobRunner(FunctionSet set, FunctionJobs jobs, JobBinding binding, IServiceProvider services, ILogger log)
    {
        _set = set;
        _binding = binding;
        _services = services;
        _log = log;
        _owner = binding.Site.Domain;
        _background = [.. jobs.BackgroundServices.Select(method => new BackgroundState(method))];
        _scheduled = [.. jobs.Scheduled.Select(job => new JobState(job))];
    }

    public void Start()
    {
        foreach (var state in _background)
            _tasks.Add(($"the background service {state.Name}", FunctionSet.RunDetached(() => RunBackgroundAsync(state))));

        foreach (var state in _scheduled)
            _tasks.Add(($"the job {state.Job.Name}", FunctionSet.RunDetached(() => RunScheduleAsync(state))));
    }

    /// <summary>
    /// Waits up to <paramref name="grace"/> for every task to end, once the stopping token has
    /// been cancelled. Returns those still going, such as "the job Feed.Refresh".
    /// </summary>
    public async Task<IReadOnlyList<string>> WaitAsync(TimeSpan grace)
    {
        try
        {
            await Task.WhenAll(_tasks.Select(t => t.Task)).WaitAsync(grace);
        }
        catch (TimeoutException)
        {
            // Named below.
        }

        return [.. _tasks.Where(t => !t.Task.IsCompleted).Select(t => t.What)];
    }

    public (IReadOnlyList<FunctionJobStatus> Jobs, IReadOnlyList<FunctionBackgroundStatus> BackgroundServices) Snapshot() =>
        ([.. _scheduled.Select(s => s.Snapshot())], [.. _background.Select(s => s.Snapshot())]);

    // ---------------------------------------------------------------- background services

    private async Task RunBackgroundAsync(BackgroundState state)
    {
        var stopping = _binding.Stopping;
        var pause = FirstPause;

        while (!stopping.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            state.Started(started);

            try
            {
                await FunctionJobs.InvokeAsync(state.Method, FunctionJobs.BindArguments(state.Method, _binding, _services));

                if (!stopping.IsCancellationRequested)
                {
                    _log.LogInformation(
                        "The background service {Method} of {Owner}'s functions returned, so it is finished until they next load",
                        state.Name, _owner);
                }

                state.Ended(stopping.IsCancellationRequested ? FunctionBackgroundState.Stopped : FunctionBackgroundState.Finished);
                return;
            }
            catch (Exception ex) when (stopping.IsCancellationRequested)
            {
                // Being stopped: a cancellation is how it is meant to end, and it is not restarted either way.
                if (ex is not OperationCanceledException)
                {
                    state.Failed(ex.Message);
                    _log.LogWarning(ex, "The background service {Method} of {Owner}'s functions threw as it was being stopped",
                        state.Name, _owner);
                }

                state.Ended(FunctionBackgroundState.Stopped);
                return;
            }
            catch (Exception ex)
            {
                // A run that lasted a good while was a healthy one, so its failure starts the backoff over.
                if (DateTimeOffset.UtcNow - started > LongestPause) pause = FirstPause;

                state.Failed(ex.Message);
                _log.LogError(ex, "The background service {Method} of {Owner}'s functions failed; it starts again in {Pause}",
                    state.Name, _owner, JobInterval.Format(pause));

                try
                {
                    await Task.Delay(pause, stopping);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                state.Restarting();
                pause = pause * 2 < LongestPause ? pause * 2 : LongestPause;
            }
        }

        state.Ended(FunctionBackgroundState.Stopped);
    }

    // ---------------------------------------------------------------- jobs

    private async Task RunScheduleAsync(JobState state)
    {
        var job = state.Job;
        var stopping = _binding.Stopping;
        Task? run = null;

        try
        {
            var due = job.First(DateTimeOffset.UtcNow);

            while (due is { } next)
            {
                state.Due(next);
                await WaitUntilAsync(next, stopping);

                if (run is { IsCompleted: false })
                {
                    var started = state.Skipped();
                    _log.LogWarning(
                        "The job {Method} of {Owner}'s functions came due at {Due:u}, but its run that started at {Started:u} " +
                        "is still going, so this one is skipped", job.Name, _owner, next, started);
                }
                else
                {
                    // Its own task, so a run that overruns does not hold up the clock (see the class remarks).
                    run = Task.Run(() => RunOnceAsync(state));
                }

                due = job.Next(next, DateTimeOffset.UtcNow);
            }

            _log.LogWarning("The job {Method} of {Owner}'s functions has no occurrences left, so it stops", job.Name, _owner);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Retiring, or the server is stopping.
        }
        catch (Exception ex)
        {
            // The loop itself, not the job, which RunOnceAsync keeps to itself. Nothing should get here.
            _log.LogError(ex, "The schedule of the job {Method} of {Owner}'s functions stopped unexpectedly", job.Name, _owner);
        }
        finally
        {
            state.Due(null);

            // The loop is over only when its last run is: retiring waits on this task, and so on the run.
            if (run is not null) await run;
        }
    }

    /// <summary>One run, in a scope of its own. Never throws: what the job throws is logged and counted.</summary>
    private async Task RunOnceAsync(JobState state)
    {
        var job = state.Job;
        var clock = Stopwatch.StartNew();
        string? error = null;

        state.Started(DateTimeOffset.UtcNow);
        try
        {
            await using var scope = _set.CreateJobScope();
            await FunctionJobs.InvokeAsync(job.Method, FunctionJobs.BindArguments(job.Method, _binding, scope.Services));
        }
        catch (OperationCanceledException) when (_binding.Stopping.IsCancellationRequested)
        {
            _log.LogInformation("The job {Method} of {Owner}'s functions was stopped part way through a run", job.Name, _owner);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.LogError(ex, "The job {Method} of {Owner}'s functions failed", job.Name, _owner);
        }
        finally
        {
            state.Finished(clock.Elapsed, error);
        }
    }

    /// <summary>
    /// Waits until the wall clock reads <paramref name="due"/>. A long wait goes in steps of at
    /// most an hour, checked against the clock after each, so it neither overflows a timer nor
    /// misses a correction to the clock made meanwhile.
    /// </summary>
    private static async Task WaitUntilAsync(DateTimeOffset due, CancellationToken stopping)
    {
        while (true)
        {
            stopping.ThrowIfCancellationRequested();

            var wait = due - DateTimeOffset.UtcNow;
            if (wait <= TimeSpan.Zero) return;

            // Rounded up to whole milliseconds, which is what a timer counts in, so the last step is never zero.
            await Task.Delay(wait < LongestStep ? TimeSpan.FromMilliseconds(Math.Ceiling(wait.TotalMilliseconds)) : LongestStep, stopping);
        }
    }

    // ---------------------------------------------------------------- state

    private sealed class BackgroundState(MethodInfo method)
    {
        private readonly Lock _gate = new();
        private FunctionBackgroundState _state = FunctionBackgroundState.Running;
        private int _restarts;
        private string? _lastError;
        private DateTimeOffset? _startedUtc;

        public MethodInfo Method { get; } = method;

        public string Name { get; } = FunctionJobs.NameOf(method);

        public void Started(DateTimeOffset at)
        {
            lock (_gate)
            {
                _state = FunctionBackgroundState.Running;
                _startedUtc = at;
            }
        }

        public void Failed(string message)
        {
            lock (_gate)
            {
                _state = FunctionBackgroundState.Restarting;
                _lastError = message;
            }
        }

        public void Restarting()
        {
            lock (_gate) _restarts++;
        }

        public void Ended(FunctionBackgroundState state)
        {
            lock (_gate) _state = state;
        }

        public FunctionBackgroundStatus Snapshot()
        {
            lock (_gate) return new FunctionBackgroundStatus(Name, _state, _restarts, _lastError, _startedUtc);
        }
    }

    private sealed class JobState(FunctionJobs.Job job)
    {
        private readonly Lock _gate = new();
        private DateTimeOffset? _next;
        private DateTimeOffset? _lastStarted;
        private DateTimeOffset? _lastFinished;
        private TimeSpan? _lastDuration;
        private string? _lastError;
        private DateTimeOffset? _lastErrorUtc;
        private int _runs;
        private int _failures;
        private int _skipped;
        private bool _running;

        public FunctionJobs.Job Job { get; } = job;

        public void Due(DateTimeOffset? next)
        {
            lock (_gate) _next = next;
        }

        /// <summary>Counts a skipped occurrence, and returns when the run that caused it started.</summary>
        public DateTimeOffset? Skipped()
        {
            lock (_gate)
            {
                _skipped++;
                return _lastStarted;
            }
        }

        public void Started(DateTimeOffset at)
        {
            lock (_gate)
            {
                _running = true;
                _lastStarted = at;
            }
        }

        public void Finished(TimeSpan took, string? error)
        {
            lock (_gate)
            {
                _running = false;
                _lastFinished = DateTimeOffset.UtcNow;
                _lastDuration = took;
                _runs++;

                if (error is null) return;

                _failures++;
                _lastError = error;
                _lastErrorUtc = _lastFinished;
            }
        }

        public FunctionJobStatus Snapshot()
        {
            lock (_gate)
            {
                return new FunctionJobStatus(Job.Display, Job.Name, Job.Schedule, _next, _lastStarted, _lastFinished,
                    _lastDuration, _lastError, _lastErrorUtc, _runs, _failures, _running, _skipped);
            }
        }
    }
}
