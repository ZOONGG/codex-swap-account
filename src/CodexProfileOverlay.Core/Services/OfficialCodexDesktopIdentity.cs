using System.Text.RegularExpressions;

namespace CodexProfileOverlay.Core.Services;

/// <summary>One authority for desktop identity; names alone never establish identity.</summary>
public static class OfficialCodexDesktopIdentity
{
    public const string PackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";

    public static bool IsOfficial(string? family, string? packageRoot, string? executable, string? registeredExecutable = null)
    {
        if (!string.Equals(family, PackageFamily, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(packageRoot) || string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        string root = packageRoot.Replace('/', '\\').TrimEnd('\\');
        string path = executable.Replace('/', '\\');
        if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        if (registeredExecutable is not null
            && !path.Equals(root + "\\" + registeredExecutable.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)) return false;
        string[] relative = path[(root.Length + 1)..].Split('\\');
        return relative.All(part => part.Length > 0 && part is not "." and not ".."
                && !part.Contains(':') && !part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                && !part.Equals("resources", StringComparison.OrdinalIgnoreCase))
            && (relative[^1].Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                || relative[^1].Equals("Codex.exe", StringComparison.OrdinalIgnoreCase));
    }

    // Strict fallback for callers which cannot obtain package APIs. Production uses
    // Windows-validated family/full-name/install-root instead of guessing a location.
    public static bool IsOfficialPackagePath(string? executable, string windowsAppsRoot)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        string root = windowsAppsRoot.Replace('/', '\\').TrimEnd('\\');
        string path = executable.Replace('/', '\\');
        if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        string folder = path[(root.Length + 1)..].Split('\\')[0];
        if (!Regex.IsMatch(folder, @"\AOpenAI\.Codex_\d+\.\d+\.\d+\.\d+_(x64|x86|arm64)__2p2nqsd0c76g0\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        return IsOfficial(PackageFamily, root + "\\" + folder, path);
    }
}
