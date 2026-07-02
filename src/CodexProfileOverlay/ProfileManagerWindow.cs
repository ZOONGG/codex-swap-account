using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexProfileOverlay.Core.Models;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;

namespace CodexProfileOverlay;

internal sealed class ProfileManagerWindow : Window
{
    private readonly ListBox listBox = new();
    private IReadOnlyList<ProfileInfo> profiles;

    public ProfileManagerWindow(
        IReadOnlyList<ProfileInfo> profiles,
        string? activeProfile,
        Action addProfile,
        Action<ProfileInfo> renameDisplayName,
        Action<ProfileInfo> renameDirectory,
        Action<ProfileInfo> removeProfile,
        Action<IReadOnlyList<string>> reorder,
        Action<ProfileInfo> openProfileFolder,
        Action refreshProfiles)
    {
        this.profiles = profiles;
        Title = "Manage Profiles";
        Width = 560;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("OverlayBackgroundBrush");
        Foreground = (Brush)Application.Current.FindResource("StrongTextBrush");

        var root = new DockPanel { Margin = new Thickness(16) };
        listBox.MinHeight = 320;
        DockPanel.SetDock(listBox, Dock.Top);
        root.Children.Add(listBox);

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Button("Add", addProfile));
        buttons.Children.Add(Button("Rename display", () => WithSelected(renameDisplayName)));
        buttons.Children.Add(Button("Rename folder", () => WithSelected(renameDirectory)));
        buttons.Children.Add(Button("Move up", () => MoveSelected(-1, reorder)));
        buttons.Children.Add(Button("Move down", () => MoveSelected(1, reorder)));
        buttons.Children.Add(Button("Remove", () => WithSelected(removeProfile)));
        buttons.Children.Add(Button("Open folder", () => WithSelected(openProfileFolder)));
        buttons.Children.Add(Button("Refresh", () =>
        {
            refreshProfiles();
            Close();
        }));
        root.Children.Add(buttons);
        Content = root;
        RefreshList(activeProfile);
    }

    public void UpdateProfiles(IReadOnlyList<ProfileInfo> newProfiles, string? activeProfile)
    {
        profiles = newProfiles;
        RefreshList(activeProfile);
    }

    private void RefreshList(string? activeProfile)
    {
        listBox.Items.Clear();
        foreach (ProfileInfo profile in profiles)
        {
            listBox.Items.Add(new ListBoxItem
            {
                Content = $"{profile.DisplayName}  ({profile.Name})" + (string.Equals(profile.Name, activeProfile, StringComparison.OrdinalIgnoreCase) ? "  Active" : string.Empty),
                Tag = profile,
            });
        }
    }

    private Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), MinWidth = 92, Height = 32 };
        button.Click += (_, _) => action();
        return button;
    }

    private void WithSelected(Action<ProfileInfo> action)
    {
        if (listBox.SelectedItem is ListBoxItem { Tag: ProfileInfo profile })
        {
            action(profile);
        }
    }

    private void MoveSelected(int delta, Action<IReadOnlyList<string>> reorder)
    {
        int index = listBox.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= profiles.Count)
        {
            return;
        }

        var ordered = profiles.Select(static profile => profile.Name).ToList();
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        reorder(ordered);
        listBox.SelectedIndex = target;
    }
}
