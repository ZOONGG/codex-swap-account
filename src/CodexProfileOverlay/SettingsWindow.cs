using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodexProfileOverlay.Core.Models;
using CodexProfileOverlay.Core.Services;
using Control = System.Windows.Controls.Control;

namespace CodexProfileOverlay;

internal sealed class SettingsWindow : Window
{
    private readonly OverlaySettings settings;
    private readonly Action<OverlaySettings> save;
    private readonly TextBlock conflictText = new();
    private readonly StackPanel profileHotkeyPanel = new();
    private IReadOnlyList<ProfileInfo> profiles;

    public SettingsWindow(
        OverlaySettings settings,
        IReadOnlyList<ProfileInfo> profiles,
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
        this.save = save;

        Title = "Codex Profile Overlay Settings";
        Width = 720;
        Height = 760;
        MinWidth = 620;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("OverlayBackgroundBrush");
        Foreground = (Brush)Application.Current.FindResource("StrongTextBrush");

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new StackPanel { Margin = new Thickness(18) };
        scroll.Content = root;
        Content = scroll;

        root.Children.Add(Section("Appearance"));
        root.Children.Add(EnumCombo("Display mode", settings.DisplayMode, value => settings.DisplayMode = value));
        root.Children.Add(EnumCombo("Position preset", settings.PositionPreset, value => settings.PositionPreset = value));
        root.Children.Add(NumberRow("Custom X offset", settings.OffsetX, value => settings.OffsetX = value));
        root.Children.Add(NumberRow("Custom Y offset", settings.OffsetY, value => settings.OffsetY = value));
        root.Children.Add(NumberRow("UI scale", settings.Scale, value => settings.Scale = Math.Clamp(value, 0.8, 1.4)));
        root.Children.Add(Check("Animations", settings.AnimationsEnabled, value => settings.AnimationsEnabled = value));

        root.Children.Add(Section("Behavior"));
        root.Children.Add(Check("Start with Windows", settings.StartWithWindows, value => settings.StartWithWindows = value));
        root.Children.Add(Check("Show automatically when Codex opens", settings.ShowAutomaticallyWhenCodexOpens, value => settings.ShowAutomaticallyWhenCodexOpens = value));
        root.Children.Add(Check("Launch Codex when the overlay starts", settings.LaunchCodexWhenOverlayStarts, value => settings.LaunchCodexWhenOverlayStarts = value));
        root.Children.Add(Check("Launch Codex after switching", settings.LaunchCodexAfterSwitching, value => settings.LaunchCodexAfterSwitching = value));
        root.Children.Add(NumberRow("Graceful-close timeout seconds", settings.GracefulCloseTimeoutSeconds, value => settings.GracefulCloseTimeoutSeconds = (int)Math.Clamp(value, 1, 60)));
        root.Children.Add(Check("Force-close fallback", settings.ForceCloseFallback, value => settings.ForceCloseFallback = value));
        root.Children.Add(Check("Confirmation before force closing", settings.ConfirmBeforeForceClose, value => settings.ConfirmBeforeForceClose = value));

        root.Children.Add(Section("Hotkeys"));
        root.Children.Add(HotkeyRow("Toggle overlay", settings.Hotkeys.ToggleOverlay, value => settings.Hotkeys.ToggleOverlay = value));
        profileHotkeyPanel.Margin = new Thickness(0, 2, 0, 0);
        root.Children.Add(profileHotkeyPanel);
        RebuildProfileHotkeys();
        root.Children.Add(CommandRow(("Restore defaults", () =>
        {
            settings.Hotkeys = HotkeySettings.CreateDefault();
            RebuildProfileHotkeys();
            Save();
        })));
        conflictText.Foreground = Brushes.IndianRed;
        conflictText.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(conflictText);

        root.Children.Add(Section("Profiles"));
        root.Children.Add(CommandRow(
            ("Add", addProfile),
            ("Manage", manageProfiles),
            ("Refresh", () => Close()),
            ("Open profiles folder", openProfilesFolder),
            ("Open removed profiles folder", openRemovedProfilesFolder)));

        root.Children.Add(Section("Advanced"));
        root.Children.Add(CommandRow(
            ("Open app data", openApplicationDataFolder),
            ("Open backups", openBackupsFolder),
            ("Open logs", openLogsFolder)));
        root.Children.Add(CommandRow(
            ("Reset position", resetPosition),
            ("Reset settings", resetSettings),
            ("Export settings", ExportSettings),
            ("Import settings", ImportSettings),
            ("Exit application", exitApplication)));

        Closing += (_, _) => Save();
    }

