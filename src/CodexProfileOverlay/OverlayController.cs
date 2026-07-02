using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;

namespace CodexProfileOverlay;

internal sealed class OverlayController : IDisposable
{
    private readonly ProfileManagerService profileManager;
    private readonly ActiveProfileStore activeProfileStore;
    private readonly SettingsService settingsService;
    private readonly AuthSwitchService switchService;
    private readonly CodexProcessService processService;
    private readonly IStartupRegistrationService startupRegistrationService;
    private readonly SafeLogger logger;
    private readonly CodexWindowFinder windowFinder;
    private readonly DispatcherTimer timer;
    private readonly AppPaths paths;
    private readonly CancellationTokenSource disposalTokenSource = new();
    private OverlaySettings settings;
    private TrayIconService? trayIcon;
    private OverlayWindow? overlayWindow;
    private SettingsWindow? settingsWindow;
    private ProfileManagerWindow? profileManagerWindow;
    private HotkeyManager? hotkeyManager;
    private IReadOnlyList<ProfileInfo> profiles = [];
    private CodexWindowInfo? attachedWindow;
    private bool switching;
    private bool manualHidden;

    public OverlayController(
        AppPaths paths,
        ProfileManagerService profileManager,
        ActiveProfileStore activeProfileStore,
        SettingsService settingsService,
        AuthSwitchService switchService,
        CodexProcessService processService,
        IStartupRegistrationService startupRegistrationService,
        SafeLogger logger)
    {
        this.paths = paths;
        this.profileManager = profileManager;
        this.activeProfileStore = activeProfileStore;
        this.settingsService = settingsService;
        this.switchService = switchService;
        this.processService = processService;
        this.startupRegistrationService = startupRegistrationService;
        this.logger = logger;
        settings = settingsService.Load();
        windowFinder = new CodexWindowFinder(logger);
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        EnsureOverlay();
        EnsureTray();
        RefreshProfiles();
        ApplySettings();
        timer.Start();
        if (settings.LaunchCodexWhenOverlayStarts)
        {
            processService.LaunchCodex();
        }

        Tick();
    }

    public void Dispose()
    {
        disposalTokenSource.Cancel();
        timer.Stop();
        hotkeyManager?.Dispose();
        trayIcon?.Dispose();
        settingsWindow?.Close();
        profileManagerWindow?.Close();
        overlayWindow?.Close();
        disposalTokenSource.Dispose();
    }

