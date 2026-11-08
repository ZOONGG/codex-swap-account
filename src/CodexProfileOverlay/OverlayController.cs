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
    private readonly DesktopSessionService desktopSessions;
    private readonly CodexProcessService processService;
    private readonly IStartupRegistrationService startupRegistrationService;
    private readonly SafeLogger logger;
    private readonly BackupMaintenanceService backupMaintenance;
    private readonly CodexWindowFinder windowFinder;
    private readonly DispatcherTimer timer;
    private readonly AppPaths paths;
    private readonly OverlayVisibilityState visibilityState = new();
    private readonly CancellationTokenSource disposalTokenSource = new();
    private bool accountOperation;
    private bool installingUpdate;
    private readonly Dictionary<string, DateTimeOffset> accountVerifiedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AccountAvailability> accountStates = new(StringComparer.OrdinalIgnoreCase);
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
    private readonly bool installed;
    private string? notifiedUpdateTag;
    private ProfileManagerWindow? profileManagerWindow;
    private HotkeyManager? hotkeyManager;
    private IReadOnlyList<ProfileInfo> profiles = [];
    private CodexWindowInfo? attachedWindow;
    private readonly ProfileUsageOperations usageOperations = new();
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
        desktopSessions = new DesktopSessionService(paths);
        this.processService = processService;
        this.startupRegistrationService = startupRegistrationService;
        this.logger = logger;
        this.backupMaintenance = backupMaintenance;
        settings = settingsService.Load();
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(InstallationMode.UninstallKey))
            installed = InstallationMode.IsInstalled(Environment.ProcessPath!, key?.GetValue("InstallLocation") as string);
        updates.Changed += UpdateStateChanged;
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
        if (desktopSessions.HasPendingOperation) ShowDesktopError("DesktopRecoveryRequired");
        else if (!afterInstallation && settings.LaunchCodexWhenOverlayStarts)
        {
            _ = LaunchCodexAndWaitAsync(disposalTokenSource.Token);
        }

        Tick();
        var updateCancellation = disposalTokenSource.Token;
        _ = Task.Run(() => CheckForUpdatesAtStartupAsync(updateCancellation));
        if (afterInstallation) overlayWindow?.ShowNotification(localizer.Format("UpdateInstalledVersion", updates.CurrentVersion));
    }

    private async Task CheckForUpdatesAtStartupAsync(CancellationToken cancellation)
    {
        try
        {
            using var interval = new PeriodicTimer(TimeSpan.FromMinutes(15));
            do { await updates.CheckAndDownloadAsync(false, cancellation); }
            while (await interval.WaitForNextTickAsync(cancellation));
        }
        catch (OperationCanceledException) { }
    }

    private void UpdateStateChanged()
    {
        if (disposalTokenSource.IsCancellationRequested) return;
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (disposalTokenSource.IsCancellationRequested || !updates.IsReady || updates.Cache.Release?.Tag == notifiedUpdateTag) return;
            notifiedUpdateTag = updates.Cache.Release!.Tag;
            overlayWindow?.ShowNotification(localizer.Format("UpdatePackageReady", notifiedUpdateTag));
        });
    }

    public void Dispose()
    {
        disposalTokenSource.Cancel();
        timer.Stop();
        statusService.Dispose();
        updates.Changed -= UpdateStateChanged;
        updates.Dispose();
        hotkeyManager?.Dispose();
        trayIcon?.Dispose();
        settingsWindow?.CloseForShutdown();
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
            OnRefreshUsage = RefreshUsageForProfileAsync,
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

        hotkeyManager = new HotkeyManager(overlayWindow.Handle, logger);
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
        trayIcon.OpenCodexRequested += () => _ = OpenCodexOrRecoverAsync();
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

    private void RefreshProfiles() => TryRefreshProfiles();

    private bool TryRefreshProfiles()
    {
        try
        {
            if (desktopSessions.HasPendingOperation) return false;
            profileManager.EnsureMetadata();
            profiles = profileManager.ListProfiles();
            string? activeProfile = activeProfileStore.Read();
            overlayWindow?.SetProfiles(profiles, activeProfile);
            trayIcon?.UpdateProfiles(profiles, activeProfile);
            settingsWindow?.UpdateProfiles(profiles);
            settingsWindow?.SetConflicts(RegisterHotkeys());
            profileManagerWindow?.UpdateProfiles(profiles, activeProfile);

            RefreshStatusIndicators();
            return true;
        }
        catch (Exception exception)
        {
            try { logger.Error("Profile refresh failed.", exception); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            overlayWindow?.ShowError(localizer["CouldNotRefreshProfiles"]);
            return false;
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
        if (installingUpdate || switching || accountOperation) return;
        if (desktopSessions.HasPendingOperation)
        {
            await RunDesktopOperationAsync(null, capture: false);
            return;
        }
        if (string.Equals(profileName, activeProfileStore.Read(), StringComparison.OrdinalIgnoreCase)) return;
        if (desktopSessions.RequiresDesktopSwitch(profileName))
        {
            await RunDesktopOperationAsync(profileName, capture: false);
            return;
        }

        EnsureOverlay();
        switching = true;
        AuthSwitchResult? switchResult = null;
        bool relaunchAttempted = false;
        IDisposable? backupLease = null;
        overlayWindow!.SetSwitching(true);
        try
        {
            if ((settings.ConfirmBeforeForceClose || settings.ForceCloseFallback) && !ConfirmProfileSwitch(profileName))
            {
                return;
            }

            if (switchService.HasPendingSwitch) await usageOperations.DrainAsync(disposalTokenSource.Token);
            else await usageOperations.DrainAsync(disposalTokenSource.Token, profileName, activeProfileStore.Read());
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
            backupLease = backupMaintenance.ProtectSwitchBackups();
            switchResult = await switchService.SwitchAsync(profileName, disposalTokenSource.Token).ConfigureAwait(true);
            RefreshProfiles();
            if (settings.LaunchCodexAfterSwitching)
            {
                relaunchAttempted = true;
                await LaunchCodexAndWaitAsync(disposalTokenSource.Token, throwOnFailure: true, launchTarget).ConfigureAwait(true);
            }

            overlayWindow?.ShowNotification(localizer.Format("SwitchedToAccount", profileName));
            logger.Info("Switched an auth-only profile.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            bool restored = false;
            if (switchResult is not null)
            {
                try
                {
                    // Activation can start writers even if no window ever appears.
                    using var rollbackShutdown = relaunchAttempted
                        ? await processService.CloseCodexAsync(settings.GracefulCloseTimeoutSeconds,
                            settings.ForceCloseFallback, CancellationToken.None)
                        : null;
                    if (rollbackShutdown is not null)
                        CodexShutdownCoordinator.VerifyStopped(rollbackShutdown, CancellationToken.None);
                    switchService.Rollback(switchResult);
                    restored = true;
                    RefreshProfiles();
                }
                catch (Exception rollbackException)
                {
                    logger.Error("Authorization rollback failed.", rollbackException);
                }
            }
            logger.Error("Auth-only profile switch failed.", exception);
            string message = localizer["CouldNotSwitch"];
            if (restored) message += " " + localizer["PreviousAuthorizationRestored"] + ".";
            overlayWindow?.ShowError(message);
            trayIcon?.ShowBalloon("Codex Profile Overlay", message);
        }
        finally
        {
            backupLease?.Dispose();
            switching = false;
            if (backupLease is not null) backupMaintenance.TryCleanupRetention();
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
        if (installingUpdate || switching || accountOperation) return;
        EnsureOverlay();
        if (desktopSessions.HasPendingOperation)
        {
            await RunDesktopOperationAsync(null, capture: false);
            return;
        }
        string? name;
        bool capture;
        accountOperation = true;
        try
        {
            Window? owner = PromptOwner();
            (name, capture) = PromptDialog.ShowProfileChoice(owner, owner is null ? CurrentCodexWindowHandle() : IntPtr.Zero, localizer);
        }
        finally { accountOperation = false; }
        if (name is null) return;
        string? error = !ProfileName.IsValid(name) ? "InvalidProfileName"
            : Directory.Exists(Path.Combine(paths.ProfilesDirectory, name)) || File.Exists(Path.Combine(paths.ProfilesDirectory, name)) ? "DuplicateProfileName" : null;
        if (error is not null)
        {
            System.Windows.MessageBox.Show(PromptOwner(), localizer[error], localizer["AddProfileTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (capture) await RunDesktopOperationAsync(name, capture: true);
        else await LoginAccountAsync(name, false);
    }

    private Task OpenCodexOrRecoverAsync()
    {
        if (installingUpdate || switching || accountOperation) return Task.CompletedTask;
        return desktopSessions.HasPendingOperation
            ? RunDesktopOperationAsync(null, capture: false)
            : LaunchCodexAndWaitAsync(disposalTokenSource.Token);
    }

    private async Task RunDesktopOperationAsync(string? name, bool capture)
    {
        if (installingUpdate || switching || accountOperation)
        {
            overlayWindow?.ShowNotification(localizer["AccountBusy"]);
            return;
        }
        accountOperation = true;
        bool recovery = name is null;
        string title = recovery ? "DesktopRecover" : capture ? "DesktopCapture" : "DesktopSwitch";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(disposalTokenSource.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, deadline.Token);
        CancellationToken token = combined.Token;
        PreparedCodexLaunch? launch = null;
        string? live = null;
        CodexDesktopProcessSession? shutdown = null;
        IDisposable? lease = null;
        LoginProgressWindow? progress = null;
        bool closeRequested = false;
        bool targetDesktop = false;
        bool force = settings.ForceCloseFallback;
        string failure = recovery ? "DesktopRecoveryRequired" : "DesktopPreflightFailed";
        try
        {
            launch = await processService.PrepareLaunchAsync(token);
            targetDesktop = name is not null && !capture && desktopSessions.IsDesktopProfile(name);
            string? capturedProfile = targetDesktop ? name : activeProfileStore.Read();
            live = WindowsDesktopStorage.Resolve(paths, desktopSessions, capturedProfile, capture);
            if (capture) desktopSessions.PreflightCapture(name!, live);
            else if (!recovery) desktopSessions.PreflightSwitch(name!, live);

            string confirmKey = recovery ? "DesktopRecoverConfirm" : capture ? "DesktopCaptureConfirm"
                : targetDesktop ? "DesktopSwitchConfirm" : "DesktopLeaveConfirm";
            string message = localizer[confirmKey] + (force ? " " + localizer["DesktopForceConfirm"] : "");
            Window? owner = PromptOwner();
            if (!ConfirmDialog.Show(owner, owner is null ? CurrentCodexWindowHandle() : IntPtr.Zero,
                localizer[title], message, localizer[title], localizer["Cancel"])) return;

            if (!string.Equals(live, WindowsDesktopStorage.Resolve(paths, desktopSessions, capturedProfile, capture), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The live Desktop data directory changed during confirmation.");
            if (recovery) await usageOperations.DrainAsync(token);
            else await usageOperations.DrainAsync(token, name, capture ? null : activeProfileStore.Read());
            failure = "DesktopCloseFailed";
            progress = new LoginProgressWindow(localizer, cancellation, title, "DesktopWorking") { ShowInTaskbar = true };
            DialogPlacement.CenterOnOwnerScreen(progress, owner, CurrentCodexWindowHandle());
            progress.Show();
            closeRequested = true;
            shutdown = await processService.CloseCodexAsync(settings.GracefulCloseTimeoutSeconds, force, token);
            if (capture && !shutdown.ObservedDesktop)
                throw new InvalidOperationException("The verified Desktop session closed before capture.");
            // Resolve the registered version again after confirmation and shutdown.
            launch = await processService.PrepareLaunchAsync(token);
            lease = WindowsDesktopStorage.PreventLaunch(launch, shutdown);
            failure = "DesktopOperationFailed";
            await Task.Run(() =>
            {
                if (recovery) desktopSessions.Recover(live, () => VerifyStopped(token));
                else if (capture) desktopSessions.Capture(name!, live, () => VerifyStopped(token), token);
                else desktopSessions.Switch(name!, live, () => VerifyStopped(token), token);
            }, token);
            token.ThrowIfCancellationRequested();
            lease.Dispose();
            lease = null;

            if (capture || recovery || settings.LaunchCodexAfterSwitching)
            {
                failure = "DesktopLaunchFailed";
                await LaunchCodexAndWaitAsync(token, throwOnFailure: true, launch);
                var window = attachedWindow;
                await Task.Delay(TimeSpan.FromMilliseconds(750), token);
                if (window is null || windowFinder.RefreshKnownWindow(window) is null)
                    throw new InvalidOperationException("Codex did not keep a verified window after relaunch.");
            }
            if (!recovery) desktopSessions.Complete(live);
            if (capture)
            {
                accountStates[name!] = AccountAvailability.Available;
                accountVerifiedAt[name!] = DateTimeOffset.UtcNow;
                try { statusService.ClearCachedUsage(name!); }
                catch (Exception exception) { logger.Error("Could not clear cached usage after Desktop capture.", exception); }
            }
            RefreshProfiles();
            overlayWindow?.ShowNotification(localizer[recovery ? "DesktopRecovered" : capture ? "ProfileAdded"
                : targetDesktop ? "DesktopSwitched" : "DesktopLeft"]);
        }
        catch (Exception exception)
        {
            logger.Error("Desktop session operation failed.", exception);
            bool rolledBack = false;
            if (!recovery && live is not null && desktopSessions.HasPendingOperation)
            {
                try
                {
                    if (lease is null)
                    {
                        // Activation may have started a process even when the window never appeared.
                        shutdown?.Dispose();
                        shutdown = await processService.CloseCodexAsync(settings.GracefulCloseTimeoutSeconds, force, CancellationToken.None);
                        launch = await processService.PrepareLaunchAsync(CancellationToken.None);
                        lease = WindowsDesktopStorage.PreventLaunch(launch, shutdown);
                    }
                    await Task.Run(() => desktopSessions.Recover(live, () => VerifyStopped(CancellationToken.None)));
                    rolledBack = true;
                }
                catch (Exception rollback)
                {
                    logger.Error("Desktop session rollback requires recovery.", rollback);
                    ShowDesktopError("DesktopRecoveryRequired");
                    return;
                }
            }

            lease?.Dispose();
            lease = null;
            if (!recovery && closeRequested && launch is not null && !disposalTokenSource.IsCancellationRequested)
            {
                try { await LaunchCodexAndWaitAsync(disposalTokenSource.Token, throwOnFailure: true, launch); }
                catch (Exception relaunch) { logger.Error("Could not reopen Codex after Desktop rollback.", relaunch); }
            }
            if (desktopSessions.HasPendingOperation) ShowDesktopError("DesktopRecoveryRequired");
            else if (deadline.IsCancellationRequested) ShowDesktopError("DesktopTimedOut");
            else if (exception is DesktopAccountChangedException) ShowDesktopError("DesktopAccountChanged");
            else if (exception is not OperationCanceledException)
                ShowDesktopError(rolledBack ? "DesktopRolledBack" : failure);
        }
        finally
        {
            lease?.Dispose();
            shutdown?.Dispose();
            if (progress?.IsVisible == true) progress.Close();
            accountOperation = false;
            RefreshProfiles();
        }

        void VerifyStopped(CancellationToken cancellationToken) =>
            CodexShutdownCoordinator.VerifyStopped(shutdown!, cancellationToken);
    }

    private void ShowDesktopError(string key)
    {
        overlayWindow?.ShowError(localizer[key]);
        trayIcon?.ShowBalloon("Codex Profile Overlay", localizer[key]);
    }

    private async Task LoginAccountAsync(string name, bool reauthorize)
    {
        if (installingUpdate || switching || accountOperation)
        {
            overlayWindow?.ShowNotification(localizer["AccountBusy"]);
            return;
        }
        if (desktopSessions.HasPendingOperation)
        {
            ShowDesktopError("DesktopRecoveryRequired");
            return;
        }
        if (reauthorize && desktopSessions.IsDesktopProfile(name))
        {
            ShowDesktopError("DesktopReauthorize");
            return;
        }
        accountOperation = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(disposalTokenSource.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, timeout.Token);
        var progress = new LoginProgressWindow(localizer, cancellation);
        CodexDesktopProcessSession? shutdown = null;
        PreparedCodexLaunch? launch = null;
        bool stopped = false;
        bool added = false;
        try
        {
            await usageOperations.DrainAsync(loginCancellation.Token, name);
            var runtime = new OfficialProfileLoginRuntime();
            runtime.EnsureAvailable();
            Window? owner = PromptOwner();
            if (owner is not null) progress.Owner = owner;
            // Keep browser sign-in reachable from the taskbar, including when Codex closes.
            progress.ShowInTaskbar = true;
            DialogPlacement.CenterOnOwnerScreen(progress, owner, CurrentCodexWindowHandle());
            progress.Show();
            BringToFront(progress);
            var service = new ProfileLoginService(paths, profileManager, runtime);
            bool success = await service.LoginAsync(name, reauthorize, token =>
                Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    if (!ConfirmDialog.Show(progress, IntPtr.Zero, localizer["Reauthorize"],
                        localizer["ReauthorizeActiveConfirm"], localizer["Reauthorize"], localizer["Cancel"])) return false;
                    // Codex owns the auxiliary windows natively. Detach before closing it so
                    // Windows cannot destroy the login window with that owner chain.
                    progress.Owner = null;
                    launch = await processService.PrepareLaunchAsync(token);
                    shutdown = await processService.CloseCodexAsync(settings.GracefulCloseTimeoutSeconds,
                        settings.ForceCloseFallback, token);
                    CodexShutdownCoordinator.VerifyStopped(shutdown, token);
                    stopped = true;
                    return true;
                }).Task.Unwrap(), loginCancellation.Token);
            if (success)
            {
                added = !reauthorize;
                accountStates[name] = AccountAvailability.Available;
                accountVerifiedAt[name] = DateTimeOffset.UtcNow;
                try { statusService.ClearCachedUsage(name); }
                catch (Exception) { logger.Error("Could not clear cached usage after successful login."); }
                RefreshProfiles();
                overlayWindow?.ShowNotification(localizer[reauthorize ? "Reauthorized" : "ProfileAdded"]);
            }
            else if (!loginCancellation.IsCancellationRequested) ShowLoginError("LoginFailed");
        }
        catch (LoginRuntimeUnavailableException)
        {
            System.Windows.MessageBox.Show(progress.IsVisible ? progress : PromptOwner(), localizer["LoginRuntimeMissing"], localizer["BrowserLogin"],
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (LoginProcessFailedException) { ShowLoginError("LoginProcessFailed"); }
        catch (LoginAuthMissingException) { ShowLoginError("LoginAuthMissing"); }
        catch (OperationCanceledException)
        {
            if (timeout.IsCancellationRequested && !cancellation.IsCancellationRequested) ShowLoginError("LoginTimedOut");
        }
        catch (UnauthorizedAccessException) { ShowLoginError("LoginAccessDenied"); }
        catch (TimeoutException) { ShowLoginError("LoginCleanupFailed"); }
        catch (LoginCleanupException) { ShowLoginError("LoginCleanupFailed"); }
        catch (Exception)
        {
            // Never include runtime output or credential parser errors in logs.
            logger.Error("Account login failed.");
            ShowLoginError("LoginFailed");
        }
        finally
        {
            progress.Close();
            if (stopped && launch is not null && !disposalTokenSource.IsCancellationRequested)
            {
                try { await LaunchCodexAndWaitAsync(disposalTokenSource.Token, throwOnFailure: true, launch); }
                catch (Exception) { overlayWindow?.ShowError(localizer["LoginRelaunchFailed"]); }
            }
            shutdown?.Dispose();
            accountOperation = false;
            RefreshProfiles();
        }
        if (added && !disposalTokenSource.IsCancellationRequested && ConfirmDialog.Show(
            PromptOwner(), CurrentCodexWindowHandle(), localizer["ConfirmSwitchTitle"], localizer["SwitchNewProfile"],
            localizer["ConfirmSwitchPrimary"], localizer["Cancel"]))
        {
            await SwitchProfileAsync(name);
        }

        void ShowLoginError(string key)
        {
            if (!disposalTokenSource.IsCancellationRequested)
                System.Windows.MessageBox.Show(progress.IsVisible ? progress : PromptOwner(), localizer[key],
                    localizer["BrowserLogin"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private AccountAvailability GetAccountState(ProfileInfo profile)
    {
        AccountAvailability state = accountStates.GetValueOrDefault(profile.Name, AccountAvailability.Unknown);
        if (state == AccountAvailability.Available
            && (!accountVerifiedAt.TryGetValue(profile.Name, out var verified)
                || DateTimeOffset.UtcNow - verified > TimeSpan.FromMinutes(15))) return AccountAvailability.Unknown;
        return state;
    }

    private async Task CheckAccountAsync(ProfileInfo profile)
    {
        if (installingUpdate || switching || accountOperation)
        {
            overlayWindow?.ShowNotification(localizer["AccountBusy"]);
            return;
        }
        accountOperation = true;
        try
        {
            await usageOperations.DrainAsync(disposalTokenSource.Token, profile.Name);
            accountStates[profile.Name] = await new AccountStatusChecker(paths).CheckAsync(profile.DirectoryPath, disposalTokenSource.Token);
            profileManagerWindow?.UpdateProfiles(profiles, activeProfileStore.Read());
        }
        catch (Exception) { accountStates[profile.Name] = AccountAvailability.Unknown; }
        finally
        {
            accountOperation = false;
            profileManagerWindow?.UpdateProfiles(profiles, activeProfileStore.Read());
        }
    }

    private void ShowSettingsWindow()
    {
        if (settingsWindow is not null)
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
            TestUsageProviderAsync,
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
            () => !switching && !accountOperation && !desktopSessions.HasPendingOperation,
            value => installingUpdate = value);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        ShowCodexOwnedWindow(settingsWindow);
        settingsWindow.SetConflicts(RegisterHotkeys());
    }

    private void ShowProfileManager()
    {
        if (profileManagerWindow is not null)
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
            profile => _ = RemoveProfileAsync(profile),
            ReorderProfiles,
            profile => OpenFolder(profile.DirectoryPath),
            RefreshProfiles,
            profile => _ = LoginAccountAsync(profile.Name, true),
            profile => _ = CheckAccountAsync(profile),
            GetAccountState);
        profileManagerWindow.Closed += (_, _) => profileManagerWindow = null;
        ShowCodexOwnedWindow(profileManagerWindow);
    }

    private void RenameDisplayName(ProfileInfo profile)
    {
        if (accountOperation || switching || desktopSessions.HasPendingOperation) return;
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
        if (installingUpdate || accountOperation || switching || desktopSessions.HasPendingOperation) return;
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

    private async Task RemoveProfileAsync(ProfileInfo profile)
    {
        if (installingUpdate || accountOperation || switching) return;
        accountOperation = true;
        try
        {
            if (desktopSessions.HasPendingOperation)
            {
                ShowRemovalError(localizer["DesktopRecoveryRequired"]);
                return;
            }
            if (string.Equals(profile.Name, activeProfileStore.Read(), StringComparison.OrdinalIgnoreCase))
            {
                ShowRemovalError(localizer["ActiveProfileCannotRemove"]);
                return;
            }
            Window? owner = PromptOwner();
            if (!ConfirmDialog.Show(owner, owner is null ? CurrentCodexWindowHandle() : IntPtr.Zero,
                localizer["Remove"], localizer["RemoveProfilePrompt"], localizer["Remove"], localizer["Cancel"], danger: true))
                return;
            // Re-read after the modal dispatcher loop.
            if (desktopSessions.HasPendingOperation)
            {
                ShowRemovalError(localizer["DesktopRecoveryRequired"]);
                return;
            }
            await usageOperations.DrainAsync(disposalTokenSource.Token, profile.Name);
            profileManager.RemoveProfile(profile.Name, activeProfileStore.Read());
            try { statusService.ClearCachedUsage(profile.Name); }
            catch (Exception exception) { logger.Error("Could not clear cached usage after removal.", exception); }
            if (!TryRefreshProfiles())
            {
                ShowRemovalError(localizer["CouldNotRefreshProfiles"]);
                return;
            }
            overlayWindow?.ShowNotification(localizer["ProfileRemoved"]);
        }
        catch (OperationCanceledException) when (disposalTokenSource.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // No paths or parsed metadata in removal diagnostics.
            try { logger.Info($"Profile removal failed: {exception.GetType().Name}, HRESULT=0x{exception.HResult:X8}."); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            RefreshProfiles();
            ShowRemovalError(localizer["CouldNotRemoveProfile"]);
        }
        finally { accountOperation = false; }
    }

    private void ShowRemovalError(string message)
    {
        Window? owner = PromptOwner();
        if (owner is not null) System.Windows.MessageBox.Show(owner, message, localizer["Remove"], MessageBoxButton.OK, MessageBoxImage.Warning);
        else System.Windows.MessageBox.Show(message, localizer["Remove"], MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ReorderProfiles(IReadOnlyList<string> orderedNames)
    {
        if (accountOperation || switching || desktopSessions.HasPendingOperation) return;
        profileManager.Reorder(orderedNames);
        RefreshProfiles();
    }

    private Window? PromptOwner()
    {
        if (profileManagerWindow is { IsVisible: true, IsActive: true }) return profileManagerWindow;
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
        updates.ConfigureBackgroundDownloads(installed, settings.AutomaticallyDownloadUpdates);
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
        trayIcon?.UpdateUsage(statusDocument.Snapshots.GetValueOrDefault(activeProfileStore.Read() ?? string.Empty), settings);
    }

    private Task<bool> RunUsageRefreshAsync(ProfileInfo profile) =>
        usageOperations.RunAsync(profile.Name, token =>
            statusService.RefreshUsageAsync(profile.Name, profile.DirectoryPath, statusService.Load(), token),
            disposalTokenSource.Token);

    private async Task<bool> TestUsageProviderAsync(string profileName)
    {
        if (installingUpdate || switching || accountOperation || desktopSessions.HasPendingOperation || switchService.HasPendingSwitch) return false;
        ProfileInfo? profile = profiles.FirstOrDefault(current =>
            string.Equals(current.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return false;
        bool supported = false;
        bool completed = await usageOperations.RunAsync(profile.Name, async token =>
        {
            supported = await statusService.TestProviderSupportAsync(profile.DirectoryPath, token);
        }, disposalTokenSource.Token);
        return completed && supported;
    }

    private async Task RefreshUsageForProfileAsync(string profileName)
    {
        if (installingUpdate || switching || accountOperation || desktopSessions.HasPendingOperation
            || switchService.HasPendingSwitch || statusService.ProviderCapability != UsageProviderCapability.Supported)
        {
            return;
        }

        ProfileInfo? profile = profiles.FirstOrDefault(item => item.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return;
        }

        if (!await RunUsageRefreshAsync(profile)) return;
        lastUsageRefreshAttempts[profile.Name] = DateTimeOffset.UtcNow;
        RefreshStatusIndicators();
    }

    private void BeginAutomaticUsageRefreshIfDue()
    {
        if (automaticUsageRefreshRunning
            || installingUpdate || switching || accountOperation || desktopSessions.HasPendingOperation
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

        if (dueProfiles.Length == 0 || switchService.HasPendingSwitch)
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
                    || installingUpdate || switching || accountOperation || desktopSessions.HasPendingOperation
                    || switchService.HasPendingSwitch
                    || !UsageRefreshPolicy.AllowsAutomaticRefresh(settings, statusService.ProviderCapability))
                {
                    return;
                }

                if (!profiles.Any(current => string.Equals(current.Name, profile.Name, StringComparison.OrdinalIgnoreCase))) continue;
                if (!await RunUsageRefreshAsync(profile)) return;
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
            localizer.Format("ConfirmSwitchPrompt", profileName)
                + (settings.ForceCloseFallback ? " " + localizer["DesktopForceConfirm"] : ""),
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