    public void UpdateProfiles(IReadOnlyList<ProfileInfo> newProfiles)
    {
        profiles = newProfiles;
        RebuildProfileHotkeys();
    }

    public void SetConflicts(IReadOnlyList<string> conflicts)
    {
        conflictText.Text = conflicts.Count == 0 ? string.Empty : string.Join(Environment.NewLine, conflicts);
    }

    private void RebuildProfileHotkeys()
    {
        profileHotkeyPanel.Children.Clear();
        while (settings.Hotkeys.ProfileHotkeys.Count < 9)
        {
            settings.Hotkeys.ProfileHotkeys.Add(null);
        }

        for (int index = 0; index < Math.Min(9, profiles.Count); index++)
        {
            int captured = index;
            profileHotkeyPanel.Children.Add(HotkeyRow($"Profile {index + 1}: {profiles[index].DisplayName}", settings.Hotkeys.ProfileHotkeys[index], value => settings.Hotkeys.ProfileHotkeys[captured] = value));
        }
    }

    private TextBlock Section(string title)
    {
        return new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 8),
        };
    }

    private UIElement EnumCombo<T>(string label, T value, Action<T> setter)
        where T : struct, Enum
    {
        var combo = new ComboBox
        {
            ItemsSource = Enum.GetValues<T>(),
            SelectedItem = value,
            Width = 180,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is T selected)
            {
                setter(selected);
                Save();
            }
        };
        return Labeled(label, combo);
    }

    private UIElement NumberRow(string label, double value, Action<double> setter)
    {
        var box = new TextBox { Text = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), Width = 100 };
        box.LostFocus += (_, _) =>
        {
            if (double.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            {
                setter(parsed);
                Save();
            }
        };
        return Labeled(label, box);
    }

    private UIElement Check(string label, bool value, Action<bool> setter)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
        check.Checked += (_, _) =>
        {
            setter(true);
            Save();
        };
        check.Unchecked += (_, _) =>
        {
            setter(false);
            Save();
        };
        return check;
    }

    private UIElement HotkeyRow(string label, HotkeyGesture? value, Action<HotkeyGesture?> setter)
    {
        var button = new Button { Content = value?.ToString() ?? "None", MinWidth = 130 };
        bool recording = false;
        button.Click += (_, _) =>
        {
            recording = true;
            button.Content = "Press keys...";
            button.Focus();
        };
        button.PreviewKeyDown += (_, e) =>
        {
            if (!recording)
            {
                return;
            }

            e.Handled = true;
            if (e.Key == Key.Escape || e.Key == Key.Back)
            {
                setter(null);
                button.Content = "None";
            }
            else if (e.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin))
            {
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

                int virtualKey = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
                var gesture = new HotkeyGesture(modifiers, virtualKey);
                setter(gesture);
                button.Content = gesture.ToString();
            }

            recording = false;
            Save();
        };
        return Labeled(label, button);
    }

    private UIElement Labeled(string label, Control control)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Width = 260,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("MutedTextBrush"),
        });
        panel.Children.Add(control);
        return panel;
    }

    private UIElement CommandRow(params (string Label, Action Action)[] actions)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        foreach ((string label, Action action) in actions)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8), MinWidth = 96, Height = 32 };
            button.Click += (_, _) => action();
            panel.Children.Add(button);
        }

        return panel;
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

    private void Save() => save(settings);
}
