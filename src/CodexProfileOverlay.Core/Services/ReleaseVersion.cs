using System.Numerics;
using System.Text.RegularExpressions;

namespace CodexProfileOverlay.Core.Services;

public sealed record ReleaseVersion(BigInteger Major, BigInteger Minor, BigInteger Patch, string? Prerelease) : IComparable<ReleaseVersion>
{
    public static ReleaseVersion Parse(string value)
    {
        var match = Regex.Match(value, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new FormatException("Invalid release version.");
        string? pre = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (pre?.Split('.').Any(x => x.Length > 1 && x[0] == '0' && x.All(char.IsAsciiDigit)) == true) throw new FormatException("Invalid prerelease.");
        return new(BigInteger.Parse(match.Groups[1].Value), BigInteger.Parse(match.Groups[2].Value), BigInteger.Parse(match.Groups[3].Value), pre);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool an = BigInteger.TryParse(a[i], out var av), bn = BigInteger.TryParse(b[i], out var bv);
            result = an && bn ? av.CompareTo(bv) : an != bn ? (an ? -1 : 1) : string.CompareOrdinal(a[i], b[i]);
            if (result != 0) return result;
        }
        return a.Length.CompareTo(b.Length);
    }
}
