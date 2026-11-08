namespace CodexProfileOverlay.Core.Services;

// Used from the controller's dispatcher. A profile operation reserves its existing
// switching/accountOperation flag before draining, so no new CLI work can start.
public sealed class ProfileUsageOperations
{
    private readonly Dictionary<string, Job> jobs = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> RunAsync(string profile, Func<CancellationToken, Task> run, CancellationToken token)
    {
        if (token.IsCancellationRequested || jobs.ContainsKey(profile)) return false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new Job(cancellation, finished.Task);
        jobs.Add(profile, job);
        try
        {
            await run(cancellation.Token);
            return !cancellation.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            try { await job.CancellationCallbacks; }
            finally
            {
                jobs.Remove(profile);
                finished.SetResult();
            }
        }
    }

    public async Task DrainAsync(CancellationToken token, params string?[] profiles)
    {
        token.ThrowIfCancellationRequested();
        Job[] matching = (profiles.Length == 0
            ? jobs.Values.AsEnumerable()
            : profiles.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(profile => jobs.GetValueOrDefault(profile)).OfType<Job>()).ToArray();

        // CancelAsync signals cancellation immediately and runs callbacks off the dispatcher.
        Task[] stopping = matching.Select(job => Task.WhenAll(job.RequestCancellation(), job.Finished)).ToArray();
        await Task.WhenAll(stopping).WaitAsync(TimeSpan.FromSeconds(8), token);
        token.ThrowIfCancellationRequested();
    }

    private sealed class Job(CancellationTokenSource cancellation, Task finished)
    {
        private Task? callbacks;
        public Task Finished => finished;
        public Task CancellationCallbacks => callbacks ?? Task.CompletedTask;
        public Task RequestCancellation() => callbacks ??= cancellation.CancelAsync();
    }
}
