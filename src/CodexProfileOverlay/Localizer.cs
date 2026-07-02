using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay;

internal sealed class Localizer
{
    public LanguagePreference Language { get; private set; }

    public event Action? LanguageChanged;

    public Localizer(LanguagePreference language)
    {
        Language = language;
    }

    public void SetLanguage(LanguagePreference language)
    {
        if (Language == language)
        {
            return;
        }

        Language = language;
        LanguageChanged?.Invoke();
    }

    public string this[string key] => LocalizationCatalog.Text(Language, key);

    public string Format(string key, params object[] args) => LocalizationCatalog.Text(Language, key, args);
}
