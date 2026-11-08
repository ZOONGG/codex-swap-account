using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CodexProfileOverlay.Core.Services;

public sealed partial class DesktopSessionService
{
    private const string RestartReceiptName = "restart-verified.json";
    private sealed record RestartReceipt(Guid Id, DateTimeOffset VerifiedUtc);

    public void TryCleanupCompletedSessions(IReadOnlyList<string> liveCandidates, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !Monitor.TryEnter(RetentionGate)) return;
        try
        {
            CheckRetentionCancellation(token);
            WithPinnedDirectory(JournalRoot, () =>
            {
                if (HasPendingOperation) return;
                foreach (string directory in Directory.EnumerateDirectories(JournalRoot))
                {
                    CheckRetentionCancellation(token);
                    if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid id)) continue;
                    try { CleanupCommittedSession(directory, id, liveCandidates, token); }
                    catch (Exception error) when (IsRetentionError(error))
                    {
                        logger?.Error("Could not clean a completed Desktop session.", error);
                    }
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (IsRetentionError(error))
        {
            logger?.Error("Desktop session retention could not run.", error);
        }
        finally { Monitor.Exit(RetentionGate); }
    }

    private void CleanupCommittedSession(string directory, Guid id, IReadOnlyList<string> liveCandidates,
        CancellationToken token)
    {
        using var directoryPin = PinRetentionEntry(directory, directory: true);
        string receipt = Path.Combine(directory, RestartReceiptName);
        string committed = Path.Combine(directory, "committed.json");
        if (!DesktopSnapshot.Exists(receipt) || !DesktopSnapshot.Exists(committed)
            || DesktopSnapshot.Exists(Path.Combine(directory, "rolled-back.json"))) return;

        // Use the same grace period as temporary authorization backups, measured from restart confirmation.
        var restart = ReadRetentionFile<RestartReceipt>(receipt);
        if (restart.Id != id || restart.VerifiedUtc == default
            || utcNow() - restart.VerifiedUtc < BackupMaintenanceService.TemporaryMaximumAge) return;
        var journal = ReadRetentionFile<Journal>(committed);
        if (journal.Id != id || journal.Capture || !IsValidJournal(journal)
            || string.Equals(journal.Target, journal.Previous, StringComparison.OrdinalIgnoreCase)) return;

        string[] matchingPaths = liveCandidates.Where(candidate => Fingerprint(candidate) == journal.LiveFingerprint).ToArray();
        if (matchingPaths.Length != 1) return;
        string live = matchingPaths[0];
        if (!HasSupportedLayout(live)) return;
        var layout = new Layout(paths, journal, live);
        if (!string.Equals(directory, layout.JournalDirectory, StringComparison.Ordinal)
            || !IsSetMarker(layout.Applied)) return;
        int count = layout.Steps().Count;
        if (Directory.EnumerateFileSystemEntries(directory, "move-*.intent*").Count() != count) return;
        for (int i = 0; i < count; i++)
        {
            if (!IsSetMarker(layout.Intent(i)) || DesktopSnapshot.Exists(layout.Intent(i) + ".undone")) return;
        }

        if (journal.TargetDesktop)
            CleanupRetiredTree(layout.OriginalLive, liveCandidates, token);
        if (journal.PreviousDesktop)
            CleanupRetiredTree(layout.OriginalProfileSession, liveCandidates, token);
    }

    private void CleanupRetiredTree(string tree, IReadOnlyList<string> liveCandidates, CancellationToken token)
    {
        if (!IsCanonicalLocalPath(tree)
            || liveCandidates.Append(paths.SharedCodexDirectory).Append(paths.ApplicationDataDirectory)
                .Any(root => Within(tree, root) || Within(root, tree))) return;
        WithPinnedDirectory(Path.GetDirectoryName(tree)!, () =>
        {
            if (!DesktopSnapshot.Exists(tree)) return;
            // Reject an unsafe tree before deleting any of its entries. Recheck each entry
            // under a handle during deletion, since a process can change the tree between passes.
            VisitRetiredEntry(ExtendedPath(tree), directory: true, delete: false, depth: 0, token);
            VisitRetiredEntry(ExtendedPath(tree), directory: true, delete: true, depth: 0, token);
        });
    }

    private static void VisitRetiredEntry(string path, bool directory, bool delete, int depth, CancellationToken token)
    {
        CheckRetentionCancellation(token);
        if (depth > 64 || Path.GetFileName(path).Equals("auth.json", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Unexpected entry in a retired Desktop session.");
        using var handle = PinRetentionEntry(path, directory, delete);
        if (directory)
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(path))
            {
                bool childDirectory = (File.GetAttributes(child) & FileAttributes.Directory) != 0;
                VisitRetiredEntry(child, childDirectory, delete, depth + 1, token);
            }
        }
        CheckRetentionCancellation(token);
        if (delete && !SetFileInformationByHandle(handle, 4 /* FileDispositionInfo */, 1, 1))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static void WithPinnedDirectory(string directory, Action action)
    {
        if (!IsCanonicalLocalPath(directory))
            throw new IOException("Desktop retention requires a canonical local directory.");
        RequireLocalVolume(directory);
        var ancestors = new Stack<string>();
        for (string? path = directory; path is not null; path = Path.GetDirectoryName(path))
            ancestors.Push(path);
        var handles = new Stack<SafeFileHandle>();
        try
        {
            while (ancestors.TryPop(out string? path))
            {
                try { handles.Push(PinRetentionEntry(path, directory: true)); }
                catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return; }
            }
            action();
        }
        finally
        {
            while (handles.TryPop(out var handle)) handle.Dispose();
        }
    }

    private static SafeFileHandle PinRetentionEntry(string path, bool directory, bool delete = false)
    {
        // Open the entry itself and deny rename/delete sharing. With its ancestors pinned,
        // subsequent path operations cannot be redirected through a replacement junction.
        const uint readDataOrListDirectory = 1, readAttributes = 0x80, deleteAccess = 0x10000;
        const uint backupSemantics = 0x02000000, openReparsePoint = 0x00200000;
        var handle = CreateFile(ExtendedPath(path), readDataOrListDirectory | readAttributes | (delete ? deleteAccess : 0),
            FileShare.Read, IntPtr.Zero, FileMode.Open, backupSemantics | openReparsePoint, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var info, 8))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0
                || ((info.Attributes & FileAttributes.Directory) != 0) != directory)
                throw new IOException("Unsafe Desktop retention entry.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string ExtendedPath(string path)
    {
        // Preserve literal names, including trailing characters, during native opens and enumeration.
        const string prefix = @"\\?\";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path : prefix + path;
    }

    private static FileStream OpenRetentionFile(string path)
    {
        using var pin = PinRetentionEntry(path, directory: false);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static T ReadRetentionFile<T>(string path)
    {
        using var stream = OpenRetentionFile(path);
        if (stream.Length > 16 * 1024) throw new InvalidDataException("Desktop retention record is too large.");
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Missing Desktop retention record.");
    }

    private static bool IsSetMarker(string path)
    {
        if (!DesktopSnapshot.Exists(path)) return false;
        using var stream = OpenRetentionFile(path);
        return stream.Length == 1 && stream.ReadByte() == 1;
    }

    private static void CheckRetentionCancellation(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref transactionsStarting) != 0) throw new OperationCanceledException();
    }

    private static bool IsRetentionError(Exception error) =>
        error is IOException or UnauthorizedAccessException or Win32Exception or JsonException or InvalidDataException;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        FileMode creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, in byte delete, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo
    {
        public FileAttributes Attributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out AttributeTagInfo info, uint size);
}
