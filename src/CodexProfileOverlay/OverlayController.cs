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
    private readonly BackupMaintenanceService backupMaintenance;
    private readonly CodexWindowFinder windowFinder;
    private readonly DispatcherTimer timer;
    private readonly AppPaths paths;
    private readonly OverlayVisibilityState visibilityState = new();
    private readonly CancellationTokenSource disposalTokenSource = new();
    private readonly Dictionary<string, ProfileLoginAttempt> activeProfileLogins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> lastUsageRefreshAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProfileStatusStore statusStore;
    private readonly ProfileStatusService statusService;
    private readonly SharedCodexStateMigrationService stateMigrationService;
    private OverlaySettings settings;
    private Localizer localizer;
    private TrayIconService? trayIcon;
    private OverlayWindow? overlayWindow;
    private SettingsWindow? settingsWindow;
    private readonly UpdateService updates = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexProfileOverlay", "updates.json"));
    private ProfileManagerWindow? profileManagerWindow;
    private HotkeyManager? hotkeyManager;
    private IReadOnlyList<ProfileInfo> profiles = [];
    private CodexWindowInfo? attachedWindow;
    private bool switching;
    private bool automaticUsageRefreshRunning;

    public OverlayController(
        AppPaths paths,
        ProfileManagerService profileManager,
        ActiveProfileStore activeProfileStore,
        SettingsService settingsService,
        AuthSwitchService switchService,
        CodexProcessService processService,
        IStartupRegistrationService startupRegistrationService,
        SafeLogger logger,
        BackupMaintenanceService backupMaintenance)
    {
        this.paths = paths;
        this.profileManager = profileManager;
        this.activeProfileStore = activeProfileStore;
        this.settingsService = settingsService;
        this.switchService = switchService;
        this.processService = processService;
        this.startupRegistrationService = startupRegistrationService;
        this.logger = logger;
        this.backupMaintenance = backupMaintenance;
        settings = settingsService.Load();
        App.ApplyTheme(settings.Theme);
        localizer = new Localizer(settings.Language);
        visibilityState.AutomaticDisplayEnabled = settings.ShowAutomaticallyWhenCodexOpens;
        windowFinder = new CodexWindowFinder(logger);
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(750) };
        timer.Tick += (_, _) => TickSafely();
        statusStore = new ProfileStatusStore(paths.ProfileStatusFile);
        statusService = new ProfileStatusService(statusStore, new CodexCliStatusUsageProvider(), logger);
        stateMigrationService = new SharedCodexStateMigrationService(paths);
        statusService.SetStaleThreshold(TimeSpan.FromMinutes(settings.StaleDataThresholdMinutes));
        if (statusService.ProviderCapability != UsageProviderCapability.Supported && settings.ShowAutomaticLimitIndicators)
        {
            settings.ShowAutomaticLimitIndicators = false;
            settingsService.Save(settings);
        }
    }

    public void Start(bool afterInstallation = false)
    {
        EnsureOverlay();
        EnsureTray();
        RefreshProfiles();
        ApplySettings();
        timer.Start();
        if (!afterInstallation && settings.LaunchCodexWhenOverlayStarts)
        {
            _ = LaunchCodexAndWaitAsync(disposalTokenSource.Token);
        }

        Tick();
        _ = CheckForUpdatesAtStartupAsync();
        if (afterInstallation) overlayWindow?.ShowNotification(localizer.Format("UpdateInstalledVersion", updates.CurrentVersion));
    }

    private async Task CheckForUpdatesAtStartupAsync()
    {
        try { await updates.CheckAsync(false, disposalTokenSource.Token); }
        catch (OperationCanceledException) { }
        if (updates.HasUpdate && !disposalTokenSource.IsCancellationRequested)
            overlayWindow?.ShowNotification(localizer["UpdateAvailable"]);
    }

    public void Dispose()
    {
        disposalTokenSource.Cancel();
        timer.Stop();
        statusService.Dispose();
        updates.Dispose();
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
        trayIcon.OpenCodexRequested += () => _ = LaunchCodexAndWaitAsync(disposalTokenSource.Token);
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
        CodexWindowInfo? found = attachedWindow is null
            ? windowFinder.FindMainWindow()
            : windowFinder.RefreshKnownWindow(attachedWindow) ?? windowFinder.FindMainWindow();
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
        bool foregroundBelongsToCodexOrOverlay = ForegroundBelongsToCodexOrOverlay(found, overlayWindow!.Handle);
        bool shouldShowOverlay = visibilityState.ShouldShowOverlay
            && foregroundBelongsToCodexOrOverlay
            && IsCodexOrOverlayTopVisibleAtClientCenter(found);
        overlayWindow.AllowAutoShow = shouldShowOverlay;
        if (!shouldShowOverlay)
        {
            overlayWindow.Hide();
            trayIcon?.UpdateOverlayState(false);
            return;
        }

        overlayWindow.UpdatePlacement(found.Hwnd);
        trayIcon?.UpdateOverlayState(overlayWindow.IsVisible);
    }

    private void TickSafely()
    {
        try
        {
            BeginAutomaticUsageRefreshIfDue();
            Tick();
        }
        catch (Exception exception)
        {
            logger.Error("Overlay tracking tick failed.", exception);
        }
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

            RefreshStatusIndicators();
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

        EnsureOverlay();
        switching = true;
        AuthSwitchResult? switchResult = null;
        overlayWindow!.SetSwitching(true);
        try
        {
            if (settings.ConfirmBeforeForceClose && !ConfirmProfileSwitch(profileName))
            {
                return;
            }

            overlayWindow.Hide();
            overlayWindow.AllowAutoShow = false;
            trayIcon?.UpdateOverlayState(false);
            hotkeyManager?.Clear();
            overlayWindow.ShowNotification(localizer.Format("SwitchingToProfile", profileName));

            bool allowForceClose = settings.ForceCloseFallback;
            CodexDesktopProcessSession? shutdownSession = null;
            PreparedCodexLaunch? launchTarget;
            try
            {
                launchTarget = await CodexSwitchPreparation.PrepareAndCloseAsync(settings.LaunchCodexAfterSwitching,
                    () => processService.PrepareLaunchAsync(disposalTokenSource.Token),
                    async () => shutdownSession = await processService.CloseCodexAsync(settings.GracefulCloseTimeoutSeconds,
                        allowForceClose, disposalTokenSource.Token).ConfigureAwait(true), disposalTokenSource.Token).ConfigureAwait(true);
            }
            catch { shutdownSession?.Dispose(); throw; }
            using var completedShutdown = shutdownSession;
            MigrateLegacyProfileStateSafely();
            CodexShutdownCoordinator.VerifyStopped(completedShutdown!, disposalTokenSource.Token);
            switchResult = await switchService.SwitchAsync(profileName, disposalTokenSource.Token).ConfigureAwait(true);
            RefreshProfiles();
            if (settings.LaunchCodexAfterSwitching)
            {
                await LaunchCodexAndWaitAsync(disposalTokenSource.Token, throwOnFailure: true, launchTarget).ConfigureAwait(true);
            }

            overlayWindow?.ShowNotification(localizer.Format("SwitchedToAccount", profileName));
            logger.Info($"Switched to profile '{profileName}'.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (switchResult is not null)
            {
                try
                {
                    switchService.Rollback(switchResult);
                    RefreshProfiles();
                }
                catch (Exception rollbackException)
                {
                    logger.Error("Authorization rollback failed.", rollbackException);
                }
            }
            logger.Error($"Switch to profile '{profileName}' failed.", exception);
            overlayWindow?.ShowError(localizer["CouldNotSwitch"] + " " + localizer["PreviousAuthorizationRestored"] + ".");
            trayIcon?.ShowBalloon("Codex Profile Overlay", localizer["CouldNotSwitch"] + " " + localizer["PreviousAuthorizationRestored"] + ".");
        }
        finally
        {
            switching = false;
            overlayWindow?.SetSwitching(false);
            if (overlayWindow is not null)
            {
                overlayWindow.AllowAutoShow = true;
            }

            _ = RegisterHotkeys();
            Tick();
        }
    }

    private void MigrateLegacyProfileStateSafely()
    {
        try
        {
            SharedCodexStateMigrationResult result = stateMigrationService.MigrateLegacyProfileState();
            if (!result.WasAlreadyCompleted)
            {
                logger.Info($"Merged legacy profile state into shared Codex storage: {result.CopiedFileCount} files, {result.ImportedDatabaseRowCount} database rows from {result.LegacyProfileCount} profiles.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.Error("Could not merge legacy profile state into shared Codex storage.", exception);
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

        string? validProfileName = null;
        string? createdProfileName = null;
        ProfileLoginAttempt? loginAttempt = null;
        try
        {
            validProfileName = ProfileName.RequireValid(profileName);
            await CancelProfileLoginAsync(validProfileName).ConfigureAwait(true);

            loginAttempt = new ProfileLoginAttempt(CancellationTokenSource.CreateLinkedTokenSource(disposalTokenSource.Token));
            activeProfileLogins[validProfileName] = loginAttempt;

            string directory = profileManager.CreateProfileDirectory(validProfileName);
            createdProfileName = validProfileName;
            overlayWindow?.ShowNotification(localizer.Format("StartingLogin", validProfileName));
            bool loginCreatedAuth = await processService.LoginProfileAsync(directory, loginAttempt.Cancellation.Token).ConfigureAwait(true);
            if (!loginCreatedAuth)
            {
                RemoveIncompleteProfile(createdProfileName);
                RefreshProfiles();
                overlayWindow?.ShowError(localizer["AuthNotCreated"]);
                return;
            }

            RefreshProfiles();
            overlayWindow?.ShowNotification(localizer["ProfileAdded"]);
            bool switchNewProfile = ConfirmDialog.Show(
                dialogOwner,
                dialogOwner is null ? CurrentCodexWindowHandle() : IntPtr.Zero,
                localizer["ConfirmSwitchTitle"],
                localizer["SwitchNewProfile"],
                localizer["ConfirmSwitchPrimary"],
                localizer["Cancel"]);
            if (switchNewProfile)
            {
                await SwitchProfileAsync(validProfileName).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (!disposalTokenSource.IsCancellationRequested)
        {
            RemoveIncompleteProfile(createdProfileName);
            RefreshProfiles();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RemoveIncompleteProfile(createdProfileName);
            RefreshProfiles();
            logger.Error("Add profile failed.", exception);
            overlayWindow?.ShowError(localizer["CouldNotAddProfile"]);
        }
        finally
        {
            if (validProfileName is not null && loginAttempt is not null)
            {
                if (activeProfileLogins.TryGetValue(validProfileName, out ProfileLoginAttempt? activeAttempt) && ReferenceEquals(activeAttempt, loginAttempt))
                {
                    activeProfileLogins.Remove(validProfileName);
                }

                loginAttempt.Complete();
                loginAttempt.Dispose();
            }
        }
    }

    private void RemoveIncompleteProfile(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return;
        }

        try
        {
            _ = profileManager.RemoveIncompleteProfileDirectory(profileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.Error("Could not remove incomplete profile directory.", exception);
        }
    }

    private async Task CancelProfileLoginAsync(string profileName)
    {
        if (!activeProfileLogins.TryGetValue(profileName, out ProfileLoginAttempt? loginAttempt))
        {
            return;
        }

        loginAttempt.Cancel();
        Task completed = await Task.WhenAny(
            loginAttempt.Completion,
            Task.Delay(TimeSpan.FromSeconds(5), disposalTokenSource.Token)).ConfigureAwait(true);
        if (completed != loginAttempt.Completion)
        {
            logger.Info($"Timed out waiting for previous login attempt for '{profileName}' to stop.");
        }
    }

    private sealed class ProfileLoginAttempt : IDisposable
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProfileLoginAttempt(CancellationTokenSource cancellation)
        {
            Cancellation = cancellation;
        }

        public CancellationTokenSource Cancellation { get; }

        public Task Completion => completion.Task;

        public void Cancel()
        {
            Cancellation.Cancel();
        }

        public void Complete()
        {
            completion.TrySetResult();
        }

        public void Dispose()
        {
            Cancellation.Dispose();
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
            statusService,
            backupMaintenance,
            updates,
            SaveSettings,
            RefreshStatusIndicators,
            RefreshUsageForProfileAsync,
            () => _ = AddProfileAsync(),
            ShowProfileManager,
            () => OpenFolder(paths.ProfilesDirectory),
            () => OpenFolder(paths.RemovedProfilesDirectory),
            () => OpenFolder(paths.ApplicationDataDirectory),
            () => OpenFolder(paths.BackupDirectory),
            () => OpenFolder(paths.LogDirectory),
            ResetPosition,
            ResetSettings,
            () => Application.Current.Shutdown(),
            () => !switching);
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

        if (!ConfirmDialog.Show(
            profileManagerWindow,
            profileManagerWindow is null ? CurrentCodexWindowHandle() : IntPtr.Zero,
            localizer["Remove"],
            localizer["RemoveProfilePrompt"],
            localizer["Remove"],
            localizer["Cancel"],
            danger: true))
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
        profileManagerWindow?.RefreshTheme();
        trayIcon?.UpdateStartWithWindows(settings.StartWithWindows);
        _ = RegisterHotkeys();
    }

    private void SaveSettings(OverlaySettings updatedSettings)
    {
        try
        {
            settings = updatedSettings;
            if (statusService.ProviderCapability != UsageProviderCapability.Supported)
            {
                settings.ShowAutomaticLimitIndicators = false;
            }

            statusService.SetStaleThreshold(TimeSpan.FromMinutes(settings.StaleDataThresholdMinutes));
            localizer.SetLanguage(settings.Language);
            visibilityState.AutomaticDisplayEnabled = settings.ShowAutomaticallyWhenCodexOpens;
            settingsService.Save(settings);
            ApplySettings();
            RefreshStatusIndicators();
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

    private void RefreshStatusIndicators()
    {
        ProfileStatusDocument statusDocument = statusService.Load();
        string? recommendedProfile = settings.ShowAutomaticLimitIndicators
            ? statusService.FindRecommendedProfile(profiles.Select(profile => profile.Name).ToArray(), statusDocument)
            : null;
        overlayWindow?.SetStatusDocument(statusDocument, recommendedProfile);
    }

    private async Task RefreshUsageForProfileAsync(string profileName)
    {
        if (switching || !UsageRefreshPolicy.AllowsAutomaticRefresh(settings, statusService.ProviderCapability))
        {
            return;
        }

        ProfileInfo? profile = profiles.FirstOrDefault(item => item.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return;
        }

        ProfileStatusDocument document = statusService.Load();
        await statusService.RefreshUsageAsync(profile.Name, profile.DirectoryPath, document, disposalTokenSource.Token).ConfigureAwait(true);
        lastUsageRefreshAttempts[profile.Name] = DateTimeOffset.UtcNow;
        RefreshStatusIndicators();
    }

    private void BeginAutomaticUsageRefreshIfDue()
    {
        if (automaticUsageRefreshRunning
            || switching
            || profiles.Count == 0
            || !UsageRefreshPolicy.AllowsAutomaticRefresh(settings, statusService.ProviderCapability))
        {
            return;
        }

        ProfileStatusDocument document = statusService.Load();
        string? activeProfile = activeProfileStore.Read();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var dueProfiles = profiles
            .Where(profile =>
            {
                ProfileStatusMetadata metadata = statusService.GetOrCreateStatus(document, profile.Name);
                if (!metadata.AutomaticRefreshEnabled)
                {
                    return false;
                }

                TimeSpan interval = profile.Name.Equals(activeProfile, StringComparison.OrdinalIgnoreCase)
                    ? TimeSpan.FromMinutes(settings.ActiveProfileRefreshIntervalMinutes)
                    : TimeSpan.FromMinutes(settings.InactiveProfileRefreshIntervalMinutes);
                if (!string.IsNullOrWhiteSpace(metadata.LastRefreshError))
                {
                    interval = TimeSpan.FromMinutes(1);
                }

                DateTimeOffset lastAttempt = lastUsageRefreshAttempts.TryGetValue(profile.Name, out DateTimeOffset inMemoryAttempt)
                    ? inMemoryAttempt
                    : metadata.LastRefreshAttemptAt ?? DateTimeOffset.MinValue;
                return now - lastAttempt >= interval;
            })
            .ToArray();

        if (dueProfiles.Length == 0)
        {
            return;
        }

        automaticUsageRefreshRunning = true;
        _ = RefreshDueProfilesAsync(dueProfiles);
    }

    private async Task RefreshDueProfilesAsync(IReadOnlyList<ProfileInfo> dueProfiles)
    {
        try
        {
            foreach (ProfileInfo profile in dueProfiles)
            {
                if (disposalTokenSource.IsCancellationRequested
                    || switching
                    || !UsageRefreshPolicy.AllowsAutomaticRefresh(settings, statusService.ProviderCapability))
                {
                    return;
                }

                ProfileStatusDocument document = statusService.Load();
                await statusService.RefreshUsageAsync(profile.Name, profile.DirectoryPath, document, disposalTokenSource.Token).ConfigureAwait(true);
                lastUsageRefreshAttempts[profile.Name] = DateTimeOffset.UtcNow;
                RefreshStatusIndicators();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.Error("Automatic usage refresh failed.", exception);
        }
        finally
        {
            automaticUsageRefreshRunning = false;
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
        App.ApplyTheme(settings.Theme);
        overlayWindow?.ApplySettings();
        settingsWindow?.RefreshTheme();
        profileManagerWindow?.RefreshTheme();
        RefreshProfiles();
    }

    private bool ConfirmProfileSwitch(string profileName)
    {
        return ConfirmDialog.Show(
            PromptOwner(),
            CurrentCodexWindowHandle(),
            localizer["ConfirmSwitchTitle"],
            localizer.Format("ConfirmSwitchPrompt", profileName),
            localizer["ConfirmSwitchPrimary"],
            localizer["Cancel"],
            danger: true);
    }

    private static bool ForegroundBelongsToCodexOrOverlay(CodexWindowInfo codexWindow, IntPtr overlayHandle)
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        if (IsDesktopShellWindow(foreground))
        {
            return false;
        }

        if (foreground == codexWindow.Hwnd)
        {
            return true;
        }

        if (foreground == overlayHandle)
        {
            return true;
        }

        NativeMethods.GetWindowThreadProcessId(foreground, out uint processId);
        return processId == Environment.ProcessId || processId == codexWindow.ProcessId;
    }

    private static bool IsDesktopShellWindow(IntPtr hwnd)
    {
        string className = NativeMethods.GetWindowClassName(hwnd);
        return className.Equals("Progman", StringComparison.OrdinalIgnoreCase)
            || className.Equals("WorkerW", StringComparison.OrdinalIgnoreCase)
            || className.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCodexOrOverlayTopVisibleAtClientCenter(CodexWindowInfo codexWindow)
    {
        if (codexWindow.IsMinimized || !NativeMethods.GetClientRect(codexWindow.Hwnd, out NativeRect clientRect))
        {
            return false;
        }

        var point = new NativePoint
        {
            X = Math.Max(0, clientRect.Width / 2),
            Y = Math.Max(0, clientRect.Height / 2),
        };

        if (!NativeMethods.ClientToScreen(codexWindow.Hwnd, ref point))
        {
            return false;
        }

        IntPtr hit = NativeMethods.WindowFromPoint(point);
        if (hit == IntPtr.Zero)
        {
            return false;
        }

        IntPtr root = NativeMethods.GetAncestor(hit, NativeMethods.GaRoot);
        if (root == codexWindow.Hwnd)
        {
            return true;
        }

        NativeMethods.GetWindowThreadProcessId(root == IntPtr.Zero ? hit : root, out uint processId);
        return processId == codexWindow.ProcessId || processId == Environment.ProcessId;
    }

    private async Task LaunchCodexAndWaitAsync(CancellationToken cancellationToken, bool throwOnFailure = false, PreparedCodexLaunch? launchTarget = null)
    {
        try
        {
            CodexWindowInfo? existing = attachedWindow is null
                ? windowFinder.FindMainWindow()
                : windowFinder.RefreshKnownWindow(attachedWindow) ?? windowFinder.FindMainWindow();
            if (existing is not null)
            {
                attachedWindow = existing;
                overlayWindow?.AttachTo(existing.Hwnd);
                overlayWindow?.UpdatePlacement(existing.Hwnd);
                return;
            }

            launchTarget ??= await processService.PrepareLaunchAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            processService.LaunchCodex(launchTarget);
            if (!await WaitForCodexWindowAsync(cancellationToken, TimeSpan.FromSeconds(20)).ConfigureAwait(true))
            {
                throw new InvalidOperationException("Codex window did not appear after launch.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Error("Could not launch Codex.", exception);
            overlayWindow?.ShowError(localizer["CodexCouldNotLaunch"]);
            trayIcon?.ShowBalloon("Codex Profile Overlay", localizer["CodexCouldNotLaunch"]);
            if (throwOnFailure)
            {
                throw;
            }
        }
    }

    private async Task<bool> WaitForCodexWindowAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(20));
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
                return true;
            }
        }

        logger.Info("Timed out waiting for Codex window after launch.");
        return false;
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
