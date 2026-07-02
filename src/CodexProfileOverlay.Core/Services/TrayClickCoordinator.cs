namespace CodexProfileOverlay.Core.Services;

public sealed class TrayClickCoordinator
{
    public TimeSpan DoubleClickWindow { get; init; } = TimeSpan.FromMilliseconds(260);

    private DateTimeOffset? pendingLeftClickAt;

    public bool HasPendingSingleClick => pendingLeftClickAt is not null;

    public void RegisterLeftClick(DateTimeOffset now)
    {
        pendingLeftClickAt = now;
    }

    public bool RegisterLeftDoubleClick()
    {
        bool suppressedSingleClick = pendingLeftClickAt is not null;
        pendingLeftClickAt = null;
        return suppressedSingleClick;
    }

    public bool TryConsumeDueSingleClick(DateTimeOffset now)
    {
        if (pendingLeftClickAt is null || now - pendingLeftClickAt.Value < DoubleClickWindow)
        {
            return false;
        }

        pendingLeftClickAt = null;
        return true;
    }

    public void Cancel()
    {
        pendingLeftClickAt = null;
    }
}
