using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodexProfileOverlay.Core.Services;

internal static class AuthSwitchFiles
{
    internal static void MoveDurably(string source, string destination, bool overwrite = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (Directory.Exists(source)) Directory.Move(source, destination);
            else File.Move(source, destination, overwrite);
            return;
        }

        const int writeThrough = 8;
        const int replaceExisting = 1;
        if (!MoveFileEx(source, destination, writeThrough | (overwrite ? replaceExisting : 0)))
            throw new IOException("Could not persist an authorization transaction rename.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string destination, int flags);
}
