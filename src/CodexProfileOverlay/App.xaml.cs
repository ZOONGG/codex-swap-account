using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;
using Application = System.Windows.Application;

namespace CodexProfileOverlay;

public partial class App : Application
{
    private static readonly IReadOnlyDictionary<string, string> DarkTheme = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WindowBackgroundBrush"] = "#0B0B0D",
        ["Surface1Brush"] = "#141417",
        ["Surface2Brush"] = "#1B1B20",
        ["HoverSurfaceBrush"] = "#24242B",
        ["PressedSurfaceBrush"] = "#2B2B34",
        ["BorderBrush"] = "#303038",
        ["StrongTextBrush"] = "#F3F3F5",
        ["MutedTextBrush"] = "#A7A7B0",
        ["DisabledTextBrush"] = "#686872",
        ["AccentBrush"] = "#8B5CF6",
        ["AccentHoverBrush"] = "#9D72FF",
        ["SuccessBrush"] = "#2DD4A3",
        ["ErrorBrush"] = "#F05D6C",
        ["InputBackgroundBrush"] = "#101014",
        ["OverlayBackgroundBrush"] = "#141417",
        ["OverlayBorderBrush"] = "#303038",
        ["TabBackgroundBrush"] = "#1B1B20",
        ["TabHoverBrush"] = "#24242B",
        ["TabActiveBrush"] = "#2B2440",
    };

    private static readonly IReadOnlyDictionary<string, string> LightTheme = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WindowBackgroundBrush"] = "#F5F6FA",
        ["Surface1Brush"] = "#FFFFFF",
        ["Surface2Brush"] = "#F0F2F7",
        ["HoverSurfaceBrush"] = "#E7EAF2",
        ["PressedSurfaceBrush"] = "#DDE2EC",
        ["BorderBrush"] = "#CDD3DF",
        ["StrongTextBrush"] = "#161922",
        ["MutedTextBrush"] = "#5D6575",
        ["DisabledTextBrush"] = "#9AA1AE",
        ["AccentBrush"] = "#6D4AFF",
        ["AccentHoverBrush"] = "#7E61FF",
        ["SuccessBrush"] = "#0F9F75",
        ["ErrorBrush"] = "#D8435D",
        ["InputBackgroundBrush"] = "#FFFFFF",
        ["OverlayBackgroundBrush"] = "#FFFFFF",
        ["OverlayBorderBrush"] = "#C8CEDA",
        ["TabBackgroundBrush"] = "#EEF1F7",
        ["TabHoverBrush"] = "#E2E6EF",
        ["TabActiveBrush"] = "#E8E1FF",
    };

    private Mutex? mutex;
    private EventWaitHandle? activationEvent;
    private Thread? activationThread;
    private OverlayController? controller;
    private SafeLogger? logger;
    private volatile bool exiting;

    public static void ApplyTheme(AppTheme theme)
    {
        IReadOnlyDictionary<string, string> palette = theme == AppTheme.Light ? LightTheme : DarkTheme;
        foreach ((string key, string value) in palette)
        {
            if (ColorConverter.ConvertFromString(value) is Color color)
            {
                Current.Resources[key] = new SolidColorBrush(color);
            }
        }
    }

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
