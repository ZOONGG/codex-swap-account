using System.Globalization;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using Forms = System.Windows.Forms;

namespace CodexProfileOverlay;

internal sealed class SettingsWindow : Window
{
    private static readonly FontFamily EmojiFont = new("Segoe UI Emoji");
    private readonly OverlaySettings settings;
    private readonly UpdateService updates;
    private readonly CancellationTokenSource updateLifetime = new();
    private CancellationTokenSource? downloadCancellation;
    private bool checkingUpdate, downloadingUpdate;
    private string? updateMessage;
    private double updateProgress;
    private readonly Action<OverlaySettings> save;
    private readonly Action statusChanged;
    private readonly Func<string, Task> refreshUsage;
    private readonly ProfileStatusService statusService;
    private readonly BackupMaintenanceService backupMaintenance;
    private readonly Action addProfile;
    private readonly Action manageProfiles;
    private readonly Action openProfilesFolder;
    private readonly Action openRemovedProfilesFolder;
    private readonly Action openApplicationDataFolder;
    private readonly Action openBackupsFolder;
    private readonly Action openLogsFolder;
    private readonly Action resetPosition;
    private readonly Action resetSettings;
    private readonly Action exitApplication;
    private readonly Localizer localizer;
    private readonly Grid contentHost = new();
    private readonly TextBlock pageTitle = new();
    private readonly TextBlock statusText = new();
    private readonly StackPanel navPanel = new();
    private Border? sidebarPanel;
    private Button? themeButton;
    private Button? githubButton;
    private Button? supportButton;
    private string hotkeyConflictMessage = string.Empty;
    private TextBlock? hotkeyConflictText;
    private IReadOnlyList<ProfileInfo> profiles;
    private SettingsPage page = SettingsPage.General;
    private bool isRebuilding;
    private string? selectedStatusProfile;

    public SettingsWindow(
        OverlaySettings settings,
        IReadOnlyList<ProfileInfo> profiles,
        Localizer localizer,
        ProfileStatusService statusService,
        BackupMaintenanceService backupMaintenance,
        UpdateService updates,
        Action<OverlaySettings> save,
        Action statusChanged,
        Func<string, Task> refreshUsage,
        Action addProfile,
        Action manageProfiles,
        Action openProfilesFolder,
        Action openRemovedProfilesFolder,
        Action openApplicationDataFolder,
        Action openBackupsFolder,
        Action openLogsFolder,
        Action resetPosition,
        Action resetSettings,
        Action exitApplication)
    {
        this.settings = settings;
        this.updates = updates;
        updates.Changed += UpdateChanged;
        this.profiles = profiles;
        this.localizer = localizer;
        this.statusService = statusService;
        this.backupMaintenance = backupMaintenance;
        this.save = save;
        this.statusChanged = statusChanged;
        this.refreshUsage = refreshUsage;
        this.addProfile = addProfile;
        this.manageProfiles = manageProfiles;
        this.openProfilesFolder = openProfilesFolder;
        this.openRemovedProfilesFolder = openRemovedProfilesFolder;
        this.openApplicationDataFolder = openApplicationDataFolder;
        this.openBackupsFolder = openBackupsFolder;
        this.openLogsFolder = openLogsFolder;
        this.resetPosition = resetPosition;
        this.resetSettings = resetSettings;
        this.exitApplication = exitApplication;

        Title = "Codex Swap Account";
        Icon = AppIcons.WindowIcon;
        MinWidth = 900;
        MinHeight = 620;
        Background = Brush("WindowBackgroundBrush");
        Foreground = Brush("StrongTextBrush");
        FontSize = 15;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowCaptionThemeService.Apply(this, settings.Theme);
        ApplySavedGeometry();

        Content = BuildShell();
        localizer.LanguageChanged += Rebuild;
        PreviewMouseDown += OnPreviewMouseDownCommitNumber;
        Rebuild();
        Closing += (_, _) => SaveGeometry();
        Closed += (_, _) =>
        {
            updates.Changed -= UpdateChanged;
            updateLifetime.Cancel();
            downloadCancellation?.Cancel();
            localizer.LanguageChanged -= Rebuild;
            PreviewMouseDown -= OnPreviewMouseDownCommitNumber;
        };
    }

    public void UpdateProfiles(IReadOnlyList<ProfileInfo> newProfiles)
    {
        profiles = newProfiles;
        if (page is SettingsPage.Hotkeys or SettingsPage.Profiles or SettingsPage.Status)
        {
            Rebuild();
        }
    }

    public void SetConflicts(IReadOnlyList<string> conflicts)
    {
        hotkeyConflictMessage = conflicts.Count == 0 ? string.Empty : string.Join(Environment.NewLine, conflicts);
        if (hotkeyConflictText is not null)
        {
            hotkeyConflictText.Text = hotkeyConflictMessage;
        }
    }

    public void RefreshTheme()
    {
        Background = Brush("WindowBackgroundBrush");
        Foreground = Brush("StrongTextBrush");
        WindowCaptionThemeService.Apply(this, settings.Theme);
        if (sidebarPanel is not null)
        {
            sidebarPanel.Background = Brush("Surface1Brush");
            sidebarPanel.BorderBrush = Brush("BorderBrush");
        }

        Rebuild();
    }

    private UIElement BuildShell()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        sidebarPanel = new Border
        {
            Background = Brush("Surface1Brush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(18, 20, 14, 18),
            Child = navPanel,
        };
        root.Children.Add(sidebarPanel);

        var main = new Grid { Margin = new Thickness(30, 24, 30, 22) };
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(main, 1);
        root.Children.Add(main);

        var header = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        pageTitle.FontSize = 28;
        pageTitle.FontWeight = FontWeights.SemiBold;
        pageTitle.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(pageTitle);

        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        githubButton = new Button { MinWidth = 92, Margin = new Thickness(0, 0, 8, 0) };
        githubButton.Click += (_, _) => OpenExternalUrl("https://github.com/ZOONGG/codex-swap-account");
        headerActions.Children.Add(githubButton);
        supportButton = new Button { MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        supportButton.Click += (_, _) => OpenSupport();
        headerActions.Children.Add(supportButton);
        themeButton = CreateThemeButton();
        headerActions.Children.Add(themeButton);
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        main.Children.Add(header);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = contentHost,
        };
        Grid.SetRow(scroll, 1);
        main.Children.Add(scroll);

        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        statusText.Foreground = Brush("SuccessBrush");
        statusText.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(statusText, Dock.Left);
        footer.Children.Add(statusText);

        var close = new Button { Content = localizer["Close"], MinWidth = 120 };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        Grid.SetRow(footer, 2);
        main.Children.Add(footer);
        return root;
    }

