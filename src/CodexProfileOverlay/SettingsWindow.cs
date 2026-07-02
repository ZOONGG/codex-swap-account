using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodexProfileOverlay.Core.Models;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using Forms = System.Windows.Forms;

namespace CodexProfileOverlay;

internal sealed class SettingsWindow : Window
{
    private readonly OverlaySettings settings;
    private readonly Action<OverlaySettings> save;
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
    private readonly TextBlock conflictText = new();
    private readonly StackPanel navPanel = new();
    private IReadOnlyList<ProfileInfo> profiles;
    private SettingsPage page = SettingsPage.General;
    private bool isRebuilding;

    public SettingsWindow(
        OverlaySettings settings,
        IReadOnlyList<ProfileInfo> profiles,
        Localizer localizer,
        Action<OverlaySettings> save,
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
        this.profiles = profiles;
        this.localizer = localizer;
        this.save = save;
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

        Title = "Codex Profile Overlay";
        MinWidth = 900;
        MinHeight = 620;
        Background = Brush("WindowBackgroundBrush");
        Foreground = Brush("StrongTextBrush");
        FontSize = 15;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ApplySavedGeometry();

        Content = BuildShell();
        localizer.LanguageChanged += Rebuild;
        Rebuild();
        Closing += (_, _) => SaveGeometry();
        Closed += (_, _) => localizer.LanguageChanged -= Rebuild;
    }

    public void UpdateProfiles(IReadOnlyList<ProfileInfo> newProfiles)
    {
        profiles = newProfiles;
        if (page == SettingsPage.Hotkeys || page == SettingsPage.Profiles)
        {
            Rebuild();
        }
    }

    public void SetConflicts(IReadOnlyList<string> conflicts)
    {
        conflictText.Text = conflicts.Count == 0 ? string.Empty : string.Join(Environment.NewLine, conflicts);
    }

    private UIElement BuildShell()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = new Border
        {
            Background = Brush("Surface1Brush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(18, 20, 14, 18),
            Child = navPanel,
        };
        root.Children.Add(sidebar);

        var main = new Grid { Margin = new Thickness(30, 24, 30, 22) };
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(main, 1);
        root.Children.Add(main);

        pageTitle.FontSize = 28;
        pageTitle.FontWeight = FontWeights.SemiBold;
        pageTitle.Margin = new Thickness(0, 0, 0, 20);
        main.Children.Add(pageTitle);

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
                Margin = new Thickness(4, 0, 0, 22),
            });

            AddNav(SettingsPage.General, localizer["General"]);
            AddNav(SettingsPage.Appearance, localizer["Appearance"]);
            AddNav(SettingsPage.Profiles, localizer["Profiles"]);
            AddNav(SettingsPage.Hotkeys, localizer["Hotkeys"]);
            AddNav(SettingsPage.Language, localizer["Language"]);
            AddNav(SettingsPage.Advanced, localizer["Advanced"]);

            pageTitle.Text = PageName(page);
            contentHost.Children.Clear();
            contentHost.Children.Add(page switch
            {
                SettingsPage.General => BuildGeneralPage(),
                SettingsPage.Appearance => BuildAppearancePage(),
                SettingsPage.Profiles => BuildProfilesPage(),
                SettingsPage.Hotkeys => BuildHotkeysPage(),
                SettingsPage.Language => BuildLanguagePage(),
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
        stack.Children.Add(Card(CommandRow(
            (localizer["AddProfile"], addProfile, true),
            (localizer["ManageProfiles"], manageProfiles, false),
            (localizer["OpenProfilesFolder"], openProfilesFolder, false),
            (localizer["OpenRemovedProfilesFolder"], openRemovedProfilesFolder, false))));
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

        conflictText.Foreground = Brush("ErrorBrush");
        rows.Add(conflictText);
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

    private UIElement BuildAdvancedPage()
    {
        var stack = PageStack();
        stack.Children.Add(Card(
            NumberInput(localizer["GracefulTimeout"], localizer["GracefulTimeoutHelp"], settings.GracefulCloseTimeoutSeconds, value => settings.GracefulCloseTimeoutSeconds = (int)value, 1, 1, 60),
            SettingCheck(localizer["ForceCloseFallback"], localizer["ForceCloseFallbackHelp"], settings.ForceCloseFallback, value => settings.ForceCloseFallback = value),
            SettingCheck(localizer["ConfirmForceClose"], localizer["ConfirmForceCloseHelp"], settings.ConfirmBeforeForceClose, value => settings.ConfirmBeforeForceClose = value)));
        stack.Children.Add(Card(CommandRow(
            (localizer["OpenApplicationData"], openApplicationDataFolder, false),
            (localizer["OpenBackups"], openBackupsFolder, false),
            (localizer["OpenLogs"], openLogsFolder, false),
            (localizer["ResetPosition"], resetPosition, false),
            (localizer["ResetSettings"], resetSettings, false),
            (localizer["ExportSettings"], ExportSettings, false),
            (localizer["ImportSettings"], ImportSettings, false),
            (localizer["ExitApplication"], exitApplication, false))));
        return stack;
    }

    private UIElement SettingCheck(string title, string subtitle, bool value, Action<bool> setter)
    {
        var check = new CheckBox { IsChecked = value, HorizontalAlignment = HorizontalAlignment.Left };
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
        var box = new TextBox
        {
            Text = value.ToString("0.##", CultureInfo.InvariantCulture),
            MinWidth = 150,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.SizeWE,
        };
        Point dragStart = default;
        double dragStartValue = value;
        bool dragging = false;
        box.LostFocus += (_, _) => CommitNumber(box, value, setter, minimum, maximum);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitNumber(box, value, setter, minimum, maximum);
                e.Handled = true;
            }
        };
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            dragStart = e.GetPosition(this);
            dragStartValue = TryParseFinite(box.Text, out double parsed) ? parsed : SanitizeNumber(value, minimum, maximum);
            dragging = false;
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
            setter(next);
            box.Text = (dragStep < 1 ? next : Math.Round(next)).ToString("0.##", CultureInfo.InvariantCulture);
            box.CaretIndex = box.Text.Length;
            e.Handled = true;
        };
        box.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!dragging)
            {
                return;
            }

            dragging = false;
            CommitNumber(box, value, setter, minimum, maximum);
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
                conflictText.Text = localizer["UseModifier"];
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

    private void CommitNumber(TextBox box, double previousValue, Action<double> setter, double minimum, double maximum)
    {
        if (!TryParseFinite(box.Text, out double parsed))
        {
            box.Text = SanitizeNumber(previousValue, minimum, maximum).ToString("0.##", CultureInfo.InvariantCulture);
            conflictText.Text = localizer["InvalidNumber"];
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
        Advanced,
    }

    private sealed record Option<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }
}
