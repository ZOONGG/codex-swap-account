namespace CodexProfileOverlay.Core.Services;

public sealed record DesktopProcessEntry(int Id, int ParentId, string Name);

public interface IDesktopProcessHandle : IDisposable
{
    int Id { get; }
    DateTimeOffset StartedAt { get; }
    DateTimeOffset? ExitedAt { get; }
    bool IsOfficialDesktop { get; }
    bool IsProtected { get; }
    void RequestClose();
    void Terminate();
}

public interface IDesktopProcessCatalog
{
    IReadOnlyList<DesktopProcessEntry> Snapshot();
    IDesktopProcessHandle? Open(int processId);
}

/// <summary>Owns retained handles, including exited parents. Never kills by PID/name/tree.</summary>
public sealed class CodexDesktopProcessSession(IDesktopProcessCatalog catalog, int switcherId) : ICodexProcessSession, IDisposable
{
    private readonly Dictionary<int, IDesktopProcessHandle> tracked = new();
    private readonly HashSet<int> passive = new();
    public bool ObservedDesktop { get; private set; }

    public IReadOnlyList<int> FindRunning()
    {
        var entries = catalog.Snapshot();
        var excluded = new HashSet<int>(entries.Where(e => e.Id == switcherId
            || e.Name.Equals("CodexProfileOverlay.exe", StringComparison.OrdinalIgnoreCase)).Select(e => e.Id));
        ExpandExcluded(entries, excluded);

        foreach (DesktopProcessEntry entry in entries.Where(e => !excluded.Contains(e.Id)
            && (e.Name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                || e.Name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase))))
        {
            if (tracked.TryGetValue(entry.Id, out var known) && known.ExitedAt is null) continue;
            IDesktopProcessHandle? handle = catalog.Open(entry.Id);
            if (handle is null) continue;
            if (!handle.IsOfficialDesktop) { handle.Dispose(); continue; }
            ObservedDesktop = true;
            Retain(handle);
        }

        bool changed;
        do
        {
            changed = false;
            foreach (DesktopProcessEntry entry in entries)
            {
                if (excluded.Contains(entry.Id) || (tracked.TryGetValue(entry.Id, out var existing) && existing.ExitedAt is null)
                    || !tracked.TryGetValue(entry.ParentId, out var parent)) continue;
                // If the PID has been reused, its current process is not our retained parent.
                using IDesktopProcessHandle? currentParent = catalog.Open(entry.ParentId);
                if (currentParent is not null && currentParent.StartedAt != parent.StartedAt)
                    throw new InvalidOperationException("A tracked parent PID was reused; shutdown cannot be verified safely.");
                IDesktopProcessHandle? child = catalog.Open(entry.Id);
                if (child is null) continue;
                if (existing is not null && existing.StartedAt == child.StartedAt)
                {
                    child.Dispose();
                    continue; // Retain the exited identity, but do not rediscover it endlessly.
                }
                if (child.StartedAt < parent.StartedAt
                    || (parent.ExitedAt is { } exited && child.StartedAt > exited))
                {
                    child.Dispose();
                    continue;
                }
                Retain(child, parent.IsProtected || passive.Contains(parent.Id));
                changed = true;
            }
        } while (changed);

        return tracked.Values.Where(p => p.ExitedAt is null).Select(p => p.Id).ToArray();
    }

    public void RequestClose(int processId)
    {
        var handle = tracked[processId];
        if (handle.ExitedAt is null && !handle.IsProtected && !passive.Contains(processId)) handle.RequestClose();
    }

    public void Terminate(int processId)
    {
        var handle = tracked[processId];
        if (handle.ExitedAt is null && !handle.IsProtected && !passive.Contains(processId)) handle.Terminate();
    }

    private void Retain(IDesktopProcessHandle handle, bool inheritedProtection = false)
    {
        if (tracked.Remove(handle.Id, out var old)) old.Dispose();
        tracked.Add(handle.Id, handle);
        passive.Remove(handle.Id);
        if (inheritedProtection) passive.Add(handle.Id);
    }

    private static void ExpandExcluded(IReadOnlyList<DesktopProcessEntry> entries, HashSet<int> excluded)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var entry in entries)
                if (excluded.Contains(entry.ParentId)) changed |= excluded.Add(entry.Id);
        } while (changed);
    }

    public void Dispose()
    {
        foreach (var handle in tracked.Values) handle.Dispose();
        tracked.Clear();
        passive.Clear();
    }
}