    private void Rebuild()
    {
        if (isRebuilding)
        {
            return;
        }

        isRebuilding = true;
        try
        {
            navPanel.Children.Clear();
            navPanel.Children.Add(new TextBlock
            {
                Text = "Codex",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("StrongTextBrush"),
                Margin = new Thickness(4, 0, 0, 22),
            });

            AddNav(SettingsPage.General, localizer["General"]);
            AddNav(SettingsPage.Appearance, localizer["Appearance"]);
            AddNav(SettingsPage.Profiles, localizer["Profiles"]);
            AddNav(SettingsPage.Hotkeys, localizer["Hotkeys"]);
            AddNav(SettingsPage.Language, localizer["Language"]);
            AddNav(SettingsPage.Status, localizer["StatusAndLimits"]);
            AddNav(SettingsPage.Advanced, localizer["Advanced"] + (updates.HasUpdate ? " •" : ""));

            pageTitle.Text = PageName(page);
            pageTitle.Foreground = Brush("StrongTextBrush");
            UpdateHeaderButtons();
            UpdateThemeButton();
            contentHost.Children.Clear();
            contentHost.Children.Add(page switch
            {
                SettingsPage.General => BuildGeneralPage(),
                SettingsPage.Appearance => BuildAppearancePage(),
                SettingsPage.Profiles => BuildProfilesPage(),
                SettingsPage.Hotkeys => BuildHotkeysPage(),
                SettingsPage.Language => BuildLanguagePage(),
                SettingsPage.Status => BuildStatusPage(),
                SettingsPage.Advanced => BuildAdvancedPage(),
                _ => BuildGeneralPage(),
            });
        }
        finally
        {
            isRebuilding = false;
        }
    }

