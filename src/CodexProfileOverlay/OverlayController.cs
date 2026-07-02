using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
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
    private readonly OverlayVisibilityState visibilityState = new();
    private readonly CancellationTokenSource disposalTokenSource = new();
    private OverlaySettings settings;
    private Localizer localizer;
    private TrayIconService? trayIcon;
    private OverlayWindow? overlayWindow;
    private SettingsWindow? settingsWindow;
    private ProfileManagerWindow? profileManagerWindow;
    private HotkeyManager? hotkeyManager;
    private IReadOnlyList<ProfileInfo> profiles = [];
    private CodexWindowInfo? attachedWindow;
    private bool switching;

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
        App.ApplyTheme(settings.Theme);
        localizer = new Localizer(settings.Language);
        visibilityState.AutomaticDisplayEnabled = settings.ShowAutomaticallyWhenCodexOpens;
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
            visibilityState.RevealManually();
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
            Localizer = localizer,
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

        trayIcon = new TrayIconService(localizer);
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
            visibilityState.MarkCodexUnavailable();
            overlayWindow?.Hide();
            trayIcon?.UpdateOverlayState(false);
            attachedWindow = null;
            return;
        }

        if (attachedWindow?.Hwnd != found.Hwnd)
        {
            attachedWindow = found;
            overlayWindow!.AttachTo(found.Hwnd);
            if (!settings.ShowAutomaticallyWhenCodexOpens)
            {
                visibilityState.MarkManualHide();
            }

            logger.Info($"Attached overlay to Codex process {found.ProcessId}.");
        }

        visibilityState.AutomaticDisplayEnabled = settings.ShowAutomaticallyWhenCodexOpens;
        visibilityState.MarkCodexAvailable(found.IsMinimized);
        overlayWindow!.AllowAutoShow = visibilityState.ShouldShowOverlay;
        overlayWindow.UpdatePlacement(found.Hwnd);
        if (!visibilityState.ShouldShowOverlay)
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
            visibilityState.RevealManually();
            overlayWindow.AllowAutoShow = true;
            overlayWindow.Show();
            Tick();
        }
    }

    private void HideOverlay()
    {
        visibilityState.MarkManualHide();
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
            overlayWindow?.ShowError(localizer["CouldNotRefreshProfiles"]);
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
            overlayWindow?.ShowError(localizer["HotkeysCouldNotRegister"]);
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
        overlayWindow?.ShowNotification(localizer.Format("SwitchingToProfile", profileName));
        try
        {
            bool allowForceClose = settings.ForceCloseFallback;
            if (allowForceClose && settings.ConfirmBeforeForceClose)
            {
                allowForceClose = MessageBox.Show(
                    overlayWindow,
                    localizer["ForceClosePrompt"],
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

            overlayWindow?.ShowNotification(localizer.Format("SwitchedToAccount", profileName));
            logger.Info($"Switched to profile '{profileName}'.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Error($"Switch to profile '{profileName}' failed.", exception);
            overlayWindow?.ShowError(localizer["CouldNotSwitch"] + " " + localizer["PreviousAuthorizationRestored"] + ".");
            trayIcon?.ShowBalloon("Codex Profile Overlay", localizer["CouldNotSwitch"] + " " + localizer["PreviousAuthorizationRestored"] + ".");
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
        Window? dialogOwner = PromptOwner();
        string? profileName = ShowPrompt(dialogOwner, localizer["AddProfileTitle"], localizer["ProfileDirectoryName"], primaryText: localizer["Add"]);
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return;
        }

        try
        {
            string directory = profileManager.CreateProfileDirectory(profileName);
            overlayWindow?.ShowNotification(localizer.Format("StartingLogin", profileName));
            bool loginCreatedAuth = await processService.LoginProfileAsync(directory, disposalTokenSource.Token).ConfigureAwait(true);
            if (!loginCreatedAuth)
            {
                overlayWindow?.ShowError(localizer["AuthNotCreated"]);
                return;
            }

            RefreshProfiles();
            overlayWindow?.ShowNotification(localizer["ProfileAdded"]);
            MessageBoxResult switchNewProfile = dialogOwner is null
                ? MessageBox.Show(localizer["SwitchNewProfile"], "Codex Profile Overlay", MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(dialogOwner, localizer["SwitchNewProfile"], "Codex Profile Overlay", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (switchNewProfile == MessageBoxResult.Yes)
            {
                await SwitchProfileAsync(profileName).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Error("Add profile failed.", exception);
            overlayWindow?.ShowError(localizer["CouldNotAddProfile"]);
        }
    }

    private void ShowSettingsWindow()
    {
        if (settingsWindow is { IsVisible: true })
        {
            BringToFront(settingsWindow);
            return;
        }

        settingsWindow = new SettingsWindow(
            settings,
            profiles,
            localizer,
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
        ShowCodexOwnedWindow(settingsWindow);
        settingsWindow.SetConflicts(RegisterHotkeys());
    }

    private void ShowProfileManager()
    {
        if (profileManagerWindow is { IsVisible: true })
        {
            BringToFront(profileManagerWindow);
            return;
        }

        profileManagerWindow = new ProfileManagerWindow(
            profiles,
            activeProfileStore.Read(),
            localizer,
            () => _ = AddProfileAsync(),
            RenameDisplayName,
            RemoveProfile,
            ReorderProfiles,
            profile => OpenFolder(profile.DirectoryPath),
            RefreshProfiles);
        profileManagerWindow.Closed += (_, _) => profileManagerWindow = null;
        ShowCodexOwnedWindow(profileManagerWindow);
    }

    private void RenameDisplayName(ProfileInfo profile)
    {
        string? name = ShowPrompt(PromptOwner(), localizer["RenameDisplayName"], localizer["DisplayName"], profile.DisplayName, localizer["Save"]);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        profileManager.RenameDisplayName(profile.Name, name);
        RefreshProfiles();
        overlayWindow?.ShowNotification(localizer["ProfileRenamed"]);
    }

    private void RenameDirectory(ProfileInfo profile)
    {
        if (string.Equals(profile.Name, activeProfileStore.Read(), StringComparison.OrdinalIgnoreCase))
        {
            overlayWindow?.ShowError(localizer["ActiveProfileCannotRemove"]);
            return;
        }

        string? name = ShowPrompt(PromptOwner(), "Rename profile folder", "New folder name", profile.Name, localizer["Save"]);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        profileManager.RenameDirectory(profile.Name, name);
        RefreshProfiles();
        overlayWindow?.ShowNotification(localizer["ProfileRenamed"]);
    }

    private void RemoveProfile(ProfileInfo profile)
    {
        if (string.Equals(profile.Name, activeProfileStore.Read(), StringComparison.OrdinalIgnoreCase))
        {
            overlayWindow?.ShowError(localizer["ActiveProfileCannotRename"]);
            return;
        }

        if (MessageBox.Show(profileManagerWindow, localizer["RemoveProfilePrompt"], localizer["Remove"], MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        profileManager.RemoveProfile(profile.Name, activeProfileStore.Read());
        RefreshProfiles();
        overlayWindow?.ShowNotification(localizer["ProfileRemoved"]);
    }

    private void ReorderProfiles(IReadOnlyList<string> orderedNames)
    {
        profileManager.Reorder(orderedNames);
        RefreshProfiles();
    }

    private Window? PromptOwner()
    {
        if (settingsWindow is { IsVisible: true })
        {
            return settingsWindow;
        }

        if (profileManagerWindow is { IsVisible: true })
        {
            return profileManagerWindow;
        }

        return null;
    }

    private string? ShowPrompt(Window? owner, string title, string label, string initialValue = "", string? primaryText = null)
    {
        return PromptDialog.Show(owner, owner is null ? CurrentCodexWindowHandle() : IntPtr.Zero, title, label, initialValue, primaryText ?? localizer["Save"], localizer["Cancel"]);
    }

    private void ShowCodexOwnedWindow(Window window)
    {
        AttachToCodexOwner(window);
        window.ShowInTaskbar = false;
        window.Show();
        BringToFront(window);
    }

    private void AttachToCodexOwner(Window window)
    {
        IntPtr owner = CurrentCodexWindowHandle();
        if (owner == IntPtr.Zero)
        {
            return;
        }

        if (NativeMethods.IsIconic(owner))
        {
            _ = NativeMethods.ShowWindow(owner, NativeMethods.SwRestore);
        }

        var helper = new WindowInteropHelper(window);
        helper.Owner = owner;
    }

    private IntPtr CurrentCodexWindowHandle()
    {
        if (attachedWindow is not null && NativeMethods.IsWindowVisible(attachedWindow.Hwnd))
        {
            return attachedWindow.Hwnd;
        }

        var found = windowFinder.FindMainWindow();
        if (found is null)
        {
            return IntPtr.Zero;
        }

        attachedWindow = found;
        return found.Hwnd;
    }

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
        window.Focus();
        _ = NativeMethods.SetForegroundWindow(new WindowInteropHelper(window).Handle);
    }

    private void ApplySettings()
    {
        ApplyStartupSetting();
        App.ApplyTheme(settings.Theme);
        overlayWindow?.ApplySettings();
        settingsWindow?.RefreshTheme();
        trayIcon?.UpdateStartWithWindows(settings.StartWithWindows);
        _ = RegisterHotkeys();
    }

    private void SaveSettings(OverlaySettings updatedSettings)
    {
        try
        {
            settings = updatedSettings;
            localizer.SetLanguage(settings.Language);
            visibilityState.AutomaticDisplayEnabled = settings.ShowAutomaticallyWhenCodexOpens;
            settingsService.Save(settings);
            ApplySettings();
        }
        catch (Exception exception)
        {
            logger.Error("Could not save settings.", exception);
            overlayWindow?.ShowError(localizer["CouldNotSaveSettings"]);
        }
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
            overlayWindow?.ShowError(localizer["StartupCouldNotUpdate"]);
        }
    }

    private void ResetPosition()
    {
        settings.PositionPreset = PositionPreset.AfterMenu;
        settings.OffsetX = 396;
        settings.OffsetY = 2;
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
