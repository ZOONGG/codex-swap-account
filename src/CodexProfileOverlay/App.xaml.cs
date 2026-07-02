using System.IO;
using System.Threading;
using System.Windows;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay;

public partial class App : Application
{
    private Mutex? mutex;
    private OverlayController? controller;
    private SafeLogger? logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        mutex = new Mutex(initiallyOwned: true, "Local\\CodexProfileOverlay", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        var paths = AppPaths.FromEnvironment();
        Directory.CreateDirectory(paths.ApplicationDataDirectory);
        logger = new SafeLogger(paths.LogDirectory);

        try
        {
            var profileDiscovery = new ProfileDiscoveryService(paths.ProfilesDirectory);
            var activeProfileStore = new ActiveProfileStore(paths.ActiveProfileFile);
            var settingsService = new SettingsService(paths.SettingsFile);
            var switchService = new AuthSwitchService(paths, profileDiscovery, activeProfileStore);
            var processService = new CodexProcessService(logger);
            controller = new OverlayController(
                paths,
                profileDiscovery,
                activeProfileStore,
                settingsService,
                switchService,
                processService,
                logger);
            controller.Start();
            logger.Info("Overlay started.");
        }
        catch (Exception exception)
        {
            logger?.Error("Startup failed.", exception);
            MessageBox.Show(exception.Message, "Codex Profile Overlay", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        controller?.Dispose();
        mutex?.Dispose();
        base.OnExit(e);
    }
}
