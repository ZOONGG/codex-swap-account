using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay;

internal sealed class WindowsDesktopProcessCatalog : IDesktopProcessCatalog
{
    // Full package names are immutable/versioned. Avoid reading the manifest on
    // every window refresh and every Electron helper observation.
    private static readonly ConcurrentDictionary<string, string[]> ManifestExecutables = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "codex", "chatgpt", "CodexProfileOverlay", "code", "devenv", "cmd", "powershell", "pwsh",
        "windowsterminal", "wt", "conhost", "openconsole", "bash", "wsl", "chrome", "msedge", "firefox", "explorer",
    };

    internal static bool IsProtectedProcess(string name, string? family, bool officialDesktop) => !officialDesktop
        && (ProtectedNames.Contains(name) || (family?.StartsWith("OpenAI.", StringComparison.OrdinalIgnoreCase) == true
            && !family.Equals(OfficialCodexDesktopIdentity.PackageFamily, StringComparison.OrdinalIgnoreCase)));
    public IReadOnlyList<DesktopProcessEntry> Snapshot()
    {
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
        var result = new List<DesktopProcessEntry>();
        if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        do { result.Add(new((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile)); }
        while (Process32Next(snapshot, ref entry));
        if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result;
    }

    public IDesktopProcessHandle? Open(int processId)
    {
        SafeProcessHandle handle = OpenProcess(0x00101000, false, processId); // QUERY_LIMITED_INFORMATION | SYNCHRONIZE
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == 87) return null; // Exited before handle acquisition.
            throw new Win32Exception(error, "Could not verify a candidate Codex process safely.");
        }
        try { return new WindowsProcessHandle(processId, handle); }
        catch { handle.Dispose(); throw; }
    }

    internal static bool IsOfficialDesktop(int processId)
    {
        using var handle = new WindowsDesktopProcessCatalog().Open(processId);
        return handle?.IsOfficialDesktop == true && handle.ExitedAt is null;
    }

    private sealed class WindowsProcessHandle : IDesktopProcessHandle
    {
        private readonly SafeProcessHandle handle;
        public int Id { get; }
        public DateTimeOffset StartedAt { get; }
        public bool IsOfficialDesktop { get; }
        public bool IsProtected { get; }
        public DateTimeOffset? ExitedAt
        {
            get
            {
                uint wait = WaitForSingleObject(handle, 0);
                if (wait == 258) return null; // WAIT_TIMEOUT: exit is not complete yet.
                if (wait != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!GetProcessTimes(handle, out _, out long exit, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return DateTimeOffset.FromFileTime(exit);
            }
        }

        public WindowsProcessHandle(int id, SafeProcessHandle handle)
        {
            Id = id;
            this.handle = handle;
            if (!GetProcessTimes(handle, out long created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            StartedAt = DateTimeOffset.FromFileTime(created);
            var path = new StringBuilder(32768);
            uint size = (uint)path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref size))
            {
                if (ExitedAt is not null) { IsProtected = true; return; }
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            string? family = ReadPackageString(handle, GetPackageFamilyName);
            string? fullName = ReadPackageString(handle, GetPackageFullName);
            string? root = null;
            if (fullName is not null)
            {
                uint length = 0;
                if (GetPackagePathByFullName(fullName, ref length, null) == 122)
                {
                    var buffer = new StringBuilder((int)length);
                    if (GetPackagePathByFullName(fullName, ref length, buffer) == 0) root = buffer.ToString();
                }
            }
            if (family?.Equals(OfficialCodexDesktopIdentity.PackageFamily, StringComparison.OrdinalIgnoreCase) == true && root is not null)
            {
                string[] executables = ManifestExecutables.GetOrAdd(fullName!, _ => XDocument.Load(Path.Combine(root, "AppxManifest.xml"))
                    .Descendants().Where(e => e.Name.LocalName == "Application")
                    .Select(e => (string?)e.Attribute("Executable")).OfType<string>().ToArray());
                IsOfficialDesktop = executables
                    .Any(executable => OfficialCodexDesktopIdentity.IsOfficial(family, root, path.ToString(), executable));
            }
            string name = Path.GetFileNameWithoutExtension(path.ToString());
            IsProtected = IsProtectedProcess(name, family, IsOfficialDesktop);
        }

        public void RequestClose()
        {
            if (ExitedAt is not null || IsProtected) return;
            try
            {
                using var process = Process.GetProcessById(Id);
                _ = process.CloseMainWindow();
            }
            catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException or Win32Exception)
                && ExitedAt is not null)
            { }
        }

        public void Terminate()
        {
            if (ExitedAt is not null || IsProtected) return;
            using SafeProcessHandle terminateHandle = OpenProcess(0x1001, false, Id); // TERMINATE | QUERY_LIMITED_INFORMATION
            if (terminateHandle.IsInvalid)
            {
                if (ExitedAt is not null) return;
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (!GetProcessTimes(terminateHandle, out long created, out _, out _, out _)
                || DateTimeOffset.FromFileTime(created) != StartedAt)
                throw new InvalidOperationException("Process identity changed during shutdown.");
            if (!TerminateProcess(terminateHandle, 1) && ExitedAt is null) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public void Dispose() => handle.Dispose();
    }

    private delegate int PackageStringReader(SafeProcessHandle process, ref uint length, StringBuilder? value);
    private static string? ReadPackageString(SafeProcessHandle handle, PackageStringReader reader)
    {
        uint length = 0;
        int result = reader(handle, ref length, null);
        if (result == 15700) return null; // APPMODEL_ERROR_NO_PACKAGE
        if (result != 122) throw new Win32Exception(result);
        var buffer = new StringBuilder((int)length);
        result = reader(handle, ref length, buffer);
        if (result != 0) throw new Win32Exception(result);
        return buffer.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, StringBuilder? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackageFullName(SafeProcessHandle process, ref uint length, StringBuilder? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagePathByFullName(string fullName, ref uint length, StringBuilder? path);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
