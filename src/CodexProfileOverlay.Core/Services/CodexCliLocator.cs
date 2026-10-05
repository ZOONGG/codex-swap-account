using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodexProfileOverlay.Core.Services;

public static class CodexCliLocator
{
    public static string? FindExecutable() => FindExecutable(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetEnvironmentVariable("PATH"));

    public static string? FindExecutable(string localAppData, string? searchPath)
    {
        if (!Path.IsPathFullyQualified(localAppData)) return FindOnPath(searchPath);
        string installed = Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        if (File.Exists(installed) && IsAllowedPath(installed, localAppData)) return installed;

        string bundled = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        if (Directory.Exists(bundled) && IsAllowedPath(bundled, localAppData))
        {
            try
            {
                var candidates = new List<string>();
                string current = Path.Combine(bundled, "codex.exe");
                if (File.Exists(current) && IsAllowedPath(current, localAppData)) candidates.Add(current);
                foreach (string directory in Directory.EnumerateDirectories(bundled).Where(d => !IsReparsePoint(d)))
                {
                    string candidate = Path.Combine(directory, "codex.exe");
                    if (File.Exists(candidate) && IsAllowedPath(candidate, localAppData)) candidates.Add(candidate);
                }
                string? best = candidates.Select(Rank).OfType<Candidate>()
                    .OrderByDescending(c => c.Version).ThenByDescending(c => c.Modified)
                    .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Select(c => c.Path).FirstOrDefault();
                if (best is not null) return best;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        return FindOnPath(searchPath);
    }

    private static string? FindOnPath(string? searchPath)
    {
        if (string.IsNullOrWhiteSpace(searchPath)) return null;
        foreach (string directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim('"'), "codex.exe");
                if (File.Exists(candidate)) return candidate; // Preserve explicit PATH fallback, including shims.
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsAllowedPath(string path, string root)
    {
        try
        {
            string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string? current = Path.GetFullPath(path);
            if (!current.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            while (current is not null && !current.Equals(basePath, StringComparison.OrdinalIgnoreCase))
            {
                if (IsReparsePoint(current)) return false;
                current = Path.GetDirectoryName(current);
            }
            return current is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static Candidate? Rank(string path)
    {
        try { return new Candidate(path, ReadVersion(path), File.GetLastWriteTimeUtc(path)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static CliVersion? ReadVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            CliVersion? version = CliVersion.Parse(info.ProductVersion) ?? CliVersion.Parse(info.FileVersion);
            if (version is not null) return version;
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception) { }
        return CliVersion.Parse(Path.GetFileName(Path.GetDirectoryName(path)));
    }

    private sealed record Candidate(string Path, CliVersion? Version, DateTime Modified);

    private sealed record CliVersion(Version Numeric, string[] Prerelease) : IComparable<CliVersion>
    {
        public static CliVersion? Parse(string? value)
        {
            if (value is null) return null;
            Match match = Regex.Match(value.Trim(), @"\Av?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?\z",
                RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int major)
                || !int.TryParse(match.Groups[2].Value, out int minor) || !int.TryParse(match.Groups[3].Value, out int patch)) return null;
            int revision = 0;
            if (match.Groups[4].Success && !int.TryParse(match.Groups[4].Value, out revision)) return null;
            return new(new Version(major, minor, patch, revision), match.Groups[5].Success ? match.Groups[5].Value.Split('.') : []);
        }

        public int CompareTo(CliVersion? other)
        {
            if (other is null) return 1;
            int result = Numeric.CompareTo(other.Numeric);
            if (result != 0) return result;
            if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
                return (Prerelease.Length == 0 ? 1 : 0).CompareTo(other.Prerelease.Length == 0 ? 1 : 0);
            for (int i = 0; i < Math.Min(Prerelease.Length, other.Prerelease.Length); i++)
            {
                string left = Prerelease[i], right = other.Prerelease[i];
                bool leftNumeric = left.All(char.IsAsciiDigit), rightNumeric = right.All(char.IsAsciiDigit);
                result = leftNumeric && rightNumeric ? CompareNumeric(left, right)
                    : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : StringComparer.Ordinal.Compare(left, right);
                if (result != 0) return result;
            }
            return Prerelease.Length.CompareTo(other.Prerelease.Length);
        }

        private static int CompareNumeric(string left, string right)
        {
            left = left.TrimStart('0'); right = right.TrimStart('0');
            int length = left.Length.CompareTo(right.Length);
            return length != 0 ? length : StringComparer.Ordinal.Compare(left, right);
        }
    }
}
