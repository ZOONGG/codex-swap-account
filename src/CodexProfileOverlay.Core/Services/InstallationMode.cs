namespace CodexProfileOverlay.Core.Services;

public static class InstallationMode
{
    public const string AppId = "CodexSwapAccount";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexSwapAccount_is1";

    // A portable copy of an installed EXE must not inherit installed behavior.
    public static bool IsInstalled(string executable, string? registeredDirectory)
    {
        if (string.IsNullOrWhiteSpace(registeredDirectory)) return false;
        try
        {
            string directory = Path.GetFullPath(registeredDirectory).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(Path.GetDirectoryName(Path.GetFullPath(executable)), directory, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(directory, "unins000.exe"))
                && File.ReadAllText(Path.Combine(directory, "installation.id")).Trim() == AppId;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
}
