using System.Text.RegularExpressions;

namespace CodexProfileOverlay.Core.Services;

public static class CodexLaunchIdentity
{
    public static string? Validate(string? appId)
    {
        string? value = appId?.Trim();
        return value is not null && Regex.IsMatch(value,
            @"\AOpenAI\.Codex_2p2nqsd0c76g0![A-Za-z0-9][A-Za-z0-9._-]{0,63}\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? value : null;
    }

    public static string? Select(IEnumerable<string> appIds) => appIds
        .Select(Validate).Where(id => id is not null)
        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
}
