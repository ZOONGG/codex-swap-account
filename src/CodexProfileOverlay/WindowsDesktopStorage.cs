using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay;

internal static class WindowsDesktopStorage
{
    internal static IReadOnlyList<string> Candidates(AppPaths paths)
    {
        string package = Path.Combine(paths.LocalAppData, "Packages", OfficialCodexDesktopIdentity.PackageFamily);
        return new[]
        {
            Path.Combine(package, "LocalCache", "Roaming"),
            Path.Combine(package, "LocalCache", "Local"),
            Path.Combine(package, "LocalState"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            paths.LocalAppData,
        }.Where(Path.IsPathFullyQualified).Select(root => Path.GetFullPath(Path.Combine(root, "Codex", "web", "Codex")))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string Resolve(AppPaths paths, DesktopSessionService service, string? profile, bool capture)
    {
        string? homeOverride = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(homeOverride) && !string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(homeOverride)), paths.SharedCodexDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A custom Codex home cannot be paired safely with Desktop data.");
        var candidates = Candidates(paths);
        if (service.HasPendingOperation) return candidates.Single(service.MatchesPendingPath);

        using var session = new CodexDesktopProcessSession(new WindowsDesktopProcessCatalog(), Environment.ProcessId);
        _ = session.FindRunning();
        if (!session.ObservedDesktop && !capture)
        {
            // A closed app cannot prove ownership through open files. Use the path
            // attested at capture and require the expected Chromium layout still exists.
            return candidates.Single(path => service.MatchesCapturedPath(profile!, path) && service.HasSupportedLayout(path));
        }

        if (!session.ObservedDesktop) throw new InvalidOperationException("Open the official Codex Desktop app before capture.");
        return candidates.Single(path => (capture || service.MatchesCapturedPath(profile!, path))
            && service.HasSupportedLayout(path) && IsInUseByOfficialCodex(service.GetAttestationFiles(path)));
    }

    internal static IDisposable PreventLaunch(PreparedCodexLaunch launch, CodexDesktopProcessSession session)
    {
        if (launch.Executables.Count == 0) throw new InvalidOperationException("The official Codex executable could not be verified.");
        var lease = new LaunchLease();
        try
        {
            // On Windows FILE_EXECUTE participates in read sharing. Holding this
            // handle prevents a new image section for each registered Desktop entry.
            foreach (string executable in launch.Executables)
                lease.Handles.Add(new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.None));
            CodexShutdownCoordinator.VerifyStopped(session, CancellationToken.None);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static bool IsInUseByOfficialCodex(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return false;

        var key = new StringBuilder(33);
        int error = RmStartSession(out uint handle, 0, key);
        if (error != 0) throw new Win32Exception(error);
        try
        {
            error = RmRegisterResources(handle, (uint)files.Count, files.ToArray(), 0, IntPtr.Zero, 0, IntPtr.Zero);
            if (error != 0) throw new Win32Exception(error);
            uint size = 0;
            error = RmGetList(handle, out uint needed, ref size, null, out _);
            if (error == 0) return false;
            if (error != 234 || needed == 0 || needed > 1024) throw new Win32Exception(error);
            size = needed;
            var processes = new RestartProcessInfo[size];
            error = RmGetList(handle, out _, ref size, processes, out _);
            if (error != 0) throw new Win32Exception(error);
            var catalog = new WindowsDesktopProcessCatalog();
            foreach (var entry in processes.Take((int)size))
            {
                using var process = catalog.Open(entry.Process.ProcessId);
                long started = ((long)entry.Process.StartHigh << 32) | entry.Process.StartLow;
                if (process is { IsOfficialDesktop: true, ExitedAt: null } && process.StartedAt.ToFileTime() == started) return true;
            }
            return false;
        }
        finally { _ = RmEndSession(handle); }
    }

    private sealed class LaunchLease : IDisposable
    {
        internal List<FileStream> Handles { get; } = [];
        public void Dispose()
        {
            foreach (var file in Handles) file.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RestartProcess
    {
        public int ProcessId;
        public uint StartLow, StartHigh;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RestartProcessInfo
    {
        public RestartProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ApplicationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public uint ApplicationType, AppStatus, SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmRegisterResources(uint session, uint fileCount, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] files,
        uint processCount, IntPtr processes, uint serviceCount, IntPtr services);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RestartProcessInfo[]? processes, out uint reason);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
