using System.IO;
using System.Threading;
using System.Windows;
using CodexProfileOverlay.Core.Services;
using Application = System.Windows.Application;

namespace CodexProfileOverlay;

public partial class App : Application
{
    private Mutex? mutex;
    private EventWaitHandle? activationEvent;
    private Thread? activationThread;
    private OverlayController? controller;
    private SafeLogger? logger;
    private volatile bool exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        const string mutexName = "Local\\CodexProfileOverlay";
        const string activationEventName = "Local\\CodexProfileOverlay.Activate";

        mutex = new Mutex(initiallyOwned: true, mutexName, out bool created);
        if (!created)
        {
            try
            {
                using EventWaitHandle existingEvent = EventWaitHandle.OpenExisting(activationEventName);
                _ = existingEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            Shutdown();
            return;
        }

        activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activationEventName);

        var paths = AppPaths.FromEnvironment();
        Directory.CreateDirectory(paths.ApplicationDataDirectory);
        logger = new SafeLogger(paths.LogDirectory);

        try
        {
            var profileDiscovery = new ProfileDiscoveryService(paths.ProfilesDirectory);
            var profileMetadataStore = new ProfileMetadataStore(paths.ProfilesMetadataFile);
            var profileManager = new ProfileManagerService(paths, profileDiscovery, profileMetadataStore);
            var activeProfileStore = new ActiveProfileStore(paths.ActiveProfileFile);
            var settingsService = new SettingsService(paths.SettingsFile);
            var switchService = new AuthSwitchService(paths, profileDiscovery, activeProfileStore);
            var processService = new CodexProcessService(logger);
            controller = new OverlayController(
                paths,
                profileManager,
                activeProfileStore,
                settingsService,
                switchService,
                processService,
                new StartupRegistrationService(),
                logger);
            controller.Start();
            StartActivationListener();
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
        exiting = true;
        activationEvent?.Set();
        controller?.Dispose();
        activationEvent?.Dispose();
        mutex?.Dispose();
        base.OnExit(e);
    }

    private void StartActivationListener()
    {
        if (activationEvent is null)
        {
            return;
        }

        activationThread = new Thread(() =>
        {
            while (!exiting)
            {
                activationEvent.WaitOne();
                if (!exiting)
                {
                    controller?.RevealFromSecondInstance();
                }
            }
        })
        {
            IsBackground = true,
            Name = "Single instance activation listener",
        };
        activationThread.Start();
    }
}