    private void AddNav(SettingsPage target, string label)
    {
        var button = new Button
        {
            Content = label,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8),
            Background = target == page ? Brush("TabActiveBrush") : Brushes.Transparent,
            BorderBrush = target == page ? Brush("AccentBrush") : Brushes.Transparent,
        };
        button.Click += (_, _) =>
        {
            if (page == target)
            {
                return;
            }

            page = target;
            Rebuild();
        };
        navPanel.Children.Add(button);
    }

    private Button CreateThemeButton()
    {
        var button = new Button
        {
            Width = 44,
            Height = 40,
            MinWidth = 44,
            Padding = new Thickness(0),
            Background = Brush("Surface2Brush"),
            BorderBrush = Brush("BorderBrush"),
        };
        button.Click += (_, _) =>
        {
            CommitFocusedNumber();
            settings.Theme = settings.Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
            Save();
        };
        return button;
    }

    private void UpdateHeaderButtons()
    {
        if (githubButton is not null)
        {
            githubButton.Content = "GitHub";
            githubButton.ToolTip = "https://github.com/ZOONGG/codex-swap-account";
        }

        if (supportButton is not null)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Viewbox
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 7, 0),
                Child = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M12,21 L3,12 C-3,5 6,-1 12,6 C18,-1 27,5 21,12 Z"),
                    Fill = Brush("ErrorBrush"),
                    Stretch = Stretch.Uniform,
                },
            });
            content.Children.Add(new TextBlock
            {
                Text = LocalizationCatalog.Resolve(localizer.Language) == LanguagePreference.Russian ? "Поддержать" : "Support",
                VerticalAlignment = VerticalAlignment.Center,
            });
            supportButton.Content = content;
            supportButton.ToolTip = LocalizationCatalog.Resolve(localizer.Language) == LanguagePreference.Russian
                ? "Поддержать развитие Codex Swap Account"
                : "Support Codex Swap Account development";
        }
    }

    private void OpenSupport()
    {
        var support = new SupportWindow(settings.Theme, localizer.Language) { Owner = this };
        _ = support.ShowDialog();
    }

    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private void UpdateThemeButton()
    {
        if (themeButton is null)
        {
            return;
        }

        bool light = settings.Theme == AppTheme.Light;
        themeButton.ToolTip = light ? localizer["SwitchToDarkTheme"] : localizer["SwitchToLightTheme"];
        themeButton.Content = CreateThemeIcon(light);
        themeButton.Background = Brush(light ? "TabActiveBrush" : "Surface2Brush");
        themeButton.BorderBrush = Brush(light ? "AccentBrush" : "BorderBrush");
    }

    private UIElement CreateThemeIcon(bool light)
    {
        var grid = new Grid { Width = 22, Height = 22 };
        if (light)
        {
            grid.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 10,
                Height = 10,
                Stroke = Brush("AccentBrush"),
                StrokeThickness = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            grid.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 11 0 L 11 4 M 11 18 L 11 22 M 0 11 L 4 11 M 18 11 L 22 11 M 3.2 3.2 L 6 6 M 16 16 L 18.8 18.8 M 18.8 3.2 L 16 6 M 6 16 L 3.2 18.8"),
                Stroke = Brush("AccentBrush"),
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.None,
            });
            return grid;
        }

        grid.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 17 15.7 C 12.4 16.8 8.2 13.4 8.2 8.7 C 8.2 6.4 9.3 4.3 11.1 3 C 6.3 3.4 2.8 7.3 2.8 11.9 C 2.8 16.8 6.8 20.8 11.7 20.8 C 14.2 20.8 16.4 19.8 18 18.1 C 17.5 17.4 17.2 16.6 17 15.7 Z"),
            Fill = Brush("AccentBrush"),
            Stretch = Stretch.Uniform,
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return grid;
    }

    private UIElement BuildGeneralPage()
    {
        var stack = PageStack();
        stack.Children.Add(Card(
            SettingCheck(localizer["StartWithWindows"], localizer["StartWithWindowsHelp"], settings.StartWithWindows, value => settings.StartWithWindows = value),
            SettingCheck(localizer["ShowAutomatically"], localizer["ShowAutomaticallyHelp"], settings.ShowAutomaticallyWhenCodexOpens, value => settings.ShowAutomaticallyWhenCodexOpens = value),
            SettingCheck(localizer["LaunchOnStart"], localizer["LaunchOnStartHelp"], settings.LaunchCodexWhenOverlayStarts, value => settings.LaunchCodexWhenOverlayStarts = value),
            SettingCheck(localizer["LaunchAfterSwitch"], localizer["LaunchAfterSwitchHelp"], settings.LaunchCodexAfterSwitching, value => settings.LaunchCodexAfterSwitching = value)));
        return stack;
    }

    private UIElement BuildAppearancePage()
    {
        var stack = PageStack();
        var rows = new List<UIElement>
        {
            EnumCombo(localizer["DisplayMode"], localizer["DisplayModeHelp"], settings.DisplayMode, value => settings.DisplayMode = value),
            EnumCombo(localizer["PositionPreset"], localizer["PositionPresetHelp"], settings.PositionPreset, value => settings.PositionPreset = value, Rebuild),
        };

        if (settings.PositionPreset == PositionPreset.Custom)
        {
            rows.Add(NumberInput(localizer["OffsetX"], localizer["OffsetXHelp"], settings.OffsetX, value => settings.OffsetX = value, 1, 0, 4000));
            rows.Add(NumberInput(localizer["OffsetY"], localizer["OffsetYHelp"], settings.OffsetY, value => settings.OffsetY = value, 1, 0, 4000));
        }

        rows.Add(NumberInput(localizer["UiScale"], localizer["UiScaleHelp"], settings.Scale, value => settings.Scale = value, 0.01, 0.8, 1.4));
        rows.Add(SettingCheck(localizer["Animations"], localizer["AnimationsHelp"], settings.AnimationsEnabled, value => settings.AnimationsEnabled = value));
        stack.Children.Add(Card(rows.ToArray()));
        return stack;
    }

    private UIElement BuildProfilesPage()
    {
        var stack = PageStack();
        stack.Children.Add(Card(CommandGrid(
            (localizer["AddProfile"], "M 12 5 L 12 19 M 5 12 L 19 12", addProfile, true),
            (localizer["ManageProfiles"], "M 7 7 C 7 4.8 8.8 3 11 3 C 13.2 3 15 4.8 15 7 C 15 9.2 13.2 11 11 11 C 8.8 11 7 9.2 7 7 Z M 4 21 C 4.8 16.8 7.3 15 11 15 C 14.7 15 17.2 16.8 18 21 M 18 5 L 21 5 M 19.5 3.5 L 19.5 6.5", manageProfiles, false),
            (localizer["OpenProfilesFolder"], "M 3 6 L 9 6 L 11 8 L 21 8 L 21 18 L 3 18 Z", openProfilesFolder, false),
            (localizer["OpenRemovedProfilesFolder"], "M 4 6 L 20 6 M 9 6 L 10 4 L 14 4 L 15 6 M 7 9 L 8 20 L 16 20 L 17 9 M 10 12 L 10 17 M 14 12 L 14 17", openRemovedProfilesFolder, false))));
        return stack;
    }

    private UIElement BuildHotkeysPage()
    {
        var stack = PageStack();
        var rows = new List<UIElement>
        {
            HotkeyRow(localizer["ToggleSwitcher"], settings.Hotkeys.ToggleOverlay, value => settings.Hotkeys.ToggleOverlay = value),
        };

        while (settings.Hotkeys.ProfileHotkeys.Count < 9)
        {
            settings.Hotkeys.ProfileHotkeys.Add(null);
        }

        for (int index = 0; index < Math.Min(9, profiles.Count); index++)
        {
            int captured = index;
            rows.Add(HotkeyRow(localizer.Format("ProfileHotkey", index + 1, profiles[index].DisplayName), settings.Hotkeys.ProfileHotkeys[index], value => settings.Hotkeys.ProfileHotkeys[captured] = value));
        }

        rows.Add(CommandRow((localizer["ResetHotkeys"], () =>
        {
            settings.Hotkeys = HotkeySettings.CreateDefault();
            Save();
            Rebuild();
        }, false)));

        hotkeyConflictText = new TextBlock
        {
            Text = hotkeyConflictMessage,
            Foreground = Brush("ErrorBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        rows.Add(hotkeyConflictText);
        stack.Children.Add(Card(rows.ToArray()));
        return stack;
    }

    private UIElement BuildLanguagePage()
    {
        var stack = PageStack();
        stack.Children.Add(Card(EnumCombo(localizer["Language"], localizer["LanguageHelp"], settings.Language, value =>
        {
            settings.Language = value;
            Save();
            localizer.SetLanguage(value);
        })));
        return stack;
    }

    private UIElement BuildStatusPage()
    {
        var stack = PageStack();
        bool providerSupported = statusService.ProviderCapability == UsageProviderCapability.Supported;
        var rows = new List<UIElement>
        {
            SettingCheck(
                localizer["ShowAutomaticLimitIndicators"],
                providerSupported ? localizer["ShowAutomaticLimitIndicatorsHelp"] : localizer["AutomaticLimitsUnavailableHelp"],
                settings.ShowAutomaticLimitIndicators && providerSupported,
                value => settings.ShowAutomaticLimitIndicators = value,
                providerSupported),
            SettingCheck(localizer["ShowIndicatorsInOverlay"], localizer["ShowIndicatorsInOverlayHelp"], settings.ShowIndicatorsInOverlay, value => settings.ShowIndicatorsInOverlay = value),
            SettingCheck(localizer["ShowManualEmoji"], localizer["ShowManualEmojiHelp"], settings.ShowManualProfileEmojiInOverlay, value => settings.ShowManualProfileEmojiInOverlay = value),
            NumberInput(localizer["GreenThreshold"], localizer["GreenThresholdHelp"], settings.GreenThresholdPercent, value => settings.GreenThresholdPercent = (int)value, 1, 26, 100),
            NumberInput(localizer["YellowThreshold"], localizer["YellowThresholdHelp"], settings.YellowThresholdPercent, value => settings.YellowThresholdPercent = (int)value, 1, 1, 99),
            NumberInput(localizer["StaleDataThreshold"], localizer["StaleDataThresholdHelp"], settings.StaleDataThresholdMinutes, value => settings.StaleDataThresholdMinutes = (int)value, 1, 10, 1440),
            NumberInput(localizer["LowWarningThreshold"], localizer["LowWarningThresholdHelp"], settings.LowWarningThresholdPercent, value => settings.LowWarningThresholdPercent = (int)value, 1, 5, 50),
            SettingCheck(localizer["WarnNearlyExhausted"], localizer["WarnNearlyExhaustedHelp"], settings.WarnWhenNearlyExhausted, value => settings.WarnWhenNearlyExhausted = value),
            NumberInput(localizer["ActiveProfileRefreshInterval"], localizer["ActiveProfileRefreshIntervalHelp"], settings.ActiveProfileRefreshIntervalMinutes, value => settings.ActiveProfileRefreshIntervalMinutes = (int)value, 1, 10, 120),
            NumberInput(localizer["InactiveProfileRefreshInterval"], localizer["InactiveProfileRefreshIntervalHelp"], settings.InactiveProfileRefreshIntervalMinutes, value => settings.InactiveProfileRefreshIntervalMinutes = (int)value, 1, 10, 1440),
        };

        stack.Children.Add(Card(rows.ToArray()));
        stack.Children.Add(BuildManualStatusEditor(providerSupported));

        var legend = new StackPanel();
        legend.Children.Add(new TextBlock
        {
            Text = localizer["IndicatorLegend"],
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("StrongTextBrush"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        legend.Children.Add(LegendRow("⭐", localizer["RecommendedProfile"], localizer["RecommendedProfileHelp"]));
        legend.Children.Add(LegendRow("🟢", localizer["HighCapacity"], localizer["HighCapacityHelp"]));
        legend.Children.Add(LegendRow("🟡", localizer["MediumCapacity"], localizer["MediumCapacityHelp"]));
        legend.Children.Add(LegendRow("🔴", localizer["LowCapacity"], localizer["LowCapacityHelp"]));

        legend.Children.Add(new TextBlock
        {
            Text = localizer["AutomaticIndicatorExplanation"],
            FontSize = 13,
            Foreground = Brush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new Border
        {
            Child = legend,
            Background = Brush("Surface2Brush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 18, 0, 0),
        });
        return stack;
    }

    private UIElement LegendRow(string emoji, string title, string description)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FrameworkElement icon = CreateIndicatorVisual(emoji, 16);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(icon);

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("StrongTextBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 13,
            Foreground = Brush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private UIElement BuildManualStatusEditor(bool providerSupported)
    {
        if (profiles.Count == 0)
        {
            return Card(new TextBlock { Text = localizer["NoReadyProfiles"], Foreground = Brush("MutedTextBrush") });
        }

        ProfileInfo selected = profiles.FirstOrDefault(profile => profile.Name.Equals(selectedStatusProfile, StringComparison.OrdinalIgnoreCase)) ?? profiles[0];
        selectedStatusProfile = selected.Name;
        ProfileStatusDocument document = statusService.Load();
        ProfileStatusMetadata metadata = statusService.GetOrCreateStatus(document, selected.Name);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = localizer["ManualProfileStatus"],
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("StrongTextBrush"),
            Margin = new Thickness(0, 0, 0, 12),
        });

        var selector = new ComboBox
        {
            ItemsSource = profiles,
            DisplayMemberPath = nameof(ProfileInfo.DisplayName),
            SelectedItem = selected,
            MinWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 14),
        };
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is ProfileInfo profile && !profile.Name.Equals(selectedStatusProfile, StringComparison.OrdinalIgnoreCase))
            {
                selectedStatusProfile = profile.Name;
                Rebuild();
            }
        };
        panel.Children.Add(selector);

        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach ((string Emoji, string Key) preset in new[]
        {
            ("🟢", "PresetReady"), ("🟡", "PresetPartlyUsed"), ("🟠", "PresetLow"),
            ("🔴", "PresetExhausted"), ("⏳", "PresetResetsSoon"), ("💤", "PresetInactive"), ("⚪", "PresetUnknown"),
        })
        {
            var button = new Button { Content = EmojiLabel(preset.Emoji, localizer[preset.Key]), Margin = new Thickness(0, 0, 8, 8) };
            button.Click += (_, _) =>
            {
                SaveManual(selected.Name, preset.Emoji, localizer[preset.Key], metadata.ManualNote, metadata.ManualResetAt, metadata.ManualColor);
                Rebuild();
            };
            presets.Children.Add(button);
        }
        panel.Children.Add(presets);

        var emoji = TextSetting(localizer["ManualEmoji"], metadata.ManualEmoji, 8, value =>
            SaveManual(selected.Name, value, metadata.ManualLabel, metadata.ManualNote, metadata.ManualResetAt, metadata.ManualColor));
        var label = TextSetting(localizer["ManualLabel"], metadata.ManualLabel, ProfileStatusService.MaximumLabelLength, value =>
            SaveManual(selected.Name, metadata.ManualEmoji, value, metadata.ManualNote, metadata.ManualResetAt, metadata.ManualColor));
        var note = TextSetting(localizer["ManualNote"], metadata.ManualNote, ProfileStatusService.MaximumNoteLength, value =>
            SaveManual(selected.Name, metadata.ManualEmoji, metadata.ManualLabel, value, metadata.ManualResetAt, metadata.ManualColor));
        var reset = TextSetting(localizer["ManualResetAt"], metadata.ManualResetAt is null ? null : UsageDisplayFormatter.FormatLocal(metadata.ManualResetAt.Value), 40, value =>
        {
            DateTimeOffset? parsed = null;
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (!DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset date))
                {
                    throw new FormatException(localizer["InvalidDateTime"]);
                }

                parsed = date;
            }
            SaveManual(selected.Name, metadata.ManualEmoji, metadata.ManualLabel, metadata.ManualNote, parsed, metadata.ManualColor);
        });
        panel.Children.Add(SettingRow(localizer["ManualEmoji"], localizer["ManualEmojiHelp"], emoji));
        panel.Children.Add(SettingRow(localizer["ManualLabel"], localizer["ManualLabelHelp"], label));
        panel.Children.Add(SettingRow(localizer["ManualNote"], localizer["ManualNoteHelp"], note));
        panel.Children.Add(SettingRow(localizer["ManualResetAt"], localizer["ManualResetAtHelp"], reset));
        panel.Children.Add(SettingCheck(
            localizer["CheckThisProfileAutomatically"],
            localizer["CheckThisProfileAutomaticallyHelp"],
            metadata.AutomaticRefreshEnabled,
            value => { statusService.SetAutomaticRefreshEnabled(selected.Name, value); statusChanged(); },
            providerSupported));

        var actions = new WrapPanel();
        var testProvider = new Button { Content = localizer["TestProviderSupport"], IsEnabled = providerSupported, Margin = new Thickness(0, 0, 8, 0) };
        testProvider.Click += async (_, _) =>
        {
            testProvider.IsEnabled = false;
            try
            {
                bool supported = await statusService.TestProviderSupportAsync(selected.DirectoryPath, CancellationToken.None);
                if (supported)
                {
                    settings.ShowAutomaticLimitIndicators = true;
                    Save();
                    await refreshUsage(selected.Name);
                    statusText.Foreground = Brush("SuccessBrush");
                    statusText.Text = localizer["ProviderAvailable"];
                    Rebuild();
                }
                else
                {
                    statusText.Foreground = Brush("ErrorBrush");
                    statusText.Text = localizer["AutomaticLimitsUnavailable"];
                }
            }
            finally
            {
                testProvider.IsEnabled = providerSupported;
            }
        };
        actions.Children.Add(testProvider);
        var refresh = new Button { Content = localizer["RefreshNow"], IsEnabled = providerSupported && settings.ShowAutomaticLimitIndicators, Margin = new Thickness(0, 0, 8, 0) };
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            try
            {
                await refreshUsage(selected.Name);
                Rebuild();
            }
            finally
            {
                refresh.IsEnabled = providerSupported && settings.ShowAutomaticLimitIndicators;
            }
        };
        actions.Children.Add(refresh);
        var clearManual = new Button { Content = localizer["ClearManualStatus"], Margin = new Thickness(0, 0, 8, 0) };
        clearManual.Click += (_, _) => { statusService.ClearManualStatus(selected.Name); statusChanged(); Rebuild(); };
        actions.Children.Add(clearManual);
        var clearCached = new Button { Content = localizer["ClearCachedUsage"] };
        clearCached.Click += (_, _) => { statusService.ClearCachedUsage(selected.Name); statusChanged(); Rebuild(); };
        actions.Children.Add(clearCached);
        panel.Children.Add(actions);

        panel.Children.Add(new TextBlock
        {
            Text = BuildUsageDetails(selected.Name, document, providerSupported),
            Foreground = Brush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
        });
        return Card(panel);
    }

    private static UIElement EmojiLabel(string emoji, string label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        FrameworkElement icon = CreateIndicatorVisual(emoji, 15);
        icon.Margin = new Thickness(0, 0, 6, 0);
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    private static FrameworkElement CreateIndicatorVisual(string indicator, double size)
    {
        if (indicator == "⭐")
        {
            return new Viewbox
            {
                Width = size,
                Height = size,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M 12 2 L 14.9 8.7 L 22 9.3 L 16.6 13.9 L 18.2 21 L 12 17.3 L 5.8 21 L 7.4 13.9 L 2 9.3 L 9.1 8.7 Z"),
                    Fill = new SolidColorBrush(Color.FromRgb(255, 213, 74)),
                    Stroke = new SolidColorBrush(Color.FromRgb(245, 178, 18)),
                    StrokeThickness = 1.2,
                    StrokeLineJoin = PenLineJoin.Round,
                },
            };
        }

        Color? color = indicator switch
        {
            "🟢" => Color.FromRgb(34, 197, 94),
            "🟡" => Color.FromRgb(250, 204, 21),
            "🟠" => Color.FromRgb(249, 115, 22),
            "🔴" => Color.FromRgb(239, 68, 68),
            "⚪" => Color.FromRgb(229, 231, 235),
            _ => null,
        };

        if (color is not null)
        {
            return new System.Windows.Shapes.Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(color.Value),
                Stroke = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                StrokeThickness = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
            };
        }

        return new TextBlock
        {
            Text = indicator,
            FontFamily = EmojiFont,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private TextBox TextSetting(string name, string? value, int maximumLength, Action<string?> commit)
    {
        var box = new TextBox { Text = value ?? string.Empty, MaxLength = maximumLength, MinWidth = 260 };
        void SaveValue()
        {
            try
            {
                commit(box.Text);
                statusText.Foreground = Brush("SuccessBrush");
                statusText.Text = localizer["Applied"];
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                statusText.Foreground = Brush("ErrorBrush");
                statusText.Text = exception.Message;
            }
        }
        box.LostFocus += (_, _) => SaveValue();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                SaveValue();
                Keyboard.ClearFocus();
            }
        };
        return box;
    }

    private void SaveManual(string profileId, string? emoji, string? label, string? note, DateTimeOffset? resetAt, string? color)
    {
        statusService.SaveManualStatus(profileId, emoji, label, note, resetAt, color);
        statusChanged();
    }

    private string BuildUsageDetails(string profileId, ProfileStatusDocument document, bool providerSupported)
    {
        var lines = new List<string>
        {
            localizer[providerSupported ? "ProviderAvailable" : "AutomaticLimitsUnavailable"],
        };
        if (!document.Snapshots.TryGetValue(profileId, out UsageSnapshot? snapshot))
        {
            lines.Add(localizer["NoReliableUsageData"]);
            return string.Join(Environment.NewLine, lines);
        }

        foreach (UsageLimitWindow window in UsageIntelligence.GetKnownWindows(snapshot))
        {
            string reset = window.ResetAt is null ? string.Empty : $" · {localizer["ResetsAt"]} {UsageDisplayFormatter.FormatLocal(window.ResetAt.Value)}";
            lines.Add($"{window.Name}: {window.RemainingPercent}%{reset}");
        }
        lines.Add($"{localizer["LastUpdated"]}: {UsageDisplayFormatter.FormatLocal(snapshot.CapturedAt)}");
        if (!string.IsNullOrWhiteSpace(snapshot.Source))
        {
            lines.Add($"{localizer["UsageSource"]}: {snapshot.Source}");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.CodexCliVersion))
        {
            lines.Add($"{localizer["CodexCliVersion"]}: {snapshot.CodexCliVersion}");
        }

        if (UsageIntelligence.IsStale(snapshot, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(settings.StaleDataThresholdMinutes)))
        {
            lines.Add(localizer["UsageDataStale"]);
        }

        ProfileStatusMetadata? metadata = document.Profiles
            .FirstOrDefault(status => profileId.Equals(status.ProfileId, StringComparison.OrdinalIgnoreCase));
        if (metadata?.LastRefreshAttemptAt is not null)
        {
            lines.Add($"{localizer["LastRefreshAttempt"]}: {UsageDisplayFormatter.FormatLocal(metadata.LastRefreshAttemptAt.Value)}");
        }

        if (metadata?.LastAutomaticSnapshot is not null)
        {
            lines.Add($"{localizer["LastSuccessfulRefresh"]}: {UsageDisplayFormatter.FormatLocal(metadata.LastAutomaticSnapshot.Value)}");
        }

        if (!string.IsNullOrWhiteSpace(metadata?.LastRefreshError))
        {
            lines.Add($"{localizer["LastSafeError"]}: {metadata.LastRefreshError}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private UIElement BuildAdvancedPage()
    {
        var stack = PageStack();
        stack.Children.Add(BuildUpdateCard());
        BackupStorageSummary storage = backupMaintenance.GetStorageSummary();
        stack.Children.Add(Card(new TextBlock
        {
            Text = string.Format(
                CultureInfo.CurrentCulture,
                localizer["BackupStorageSummary"],
                FormatBytes(storage.TotalBytes),
                storage.CompletedBackupCount,
                storage.RetentionCountLimit,
                FormatBytes(storage.RetentionStorageLimitBytes)),
            Foreground = Brush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
        }));
        stack.Children.Add(Card(
            NumberInput(localizer["GracefulTimeout"], localizer["GracefulTimeoutHelp"], settings.GracefulCloseTimeoutSeconds, value => settings.GracefulCloseTimeoutSeconds = (int)value, 1, 1, 60),
            SettingCheck(localizer["ForceCloseFallback"], localizer["ForceCloseFallbackHelp"], settings.ForceCloseFallback, value => settings.ForceCloseFallback = value),
            SettingCheck(localizer["ConfirmForceClose"], localizer["ConfirmForceCloseHelp"], settings.ConfirmBeforeForceClose, value => settings.ConfirmBeforeForceClose = value)));
        stack.Children.Add(Card(CommandGrid(
            (localizer["OpenApplicationData"], "M 3 6 L 9 6 L 11 8 L 21 8 L 21 18 L 3 18 Z", openApplicationDataFolder, false),
            (localizer["OpenBackups"], "M 4 6 L 20 6 L 20 18 L 4 18 Z M 8 10 L 16 10 M 8 14 L 13 14", openBackupsFolder, false),
            (localizer["CleanLegacyBackups"], "M 5 7 L 19 7 M 9 7 L 9 5 L 15 5 L 15 7 M 7 7 L 8 20 L 16 20 L 17 7", CleanLegacyBackups, false),
            (localizer["CleanCompletedBackups"], "M 5 7 L 19 7 M 8 11 L 16 11 M 8 15 L 14 15", CleanCompletedBackups, false),
            (localizer["OpenLogs"], "M 6 3 L 16 3 L 20 7 L 20 21 L 6 21 Z M 15 3 L 15 8 L 20 8 M 9 12 L 17 12 M 9 16 L 17 16", openLogsFolder, false),
            (localizer["ResetPosition"], "M 12 4 L 12 20 M 4 12 L 20 12 M 7 7 L 4 12 L 7 17 M 17 7 L 20 12 L 17 17", resetPosition, false),
            (localizer["ResetSettings"], "M 6 8 C 7.5 5.5 10.2 4 13 4 C 17.4 4 21 7.6 21 12 C 21 16.4 17.4 20 13 20 C 9.9 20 7.2 18.2 5.9 15.6 M 6 8 L 6 4 M 6 8 L 10 8", resetSettings, false),
            (localizer["ExportSettings"], "M 12 4 L 12 15 M 8 11 L 12 15 L 16 11 M 5 19 L 19 19", ExportSettings, false),
            (localizer["ImportSettings"], "M 12 15 L 12 4 M 8 8 L 12 4 L 16 8 M 5 19 L 19 19", ImportSettings, false),
            (localizer["ExitApplication"], "M 10 5 L 5 5 L 5 19 L 10 19 M 13 8 L 17 12 L 13 16 M 8 12 L 17 12", exitApplication, false))));
        return stack;
    }

    private void UpdateChanged()
    {
        if (!updateLifetime.IsCancellationRequested)
            _ = Dispatcher.InvokeAsync(() => { if (!updateLifetime.IsCancellationRequested) Rebuild(); });
    }

    private UIElement BuildUpdateCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = localizer["Updates"], FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        string state = checkingUpdate ? "UpdateChecking" : updates.ErrorKey ?? (updates.Cache.LastSuccess is null ? "UpdateNotChecked" : updates.HasUpdate ? "UpdateAvailable" : "UpdateCurrent");
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        if (state == "UpdateCurrent") heading.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 2 8 L 6 12 L 14 3"), Stroke = Brush("SuccessBrush"), StrokeThickness = 2,
            Width = 18, Height = 18, Margin = new Thickness(1, 2, 8, 2),
        });
        heading.Children.Add(new TextBlock { Text = localizer[state], TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(heading);
        panel.Children.Add(new TextBlock { Text = "v" + updates.CurrentVersion + (updates.HasUpdate ? " → " + updates.Cache.Release!.Tag : ""), Foreground = Brush("MutedTextBrush"), Margin = new Thickness(0, 6, 0, 8) });
        if (updates.HasUpdate && updates.Cache.Release is { } release)
        {
            panel.Children.Add(new TextBlock { Text = release.Published.ToLocalTime().ToString("d", CultureInfo.CurrentCulture), Foreground = Brush("MutedTextBrush") });
            panel.Children.Add(new ScrollViewer { MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 8), Content = new TextBlock { Text = release.Notes.Length > 2000 ? release.Notes[..2000] + "…" : release.Notes, TextWrapping = TextWrapping.Wrap } });
        }
        var actions = new WrapPanel();
        Button ActionButton(string key, RoutedEventHandler action)
        {
            var button = new Button { Content = localizer[key], Margin = new Thickness(0, 4, 8, 4) };
            button.Click += action;
            actions.Children.Add(button);
            return button;
        }
        ActionButton("UpdateCheck", async (_, _) =>
        {
            checkingUpdate = true; updateMessage = null; Rebuild();
            try { await updates.CheckAsync(true, updateLifetime.Token); }
            catch (OperationCanceledException) { }
            finally { checkingUpdate = false; if (!updateLifetime.IsCancellationRequested) Rebuild(); }
        }).IsEnabled = !checkingUpdate && !downloadingUpdate;
        if (updates.HasUpdate)
        {
            ActionButton("UpdateDownload", async (_, _) => await DownloadUpdateAsync()).IsEnabled = !downloadingUpdate && !checkingUpdate;
            ActionButton("UpdateGitHub", (_, _) => OpenExternalUrl(updates.Cache.Release!.Url));
        }
        if (downloadingUpdate) ActionButton("UpdateCancel", (_, _) => downloadCancellation?.Cancel());
        panel.Children.Add(actions);
        if (downloadingUpdate) panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = updateProgress, Height = 6, Margin = new Thickness(0, 8, 0, 8) });
        if (updateMessage is not null) panel.Children.Add(new TextBlock { Text = localizer[updateMessage], TextWrapping = TextWrapping.Wrap, Foreground = Brush("MutedTextBrush"), Margin = new Thickness(0, 8, 0, 0) });
        return Card(panel);
    }

    private async Task DownloadUpdateAsync()
    {
        downloadingUpdate = true; updateProgress = 0; updateMessage = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
        downloadCancellation = cancellation;
        Rebuild();
        string? temp = null;
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (updateLifetime.IsCancellationRequested) return;
                updateProgress = value;
                // Update the existing bar without rebuilding controls or moving keyboard focus.
                if (page == SettingsPage.Advanced) UpdateProgressBar(contentHost, value);
            });
            temp = await updates.DownloadAsync(progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = UpdateService.ZipName, Filter = "ZIP (*.zip)|*.zip", DefaultExt = ".zip" };
            if (dialog.ShowDialog(this) == true)
            {
                string destination = dialog.FileName;
                await Task.Run(() => File.Copy(temp, destination, true), cancellation.Token);
                updateMessage = "UpdateSaved";
            }
        }
        catch (OperationCanceledException) { updateMessage = "UpdateCancelled"; }
        catch (Exception e) when (e is InvalidDataException or IOException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException) { updateMessage = "UpdateDownloadFailed"; }
        finally
        {
            if (temp is not null) { try { File.Delete(temp); } catch (IOException) { } }
            downloadCancellation = null; downloadingUpdate = false;
            if (!updateLifetime.IsCancellationRequested) Rebuild();
        }
    }

    private static void UpdateProgressBar(DependencyObject parent, double value)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ProgressBar bar) bar.Value = value;
            else UpdateProgressBar(child, value);
        }
    }

    private void CleanLegacyBackups()
    {
        LegacyBackupSummary summary = backupMaintenance.InspectLegacyBackups();
        if (summary.DirectoryCount == 0)
        {
            statusText.Text = localizer["NoLegacyBackups"];
            return;
        }

        string message = string.Format(
            CultureInfo.CurrentCulture,
            localizer["LegacyBackupCleanupPrompt"],
            summary.DirectoryCount,
            FormatBytes(summary.TotalBytes),
            FormatTimestamp(summary.OldestTimestamp),
            FormatTimestamp(summary.NewestTimestamp),
            FormatBytes(summary.EstimatedReclaimBytes));
        if (!ConfirmDialog.Show(this, IntPtr.Zero, localizer["CleanLegacyBackups"], message, localizer["Clean"], localizer["Cancel"], danger: true))
        {
            return;
        }

        backupMaintenance.CleanLegacyBackups();
        statusText.Text = localizer["BackupCleanupCompleted"];
        Rebuild();
    }

    private void CleanCompletedBackups()
    {
        if (!ConfirmDialog.Show(
            this,
            IntPtr.Zero,
            localizer["CleanCompletedBackups"],
            localizer["CompletedBackupCleanupPrompt"],
            localizer["Clean"],
            localizer["Cancel"],
            danger: true))
        {
            return;
        }

        backupMaintenance.CleanAllCompleted();
        statusText.Text = localizer["BackupCleanupCompleted"];
        Rebuild();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp)
        => timestamp?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";

    private UIElement SettingCheck(string title, string subtitle, bool value, Action<bool> setter, bool enabled = true)
    {
        var check = new CheckBox { IsChecked = value, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = enabled };
        check.Checked += (_, _) => { setter(true); Save(); };
        check.Unchecked += (_, _) => { setter(false); Save(); };
        return SettingRow(title, subtitle, check);
    }

    private UIElement EnumCombo<T>(string title, string subtitle, T value, Action<T> setter, Action? afterSave = null)
        where T : struct, Enum
    {
        var options = Enum.GetValues<T>().Select(item => new Option<T>(DisplayEnum(item), item)).ToArray();
        var combo = new ComboBox
        {
            ItemsSource = options,
            DisplayMemberPath = nameof(Option<T>.Label),
            SelectedValuePath = nameof(Option<T>.Value),
            SelectedValue = value,
            MinWidth = 220,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedValue is T selected)
            {
                setter(selected);
                Save();
                afterSave?.Invoke();
            }
        };
        return SettingRow(title, subtitle, combo);
    }

    private UIElement NumberInput(string title, string subtitle, double value, Action<double> setter, double dragStep = 1, double minimum = double.NegativeInfinity, double maximum = double.PositiveInfinity)
    {
        double currentValue = SanitizeNumber(value, minimum, maximum);
        var box = new TextBox
        {
            Text = currentValue.ToString("0.##", CultureInfo.InvariantCulture),
            MinWidth = 150,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.SizeWE,
        };
        void Commit() => CommitNumber(box, currentValue, committed =>
        {
            currentValue = committed;
            setter(committed);
        }, minimum, maximum);

        box.Tag = (Action)Commit;
        Point dragStart = default;
        double dragStartValue = currentValue;
        bool dragging = false;
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        };
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            dragStart = e.GetPosition(this);
            dragStartValue = TryParseFinite(box.Text, out double parsed) ? parsed : currentValue;
            dragging = false;
            box.CaptureMouse();
        };
        box.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Point current = e.GetPosition(this);
            double delta = current.X - dragStart.X;
            if (!dragging && Math.Abs(delta) < 4)
            {
                return;
            }

            dragging = true;
            double next = SanitizeNumber(dragStartValue + (delta * dragStep), minimum, maximum);
            currentValue = next;
            setter(next);
            box.Text = (dragStep < 1 ? next : Math.Round(next)).ToString("0.##", CultureInfo.InvariantCulture);
            box.CaretIndex = box.Text.Length;
            e.Handled = true;
        };
        box.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (box.IsMouseCaptured)
            {
                box.ReleaseMouseCapture();
            }

            if (!dragging)
            {
                return;
            }

            dragging = false;
            Commit();
            e.Handled = true;
        };
        return SettingRow(title, subtitle, box);
    }

    private UIElement HotkeyRow(string title, HotkeyGesture? value, Action<HotkeyGesture?> setter)
    {
        var button = new Button { Content = value?.ToString() ?? localizer["None"], MinWidth = 190 };
        bool recording = false;
        button.Click += (_, _) =>
        {
            recording = true;
            button.Content = localizer["PressShortcut"];
            button.Focus();
        };
        button.PreviewKeyDown += (_, e) =>
        {
            if (!recording)
            {
                return;
            }

            e.Handled = true;
            if (e.Key is Key.Escape)
            {
                button.Content = value?.ToString() ?? localizer["None"];
                recording = false;
                return;
            }

            if (e.Key is Key.Back or Key.Delete)
            {
                setter(null);
                button.Content = localizer["None"];
                recording = false;
                Save();
                return;
            }

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                return;
            }

            var modifiers = HotkeyModifiers.None;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                modifiers |= HotkeyModifiers.Control;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            {
                modifiers |= HotkeyModifiers.Alt;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                modifiers |= HotkeyModifiers.Shift;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0)
            {
                modifiers |= HotkeyModifiers.Windows;
            }

            if (modifiers == HotkeyModifiers.None)
            {
                hotkeyConflictMessage = localizer["UseModifier"];
                if (hotkeyConflictText is not null)
                {
                    hotkeyConflictText.Text = hotkeyConflictMessage;
                }

                recording = false;
                button.Content = value?.ToString() ?? localizer["None"];
                return;
            }

            var gesture = new HotkeyGesture(modifiers, KeyInterop.VirtualKeyFromKey(key));
            setter(gesture);
            button.Content = gesture.ToString();
            recording = false;
            Save();
        };
        return SettingRow(title, localizer["HotkeyHelp"], button);
    }

    private void OnPreviewMouseDownCommitNumber(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox box || box.Tag is not Action)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject target && IsSelfOrDescendant(box, target))
        {
            return;
        }

        CommitFocusedNumber();
    }

    private static void CommitFocusedNumber()
    {
        if (Keyboard.FocusedElement is TextBox { Tag: Action commit })
        {
            commit();
        }
    }

    private static bool IsSelfOrDescendant(DependencyObject parent, DependencyObject child)
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            if (ReferenceEquals(current, parent))
            {
                return true;
            }

            current = GetParent(current);
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is FrameworkElement element && element.Parent is not null)
        {
            return element.Parent;
        }

        if (current is FrameworkContentElement contentElement && contentElement.Parent is not null)
        {
            return contentElement.Parent;
        }

        return current is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(current)
            : null;
    }

    private void CommitNumber(TextBox box, double previousValue, Action<double> setter, double minimum, double maximum)
    {
        if (!TryParseFinite(box.Text, out double parsed))
        {
            box.Text = SanitizeNumber(previousValue, minimum, maximum).ToString("0.##", CultureInfo.InvariantCulture);
            statusText.Foreground = Brush("ErrorBrush");
            statusText.Text = localizer["InvalidNumber"];
            return;
        }

        parsed = SanitizeNumber(parsed, minimum, maximum);
        box.Text = parsed.ToString("0.##", CultureInfo.InvariantCulture);
        setter(parsed);
        Save();
    }

    private UIElement SettingRow(string title, string subtitle, Control control)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brush("StrongTextBrush") });
        text.Children.Add(new TextBlock { Text = subtitle, FontSize = 13.5, Foreground = Brush("MutedTextBrush"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(text);

        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private UIElement CommandRow(params (string Label, Action Action, bool Primary)[] actions)
    {
        var panel = new WrapPanel();
        foreach ((string label, Action action, bool primary) in actions)
        {
            var button = new Button
            {
                Content = label,
                Margin = new Thickness(0, 0, 10, 10),
                MinWidth = 132,
            };
            if (primary)
            {
                button.Style = (Style)FindResource("PrimaryButtonStyle");
            }

            button.Click += (_, _) => action();
            panel.Children.Add(button);
        }

        return panel;
    }

    private UIElement CommandGrid(params (string Label, string Icon, Action Action, bool Primary)[] actions)
    {
        var panel = new UniformGrid
        {
            Columns = 2,
            Margin = new Thickness(0, -4, 0, -4),
        };

        foreach ((string label, string icon, Action action, bool primary) in actions)
        {
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconHost = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(7),
                Background = primary ? Brush("AccentHoverBrush") : Brush("TabActiveBrush"),
                Margin = new Thickness(0, 0, 12, 0),
                Child = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(icon),
                    Stroke = primary ? Brushes.White : Brush("AccentBrush"),
                    StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Stretch = Stretch.Uniform,
                    Width = 20,
                    Height = 20,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            content.Children.Add(iconHost);

            var text = new TextBlock
            {
                Text = label,
                Foreground = primary ? Brushes.White : Brush("StrongTextBrush"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14.5,
            };
            Grid.SetColumn(text, 1);
            content.Children.Add(text);

            var button = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 10, 14, 10),
                MinHeight = 58,
                Margin = new Thickness(0, 4, 10, 6),
            };
            if (primary)
            {
                button.Style = (Style)FindResource("PrimaryButtonStyle");
            }

            button.Click += (_, _) => action();
            panel.Children.Add(button);
        }

        return panel;
    }

    private StackPanel PageStack() => new() { Margin = new Thickness(0, 0, 12, 0) };

    private Border Card(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (UIElement child in children)
        {
            panel.Children.Add(child);
        }

        return new Border
        {
            Background = Brush("Surface1Brush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(22),
            Margin = new Thickness(0, 0, 0, 16),
            Child = panel,
        };
    }

    private string PageName(SettingsPage value) => value switch
    {
        SettingsPage.General => localizer["General"],
        SettingsPage.Appearance => localizer["Appearance"],
        SettingsPage.Profiles => localizer["Profiles"],
        SettingsPage.Hotkeys => localizer["Hotkeys"],
        SettingsPage.Language => localizer["Language"],
        SettingsPage.Status => localizer["StatusAndLimits"],
        SettingsPage.Advanced => localizer["Advanced"],
        _ => localizer["Settings"],
    };

    private string DisplayEnum<T>(T value)
        where T : struct, Enum
    {
        return value switch
        {
            LanguagePreference.SystemDefault => localizer["SystemDefault"],
            LanguagePreference.English => localizer["English"],
            LanguagePreference.Russian => localizer["Russian"],
            PositionPreset.AfterMenu => localizer["AfterMenu"],
            PositionPreset.TopCenter => localizer["TopCenter"],
            PositionPreset.TopRight => localizer["TopRight"],
            PositionPreset.TopLeft => localizer["TopLeft"],
            PositionPreset.Custom => localizer["Custom"],
            OverlayDisplayMode.Auto => localizer["Auto"],
            OverlayDisplayMode.Compact => localizer["Compact"],
            OverlayDisplayMode.Expanded => localizer["Expanded"],
            _ => value.ToString(),
        };
    }

    private void ApplySavedGeometry()
    {
        Width = SanitizeNumber(settings.SettingsWindowWidth, 900, 1800, 1000);
        Height = SanitizeNumber(settings.SettingsWindowHeight, 620, 1400, 720);
        if (IsGeometryVisible(settings.SettingsWindowLeft, settings.SettingsWindowTop, Width, Height))
        {
            Left = settings.SettingsWindowLeft;
            Top = settings.SettingsWindowTop;
        }
        else
        {
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
            Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
        }
    }

    private static bool IsGeometryVisible(double left, double top, double width, double height)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            return false;
        }

        if (left == -1 && top == -1)
        {
            return false;
        }

        var windowRect = new System.Drawing.Rectangle(
            ToInt32Coordinate(left),
            ToInt32Coordinate(top),
            Math.Max(1, ToInt32Coordinate(width)),
            Math.Max(1, ToInt32Coordinate(height)));
        return Forms.Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(windowRect));
    }

    private void SaveGeometry()
    {
        if (WindowState == WindowState.Normal)
        {
            settings.SettingsWindowLeft = double.IsFinite(Left) ? Left : -1;
            settings.SettingsWindowTop = double.IsFinite(Top) ? Top : -1;
            settings.SettingsWindowWidth = SanitizeNumber(Width, 900, 1800, 1000);
            settings.SettingsWindowHeight = SanitizeNumber(Height, 620, 1400, 720);
            Save();
        }
    }

    private void Save()
    {
        try
        {
            statusText.Foreground = Brush("SuccessBrush");
            statusText.Text = localizer["Applied"];
            save(settings);
        }
        catch (Exception)
        {
            statusText.Foreground = Brush("ErrorBrush");
            statusText.Text = localizer["CouldNotSaveSettings"];
        }
    }

    private void ExportSettings()
    {
        Microsoft.Win32.SaveFileDialog dialog = new() { Filter = "JSON files (*.json)|*.json", FileName = "codex-profile-overlay-settings.json" };
        if (dialog.ShowDialog(this) == true)
        {
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexProfileOverlay", "settings.json"), dialog.FileName, overwrite: true);
        }
    }

    private void ImportSettings()
    {
        Microsoft.Win32.OpenFileDialog dialog = new() { Filter = "JSON files (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true)
        {
            string target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexProfileOverlay", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(dialog.FileName, target, overwrite: true);
            Close();
        }
    }

    private SolidColorBrush Brush(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Clone();

    private static bool TryParseFinite(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    private static double SanitizeNumber(double value, double minimum, double maximum, double fallback = 0)
    {
        if (!double.IsFinite(value))
        {
            value = fallback;
        }

        return Math.Clamp(value, minimum, maximum);
    }

    private static int ToInt32Coordinate(double value)
    {
        return (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue);
    }

    private enum SettingsPage
    {
        General,
        Appearance,
        Profiles,
        Hotkeys,
        Language,
        Status,
        Advanced,
    }

    private sealed record Option<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }
}
