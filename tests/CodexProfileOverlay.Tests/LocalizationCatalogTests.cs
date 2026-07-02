using System.Globalization;
using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class LocalizationCatalogTests
{
    [Fact]
    public void RussianCatalogCoversEnglishKeys()
    {
        Assert.Empty(LocalizationCatalog.MissingRussianKeys());
    }

    [Fact]
    public void SystemDefaultUsesRussianForRussianWindowsCulture()
    {
        LanguagePreference resolved = LocalizationCatalog.Resolve(LanguagePreference.SystemDefault, new CultureInfo("ru-RU"));

        Assert.Equal(LanguagePreference.Russian, resolved);
    }

    [Fact]
    public void FormatsSwitchProgressWithoutTranslatingProfileName()
    {
        string text = LocalizationCatalog.Text(LanguagePreference.Russian, "SwitchingToProfile", "work-account");

        Assert.Contains("work-account", text);
        Assert.StartsWith("Переключение", text, StringComparison.Ordinal);
    }
}
