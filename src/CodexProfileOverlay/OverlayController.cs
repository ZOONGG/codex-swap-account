using System.IO;
using System.Windows;
using System.Windows.Threading;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay;

internal sealed class OverlayController : IDisposable
{
    private readonly ProfileDiscoveryService profileDiscovery;
    private readonly ActiveProfileStore activeProfileStore;
    private readonly SettingsService settingsService;
    private readonly AuthSwitchService switchService;
    private readonly CodexProcessService processService;
    private readonly SafeLogger logger;
    private readonly CodexWindowFinder windowFinder;
    private readonly DispatcherTimer timer;
    private readonly AppPaths paths;
    private readonly CancellationTokenSource disposalTokenSource = new();
    private OverlayWindow? overlayWindow;
    private CodexWindowInfo? attachedWindow;
    private bool switching;

    public OverlayController(
        AppPaths paths,
        ProfileDiscoveryService profileDiscovery,
        ActiveProfileStore activeProfileStore,
        SettingsService settingsService,
        AuthSwitchService switchService,
        CodexProcessService processService,
        SafeLogger logger)
    {
        this.paths = paths;
        this.profileDiscovery = profileDiscovery;
        this.activeProfileStore = activeProfileStore;
        this.settingsService = settingsService;
        this.switchService = switchService;
        this.processService = processService;
        this.logger = logger;
        windowFinder = new CodexWindowFinder(logger);
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        EnsureOverlay();
        RefreshProfiles();
        timer.Start();
        Tick();
    }

    public void Dispose()
    {
        disposalTokenSource.Cancel();
        timer.Stop();
        overlayWindow?.Close();
        disposalTokenSource.Dispose();
    }

    private void EnsureOverlay()
    {
        if (overlayWindow is not null)
        {
            return;
        }

        overlayWindow = new OverlayWindow(settingsService.Load(), logger)
        {
            OnSwitchProfile = profile => _ = SwitchProfileAsync(profile),
            OnRefreshProfiles = RefreshProfiles,
            OnOpenProfilesFolder = () => OpenFolder(paths.ProfilesDirectory),
            OnOpenApplicationDataFolder = () => OpenFolder(paths.ApplicationDataDirectory),
            OnExit = () => Application.Current.Shutdown(),
            OnSettingsChanged = settingsService.Save,
        };
    }

    private void Tick()
    {
        if (switching)
        {
            return;
        }

        EnsureOverlay();
        var found = windowFinder.FindMainWindow();
        if (found is null)
        {
            overlayWindow?.Hide();
            attachedWindow = null;
            return;
        }

        if (attachedWindow?.Hwnd != found.Hwnd)
        {
            attachedWindow = found;
            overlayWindow!.AttachTo(found.Hwnd);
            logger.Info($"Attached overlay to Codex process {found.ProcessId}.");
        }

        overlayWindow!.UpdatePlacement(found.Hwnd);
    }

    private void RefreshProfiles()
    {
        try
        {
            var profiles = profileDiscovery.DiscoverProfiles();
            string? activeProfile = activeProfileStore.Read();
            overlayWindow?.SetProfiles(profiles, activeProfile);
        }
        catch (Exception exception)
        {
            logger.Error("Profile refresh failed.", exception);
            overlayWindow?.ShowError("Could not refresh profiles. Check the profiles folder.");
        }
    }

    private async Task SwitchProfileAsync(string profileName)
    {
        if (switching)
        {
            return;
        }

        switching = true;
        overlayWindow?.SetSwitching(true);
        try
        {
            await processService.CloseCodexAsync(disposalTokenSource.Token).ConfigureAwait(true);
            await switchService.SwitchAsync(profileName, disposalTokenSource.Token).ConfigureAwait(true);
            RefreshProfiles();
            processService.LaunchCodex();
            await WaitForCodexWindowAsync(disposalTokenSource.Token).ConfigureAwait(true);
            overlayWindow?.ShowNotification($"Switched to account: {profileName}");
            logger.Info($"Switched to profile '{profileName}'.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Error($"Switch to profile '{profileName}' failed.", exception);
            overlayWindow?.ShowError($"Could not switch to '{profileName}'. {exception.Message}");
        }
        finally
        {
            switching = false;
            overlayWindow?.SetSwitching(false);
            Tick();
        }
    }

    private async Task WaitForCodexWindowAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(300, cancellationToken).ConfigureAwait(true);
            var found = windowFinder.FindMainWindow();
            if (found is not null)
            {
                attachedWindow = found;
                overlayWindow?.AttachTo(found.Hwnd);
                overlayWindow?.UpdatePlacement(found.Hwnd);
                return;
            }
        }

        logger.Info("Timed out waiting for Codex window after launch.");
    }

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }
}
