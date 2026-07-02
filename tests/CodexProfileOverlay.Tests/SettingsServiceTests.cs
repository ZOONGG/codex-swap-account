using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsSettings()
    {
        using var temp = new TempDirectory();
        string settingsFile = Path.Combine(temp.Path, "settings.json");
        var service = new SettingsService(settingsFile);

        service.Save(new OverlaySettings { OffsetX = 123.5, OffsetY = 42.25 });

        var loaded = service.Load();
        Assert.Equal(123.5, loaded.OffsetX);
        Assert.Equal(42.25, loaded.OffsetY);
    }
}
