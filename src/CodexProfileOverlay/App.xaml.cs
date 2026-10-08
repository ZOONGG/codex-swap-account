using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        ["OverlayBackgroundBrush"] = "#000000",
        ["OverlayBorderBrush"] = "#17171B",
        ["TabBackgroundBrush"] = "#111114",
        ["TabHoverBrush"] = "#1A1A1F",
        ["TabActiveBrush"] = "#241B38",
    };

    private static readonly IReadOnlyDictionary<string, string> LightTheme = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WindowBackgroundBrush"] = "#F5F6FA",
        ["Surface1Brush"] = "#FFFFFF",
        ["Surface2Brush"] = "#F0F2F7",
        ["HoverSurfaceBrush"] = "#E7EAF2",
        ["PressedSurfaceBrush"] = "#DDE2EC",
        ["BorderBrush"] = "#C5CCD8",
        ["StrongTextBrush"] = "#161922",
        ["MutedTextBrush"] = "#5D6575",
        ["DisabledTextBrush"] = "#9AA1AE",
        ["AccentBrush"] = "#6D4AFF",
        ["AccentHoverBrush"] = "#7E61FF",
        ["SuccessBrush"] = "#0F9F75",
        ["ErrorBrush"] = "#D8435D",
        ["InputBackgroundBrush"] = "#FFFFFF",
        ["OverlayBackgroundBrush"] = "#FFFFFF",
        ["OverlayBorderBrush"] = "#AEB7C6",
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

        WindowCaptionThemeService.ApplyToOpenWindows(theme);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (e.Args.Contains("--support-visual-qa", StringComparer.OrdinalIgnoreCase))
        {
            AppTheme theme = e.Args.Contains("--light", StringComparer.OrdinalIgnoreCase)
                ? AppTheme.Light
                : AppTheme.Dark;
            LanguagePreference language = e.Args.Contains("--ru", StringComparer.OrdinalIgnoreCase)
                ? LanguagePreference.Russian
                : LanguagePreference.English;
            ApplyTheme(theme);
            var supportWindow = new SupportWindow(theme, language) { ShowInTaskbar = true };
            string? outputPath = ArgumentValue(e.Args, "--output=");
            double dpi = double.TryParse(
                ArgumentValue(e.Args, "--dpi="),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double requestedDpi)
                ? Math.Clamp(requestedDpi, 96, 192)
                : 96;
            string visualState = ArgumentValue(e.Args, "--state=") ?? "methods";
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                supportWindow.ShowInTaskbar = false;
                supportWindow.ContentRendered += async (_, _) =>
                {
                    await Task.Delay(2000);
                    await supportWindow.ApplyVisualQaStateAsync(visualState);
                    await supportWindow.Dispatcher.InvokeAsync(
                        static () => { },
                        System.Windows.Threading.DispatcherPriority.Render);
                    RenderSupportPreview(supportWindow, outputPath, dpi);
                    supportWindow.Close();
                };
            }

            MainWindow = supportWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            supportWindow.Show();
            return;
        }

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
            var backupMaintenance = new BackupMaintenanceService(paths, logger);
            backupMaintenance.CleanupRetention();
            var switchService = new AuthSwitchService(paths, profileDiscovery, activeProfileStore, backups: backupMaintenance);
            var processService = new CodexProcessService(logger);
            controller = new OverlayController(
                paths,
                profileManager,
                activeProfileStore,
                settingsService,
                switchService,
                processService,
                new StartupRegistrationService(),
                logger,
                backupMaintenance);
            controller.Start(e.Args.Contains("--updated", StringComparer.Ordinal) || e.Args.Contains("--installed", StringComparer.Ordinal));
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

    private static string? ArgumentValue(IEnumerable<string> arguments, string prefix) =>
        arguments.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];

    private static void RenderSupportPreview(Window window, string outputPath, double dpi)
    {
        if (window.Content is not FrameworkElement content || content.ActualWidth <= 0 || content.ActualHeight <= 0)
        {
            return;
        }

        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        double scale = dpi / 96;
        double renderWidth = content.ActualWidth + content.Margin.Left + content.Margin.Right;
        double renderHeight = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(renderWidth * scale),
            (int)Math.Ceiling(renderHeight * scale),
            dpi,
            dpi,
            PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(fullPath);
        encoder.Save(stream);
    }
}
