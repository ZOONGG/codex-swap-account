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

    [Fact]
    public void StatusLegendAndUnavailableStateAreLocalized()
    {
        foreach (LanguagePreference language in new[] { LanguagePreference.English, LanguagePreference.Russian })
        {
            Assert.NotEqual("IndicatorLegend", LocalizationCatalog.Text(language, "IndicatorLegend"));
            Assert.NotEqual("RecommendedProfile", LocalizationCatalog.Text(language, "RecommendedProfile"));
            Assert.NotEqual("AutomaticLimitsUnavailable", LocalizationCatalog.Text(language, "AutomaticLimitsUnavailable"));
            Assert.NotEqual("InvalidProfileName", LocalizationCatalog.Text(language, "InvalidProfileName"));
            Assert.NotEqual("DuplicateProfileName", LocalizationCatalog.Text(language, "DuplicateProfileName"));
        }
    }

    [Fact]
    public void IndicatorLegendMatchesEnglishAndRussianProductCopy()
    {
        Assert.Equal("Recommended profile", LocalizationCatalog.Text(LanguagePreference.English, "RecommendedProfile"));
        Assert.Equal("It currently has the highest available capacity among checked profiles.", LocalizationCatalog.Text(LanguagePreference.English, "RecommendedProfileHelp"));
        Assert.Equal("High capacity", LocalizationCatalog.Text(LanguagePreference.English, "HighCapacity"));
        Assert.Equal("Medium capacity", LocalizationCatalog.Text(LanguagePreference.English, "MediumCapacity"));
        Assert.Equal("Low or exhausted", LocalizationCatalog.Text(LanguagePreference.English, "LowCapacity"));

        Assert.Equal("Рекомендуемый профиль", LocalizationCatalog.Text(LanguagePreference.Russian, "RecommendedProfile"));
        Assert.Equal("Сейчас у него больше всего доступного лимита среди проверенных аккаунтов.", LocalizationCatalog.Text(LanguagePreference.Russian, "RecommendedProfileHelp"));
        Assert.Equal("Лимитов много", LocalizationCatalog.Text(LanguagePreference.Russian, "HighCapacity"));
        Assert.Equal("Средний остаток", LocalizationCatalog.Text(LanguagePreference.Russian, "MediumCapacity"));
        Assert.Equal("Лимит почти закончился", LocalizationCatalog.Text(LanguagePreference.Russian, "LowCapacity"));
    }
}
