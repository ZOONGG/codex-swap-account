using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class ProfileUsageOperationsTests
{
    [Fact]
    public async Task Drain_StopsOnlyMatchingWriterAndWaitsForFileRelease()
    {
        using var temp = new TempDirectory();
        string work = Path.Combine(temp.Path, "work");
        Directory.CreateDirectory(work);
        string auth = Path.Combine(work, "auth.json");
        File.WriteAllText(auth, "fake auth");

        var operations = new ProfileUsageOperations();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> other = operations.RunAsync("other", token => otherExit.Task.WaitAsync(token), CancellationToken.None);
        Task<bool> refresh = operations.RunAsync("work", async token =>
        {
            using var writer = File.Open(auth, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var registration = token.Register(() => cancelled.TrySetResult());
            await exit.Task;
            token.ThrowIfCancellationRequested();
        }, CancellationToken.None);

        Task drain = operations.DrainAsync(CancellationToken.None, "WORK");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(drain.IsCompleted);
        Assert.False(other.IsCompleted);
        Assert.False(await operations.RunAsync("work", _ => throw new InvalidOperationException(), CancellationToken.None));
        exit.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        Directory.Move(work, Path.Combine(temp.Path, "removed"));
        Assert.False(await refresh);
        otherExit.SetResult();
        Assert.True(await other);
    }

    [Fact]
    public async Task CancelledDrain_LeavesWriterTrackedUntilItExitsEvenIfItFails()
    {
        var operations = new ProfileUsageOperations();
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> refresh = operations.RunAsync("work", _ => exit.Task, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task drain = operations.DrainAsync(cancellation.Token, "work");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);
        Assert.False(await operations.RunAsync("work", _ => Task.CompletedTask, CancellationToken.None));

        Task retry = operations.DrainAsync(CancellationToken.None, "work");
        exit.SetException(new IOException("fake CLI failure"));
        await Assert.ThrowsAsync<IOException>(() => refresh);
        await retry.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await operations.RunAsync("work", _ => Task.CompletedTask, CancellationToken.None));
    }
}
