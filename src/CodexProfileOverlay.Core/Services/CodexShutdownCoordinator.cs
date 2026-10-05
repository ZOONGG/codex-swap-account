namespace CodexProfileOverlay.Core.Services;

public interface ICodexProcessSession
{
    bool ObservedDesktop { get; }
    IReadOnlyList<int> FindRunning();
    void RequestClose(int processId);
    void Terminate(int processId);
}

public interface IShutdownClock
{
    TimeSpan Elapsed { get; }
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}

public sealed class CodexShutdownCoordinator
{
    private readonly IShutdownClock clock;
    private readonly TimeSpan quietPeriod;
    private readonly TimeSpan forceTimeout;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public CodexShutdownCoordinator(IShutdownClock? clock = null, TimeSpan? quietPeriod = null, TimeSpan? forceTimeout = null)
    {
        this.clock = clock ?? new ShutdownClock();
        this.quietPeriod = quietPeriod ?? TimeSpan.FromMilliseconds(750);
        this.forceTimeout = forceTimeout ?? TimeSpan.FromSeconds(5);
    }

    public async Task CloseAsync(ICodexProcessSession session, TimeSpan gracefulTimeout, bool allowForceClose,
        Action cleanup, Action warnCleanupFailed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await WaitAsync(session, gracefulTimeout + quietPeriod, false, cancellationToken).ConfigureAwait(false))
        {
            if (!allowForceClose || !await WaitAsync(session, forceTimeout + quietPeriod, true, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Codex Desktop or its children are still running. Authorization was not changed.");
        }

        VerifyStopped(session, cancellationToken);
        if (session.ObservedDesktop)
        {
            try { cleanup(); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A broken warning sink must not turn compatibility polish into an
                // authorization failure either. Cancellation still propagates.
                try { warnCleanupFailed(); }
                catch (Exception warningException) when (warningException is not OperationCanceledException) { }
            }
        }
        // Cleanup is best-effort, but a respawn is an authorization safety failure.
        VerifyStopped(session, cancellationToken);
    }

    public static void VerifyStopped(ICodexProcessSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session.FindRunning().Count != 0)
            throw new InvalidOperationException("Codex Desktop restarted during shutdown. Authorization was not changed.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<bool> WaitAsync(ICodexProcessSession session, TimeSpan timeout, bool force, CancellationToken token)
    {
        TimeSpan deadline = clock.Elapsed + timeout;
        TimeSpan? emptySince = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<int> running = session.FindRunning();
            if (running.Count == 0)
            {
                emptySince ??= clock.Elapsed;
                if (clock.Elapsed - emptySince.Value >= quietPeriod) return true;
            }
            else
            {
                emptySince = null;
                foreach (int id in running)
                {
                    token.ThrowIfCancellationRequested();
                    if (force) session.Terminate(id);
                    else session.RequestClose(id);
                }
            }
            if (clock.Elapsed >= deadline) return false;
            await clock.DelayAsync(PollInterval, token).ConfigureAwait(false);
        }
    }

    private sealed class ShutdownClock : IShutdownClock
    {
        private readonly System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
        public TimeSpan Elapsed => timer.Elapsed;
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.Delay(duration, cancellationToken);
    }
}