    public void RevealFromSecondInstance()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            manualHidden = false;
            overlayWindow?.Show();
            ShowSettingsWindow();
            Tick();
        });
    }

    private void EnsureOverlay()
    {
        if (overlayWindow is not null)
        {
            return;
        }

        overlayWindow = new OverlayWindow(settings, logger)
        {
            OnSwitchProfile = profile => _ = SwitchProfileAsync(profile),
            OnRefreshProfiles = RefreshProfiles,
            OnOpenProfilesFolder = () => OpenFolder(paths.ProfilesDirectory),
            OnOpenApplicationDataFolder = () => OpenFolder(paths.ApplicationDataDirectory),
            OnOpenSettings = ShowSettingsWindow,
            OnManageProfiles = ShowProfileManager,
            OnAddProfile = () => _ = AddProfileAsync(),
            OnHideOverlay = HideOverlay,
            OnExit = () => Application.Current.Shutdown(),
            OnSettingsChanged = SaveSettings,
        };

        hotkeyManager = new HotkeyManager(overlayWindow.Handle);
        hotkeyManager.ToggleOverlayRequested += ToggleOverlay;
        hotkeyManager.ProfileHotkeyRequested += index =>
        {
            if (index >= 0 && index < profiles.Count)
            {
                _ = SwitchProfileAsync(profiles[index].Name);
            }
        };
    }

    private void EnsureTray()
    {
        if (trayIcon is not null)
        {
            return;
        }

        trayIcon = new TrayIconService();
        trayIcon.ToggleOverlayRequested += ToggleOverlay;
        trayIcon.OpenCodexRequested += processService.LaunchCodex;
        trayIcon.SettingsRequested += ShowSettingsWindow;
        trayIcon.ProfileSelected += profile => _ = SwitchProfileAsync(profile);
        trayIcon.StartWithWindowsChanged += enabled =>
        {
            settings.StartWithWindows = enabled;
            SaveSettings(settings);
            ApplyStartupSetting();
        };
        trayIcon.ExitRequested += () => Application.Current.Shutdown();
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
            trayIcon?.UpdateOverlayState(false);
            attachedWindow = null;
            return;
        }

        if (attachedWindow?.Hwnd != found.Hwnd)
        {
            attachedWindow = found;
            overlayWindow!.AttachTo(found.Hwnd);
            manualHidden = !settings.ShowAutomaticallyWhenCodexOpens;
            logger.Info($"Attached overlay to Codex process {found.ProcessId}.");
        }

        overlayWindow!.AllowAutoShow = !manualHidden;
        overlayWindow.UpdatePlacement(found.Hwnd);
        if (manualHidden)
        {
            overlayWindow.Hide();
        }

        trayIcon?.UpdateOverlayState(overlayWindow.IsVisible);
    }

    private void ToggleOverlay()
    {
        EnsureOverlay();
        if (overlayWindow!.IsVisible)
        {
            HideOverlay();
        }
        else
        {
            manualHidden = false;
            overlayWindow.AllowAutoShow = true;
            overlayWindow.Show();
            Tick();
        }
    }

    private void HideOverlay()
    {
        manualHidden = true;
        overlayWindow?.Hide();
        trayIcon?.UpdateOverlayState(false);
    }

    private void RefreshProfiles()
    {
        try
        {
            profileManager.EnsureMetadata();
            profiles = profileManager.ListProfiles();
            string? activeProfile = activeProfileStore.Read();
            overlayWindow?.SetProfiles(profiles, activeProfile);
            trayIcon?.UpdateProfiles(profiles, activeProfile);
            settingsWindow?.UpdateProfiles(profiles);
            settingsWindow?.SetConflicts(RegisterHotkeys());
            profileManagerWindow?.UpdateProfiles(profiles, activeProfile);
        }
        catch (Exception exception)
        {
            logger.Error("Profile refresh failed.", exception);
            overlayWindow?.ShowError("Could not refresh profiles. Check the profiles folder.");
        }
    }

    private IReadOnlyList<string> RegisterHotkeys()
    {
        if (hotkeyManager is null)
        {
            return [];
        }

        IReadOnlyList<string> conflicts = hotkeyManager.Register(settings.Hotkeys, profiles.Count);
        foreach (string conflict in conflicts)
        {
            logger.Info(conflict);
        }

        if (conflicts.Count > 0)
        {
            overlayWindow?.ShowError("One or more hotkeys could not be registered.");
        }

        return conflicts;
    }

    private async Task SwitchProfileAsync(string profileName)
    {
        if (switching || string.Equals(profileName, activeProfileStore.Read(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        switching = true;
        overlayWindow?.SetSwitching(true);
        hotkeyManager?.Clear();
        overlayWindow?.ShowNotification($"Switching to {profileName}...");
        try
        {
            bool allowForceClose = settings.ForceCloseFallback;
            if (allowForceClose && settings.ConfirmBeforeForceClose)
            {
                allowForceClose = MessageBox.Show(
                    overlayWindow,
                    "Codex will be asked to close gracefully. If it is still running after the timeout, allow force closing Codex to complete the switch?",
                    "Codex Profile Overlay",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes;
            }

            await processService.CloseCodexAsync(settings.GracefulCloseTimeoutSeconds, allowForceClose, disposalTokenSource.Token).ConfigureAwait(true);
            await switchService.SwitchAsync(profileName, disposalTokenSource.Token).ConfigureAwait(true);
            RefreshProfiles();
            if (settings.LaunchCodexAfterSwitching)
            {
                processService.LaunchCodex();
                await WaitForCodexWindowAsync(disposalTokenSource.Token).ConfigureAwait(true);
            }

            overlayWindow?.ShowNotification($"Switched to account: {profileName}");
            logger.Info($"Switched to profile '{profileName}'.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Error($"Switch to profile '{profileName}' failed.", exception);
            overlayWindow?.ShowError("Could not switch account. Previous authorization was restored.");
            trayIcon?.ShowBalloon("Codex Profile Overlay", "Could not switch account. Previous authorization was restored.");
        }
        finally
        {
            switching = false;
            overlayWindow?.SetSwitching(false);
            _ = RegisterHotkeys();
            Tick();
        }
    }

    private async Task AddProfileAsync()
    {
        EnsureOverlay();
        string? profileName = PromptDialog.Show(overlayWindow!, "Add profile", "Profile directory name");
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return;
        }

        try
        {
            string directory = profileManager.CreateProfileDirectory(profileName);
            overlayWindow?.ShowNotification($"Starting Codex login for {profileName}...");
            bool loginCreatedAuth = await processService.LoginProfileAsync(directory, disposalTokenSource.Token).ConfigureAwait(true);
            if (!loginCreatedAuth)
            {
                overlayWindow?.ShowError("Login finished, but auth.json was not created.");
                return;
            }

            RefreshProfiles();
            overlayWindow?.ShowNotification("Profile added successfully");
            if (MessageBox.Show(overlayWindow, "Switch to the newly added profile now?", "Codex Profile Overlay", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await SwitchProfileAsync(profileName).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Error("Add profile failed.", exception);
            overlayWindow?.ShowError("Could not add profile.");
        }
    }

    private void ShowSettingsWindow()
    {
        if (settingsWindow is { IsVisible: true })
        {
            settingsWindow.Activate();
            return;
        }

        settingsWindow = new SettingsWindow(
            settings,
            profiles,
            SaveSettings,
            () => _ = AddProfileAsync(),
            ShowProfileManager,
            () => OpenFolder(paths.ProfilesDirectory),
            () => OpenFolder(paths.RemovedProfilesDirectory),
            () => OpenFolder(paths.ApplicationDataDirectory),
            () => OpenFolder(paths.BackupDirectory),
            () => OpenFolder(paths.LogDirectory),
            ResetPosition,
            ResetSettings,
            () => Application.Current.Shutdown());
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
        settingsWindow.SetConflicts(RegisterHotkeys());
    }

    private void ShowProfileManager()
    {
        if (profileManagerWindow is { IsVisible: true })
        {
            profileManagerWindow.Activate();
            return;
        }

        profileManagerWindow = new ProfileManagerWindow(
            profiles,
            activeProfileStore.Read(),
            () => _ = AddProfileAsync(),
            RenameDisplayName,
            RenameDirectory,
            RemoveProfile,
            ReorderProfiles,
            profile => OpenFolder(profile.DirectoryPath),
            RefreshProfiles);
        profileManagerWindow.Closed += (_, _) => profileManagerWindow = null;
        profileManagerWindow.Show();
    }

    private void RenameDisplayName(ProfileInfo profile)
    {
        string? name = PromptDialog.Show(profileManagerWindow!, "Rename display name", "Display name", profile.DisplayName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        profileManager.RenameDisplayName(profile.Name, name);
        RefreshProfiles();
        overlayWindow?.ShowNotification("Profile renamed");
    }

    private void RenameDirectory(ProfileInfo profile)
    {
        if (string.Equals(profile.Name, activeProfileStore.Read(), StringComparison.OrdinalIgnoreCase))
        {
            overlayWindow?.ShowError("The active profile folder cannot be renamed.");
            return;
        }

        string? name = PromptDialog.Show(profileManagerWindow!, "Rename profile folder", "New folder name", profile.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        profileManager.RenameDirectory(profile.Name, name);
        RefreshProfiles();
        overlayWindow?.ShowNotification("Profile renamed");
    }

    private void RemoveProfile(ProfileInfo profile)
    {
        if (MessageBox.Show(profileManagerWindow, "This moves the local saved login to the removed-profiles folder. It does not delete your ChatGPT account.", "Remove profile", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        profileManager.RemoveProfile(profile.Name, activeProfileStore.Read());
        RefreshProfiles();
        overlayWindow?.ShowNotification("Profile removed from switcher");
    }

    private void ReorderProfiles(IReadOnlyList<string> orderedNames)
    {
        profileManager.Reorder(orderedNames);
        RefreshProfiles();
    }

    private void ApplySettings()
    {
        ApplyStartupSetting();
        overlayWindow?.ApplySettings();
        trayIcon?.UpdateStartWithWindows(settings.StartWithWindows);
        _ = RegisterHotkeys();
    }

    private void SaveSettings(OverlaySettings updatedSettings)
    {
        settings = updatedSettings;
        settingsService.Save(settings);
        ApplySettings();
    }

    private void ApplyStartupSetting()
    {
        try
        {
            startupRegistrationService.SetEnabled(settings.StartWithWindows, Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? AppContext.BaseDirectory);
            trayIcon?.UpdateStartWithWindows(startupRegistrationService.IsEnabled());
        }
        catch (Exception exception)
        {
            logger.Error("Could not update startup registration.", exception);
            overlayWindow?.ShowError("Start with Windows could not be updated.");
        }
    }

    private void ResetPosition()
    {
        settings.PositionPreset = PositionPreset.TopRight;
        settings.OffsetX = 14;
        settings.OffsetY = 34;
        SaveSettings(settings);
    }

    private void ResetSettings()
    {
        settings = new OverlaySettings();
        settingsService.Save(settings);
        overlayWindow?.ApplySettings();
        RefreshProfiles();
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
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }
}
