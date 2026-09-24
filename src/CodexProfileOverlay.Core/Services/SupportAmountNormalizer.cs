using System.Globalization;

namespace CodexProfileOverlay.Core.Services;

public static class SupportAmountNormalizer
{
    private const NumberStyles SupportedNumberStyles =
        NumberStyles.AllowLeadingWhite |
        NumberStyles.AllowTrailingWhite |
        NumberStyles.AllowLeadingSign |
        NumberStyles.AllowDecimalPoint;

    public static bool TryNormalize(
        string? value,
        int minimum,
        int maximum,
        CultureInfo culture,
        out int amount)
    {
        amount = 0;
        if (!TryParse(value, minimum, maximum, culture, out decimal parsed))
        {
            return false;
        }

        amount = NormalizeParsed(parsed, minimum, maximum);
        return true;
    }

    public static bool TryResolveSelection(
        string? value,
        int minimum,
        int maximum,
        IReadOnlyList<int> presets,
        CultureInfo culture,
        out int amount,
        out int? selectedPreset)
    {
        amount = 0;
        selectedPreset = null;
        if (!TryParse(value, minimum, maximum, culture, out decimal parsed))
        {
            return false;
        }

        amount = NormalizeParsed(parsed, minimum, maximum);
        selectedPreset = presets.FirstOrDefault(preset => parsed == preset) is var exactPreset &&
                         presets.Contains(exactPreset)
            ? exactPreset
            : null;
        return true;
    }

    private static bool TryParse(
        string? value,
        int minimum,
        int maximum,
        CultureInfo culture,
        out decimal parsed)
    {
        parsed = 0;
        return minimum <= maximum &&
               !string.IsNullOrWhiteSpace(value) &&
               decimal.TryParse(value, SupportedNumberStyles, culture, out parsed);
    }

    private static int NormalizeParsed(decimal parsed, int minimum, int maximum)
    {
        decimal rounded = decimal.Round(parsed, 0, MidpointRounding.AwayFromZero);
        if (rounded <= minimum)
        {
            return minimum;
        }

        if (rounded >= maximum)
        {
            return maximum;
        }

        return decimal.ToInt32(rounded);
    }
}
