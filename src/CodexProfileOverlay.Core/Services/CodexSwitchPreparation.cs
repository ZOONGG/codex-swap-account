namespace CodexProfileOverlay.Core.Services;

public static class CodexSwitchPreparation
{
    public static async Task<T?> PrepareAndCloseAsync<T>(bool relaunch,
        Func<Task<T>> prepareLaunch, Func<Task> shutdown, CancellationToken token) where T : class
    {
        token.ThrowIfCancellationRequested();
        T? target = relaunch ? await prepareLaunch().ConfigureAwait(true) : null;
        token.ThrowIfCancellationRequested();
        await shutdown().ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        return target;
    }
}
