namespace CodexProfileOverlay.Core.Services;

public static class ProfileName
{
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string trimmed = name.Trim();
        if (!string.Equals(name, trimmed, StringComparison.Ordinal) || trimmed is "." or "..")
        {
            return false;
        }

        string stem = trimmed.Split('.')[0].TrimEnd(' ');
        if (trimmed.Length > 255 || trimmed.EndsWith('.') ||
            System.Text.RegularExpressions.Regex.IsMatch(stem, @"\A(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;

        return trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !trimmed.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !trimmed.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static string RequireValid(string name)
    {
        if (!IsValid(name))
        {
            throw new ArgumentException("Profile name is invalid.", nameof(name));
        }

        return name;
    }
}
