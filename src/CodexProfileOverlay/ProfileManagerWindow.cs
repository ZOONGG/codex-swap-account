using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodexProfileOverlay.Core.Models;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;

namespace CodexProfileOverlay;

internal sealed class ProfileManagerWindow : Window
{
    private readonly ListBox listBox = new();
    private readonly Localizer localizer;
    private readonly Action<ProfileInfo> renameDisplayName;
    private readonly Action<ProfileInfo> removeProfile;
    private readonly Action<IReadOnlyList<string>> reorder;
    private readonly Action<ProfileInfo> openProfileFolder;
    private readonly Action refreshProfiles;
    private IReadOnlyList<ProfileInfo> profiles;
    private string? activeProfile;
    private Point dragStart;
    private ListBoxItem? draggedItem;
    private bool isDraggingProfile;
    private bool reorderedDuringDrag;

    public ProfileManagerWindow(
        IReadOnlyList<ProfileInfo> profiles,
        string? activeProfile,
        Localizer localizer,
        Action addProfile,
        Action<ProfileInfo> renameDisplayName,
        Action<ProfileInfo> removeProfile,
        Action<IReadOnlyList<string>> reorder,
        Action<ProfileInfo> openProfileFolder,
        Action refreshProfiles)
    {
        this.profiles = profiles;
        this.activeProfile = activeProfile;
        this.localizer = localizer;
        this.renameDisplayName = renameDisplayName;
        this.removeProfile = removeProfile;
        this.reorder = reorder;
        this.openProfileFolder = openProfileFolder;
        this.refreshProfiles = refreshProfiles;

        Title = localizer["ManageProfiles"];
        Width = 720;
        Height = 620;
        MinWidth = 640;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("WindowBackgroundBrush");
        Foreground = Brush("StrongTextBrush");
        FontSize = 15;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        Content = BuildShell(addProfile);
        localizer.LanguageChanged += Rebuild;
        Closed += (_, _) => localizer.LanguageChanged -= Rebuild;
        Rebuild();
    }

    public void UpdateProfiles(IReadOnlyList<ProfileInfo> newProfiles, string? newActiveProfile)
    {
        profiles = newProfiles;
        activeProfile = newActiveProfile;
        Rebuild();
    }

    private UIElement BuildShell(Action addProfile)
    {
        var root = new Grid { Margin = new Thickness(26) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = localizer["ManageProfiles"],
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 18),
        });

        listBox.AllowDrop = true;
        listBox.Background = Brushes.Transparent;
        listBox.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        listBox.MouseMove += OnMouseMove;
        listBox.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        Grid.SetRow(listBox, 1);
        root.Children.Add(listBox);

        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        footer.Children.Add(FooterButton(localizer["AddProfile"], addProfile, true));
        footer.Children.Add(FooterButton(localizer["Refresh"], () =>
        {
            refreshProfiles();
            Rebuild();
        }, false));
        var done = FooterButton(localizer["Done"], Close, false);
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private void Rebuild()
    {
        Title = localizer["ManageProfiles"];
        listBox.Items.Clear();
        foreach (ProfileInfo profile in profiles)
        {
            bool isActive = string.Equals(profile.Name, activeProfile, StringComparison.OrdinalIgnoreCase);
            listBox.Items.Add(new ListBoxItem
            {
                Tag = profile,
                Background = isActive ? Brush("TabActiveBrush") : Brushes.Transparent,
                Content = BuildRow(profile),
            });
        }
    }

    private UIElement BuildRow(ProfileInfo profile)
    {
        bool isActive = string.Equals(profile.Name, activeProfile, StringComparison.OrdinalIgnoreCase);
        var grid = new Grid { Height = 66, Margin = new Thickness(0), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

        if (isActive)
        {
            grid.Children.Add(new Border
            {
                Width = 3,
                Height = 34,
                CornerRadius = new CornerRadius(2),
                Background = Brush("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var grip = new TextBlock
        {
            Text = "⋮⋮",
            Foreground = Brush("MutedTextBrush"),
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.SizeAll,
        };
        Grid.SetColumn(grip, 1);
        grid.Children.Add(grip);

        var avatar = CreateAvatar(profile.Initials, profile.Accent);
        Grid.SetColumn(avatar, 2);
        grid.Children.Add(avatar);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = profile.DisplayName, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = profile.Name, FontSize = 13, Foreground = Brush("MutedTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 3);
        grid.Children.Add(text);

        if (isActive)
        {
            var badge = new Border
            {
                Background = Brush("TabActiveBrush"),
                BorderBrush = Brush("AccentBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 4, 10, 4),
                Child = new TextBlock { Text = localizer["Active"], FontSize = 12.5, Foreground = Brush("StrongTextBrush") },
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 6, 0),
            };
            Grid.SetColumn(badge, 4);
            grid.Children.Add(badge);
        }

        var menuButton = new Button
        {
            Content = "⋯",
            Width = 34,
            Height = 34,
            MinHeight = 34,
            Padding = new Thickness(0),
            ToolTip = localizer["Settings"],
        };
        var menu = new ContextMenu
        {
            Background = Brush("Surface2Brush"),
            BorderBrush = Brush("BorderBrush"),
            Foreground = Brush("StrongTextBrush"),
        };
        menu.Items.Add(MenuItem(localizer["Rename"], () => renameDisplayName(profile)));
        menu.Items.Add(MenuItem(localizer["OpenFolder"], () => openProfileFolder(profile)));
        var remove = MenuItem(localizer["Remove"], () => removeProfile(profile));
        remove.IsEnabled = !isActive;
        menu.Items.Add(remove);
        menuButton.ContextMenu = menu;
        menuButton.Click += (_, _) =>
        {
            menu.PlacementTarget = menuButton;
            menu.IsOpen = true;
        };
        Grid.SetColumn(menuButton, 5);
        grid.Children.Add(menuButton);

        return grid;
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(listBox);
        draggedItem = ItemsControl.ContainerFromElement(listBox, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (draggedItem is not null)
        {
            listBox.SelectedItem = draggedItem;
        }

        isDraggingProfile = false;
        reorderedDuringDrag = false;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || draggedItem?.Tag is not ProfileInfo)
        {
            return;
        }

        Point current = e.GetPosition(listBox);
        if (!isDraggingProfile)
        {
            if (Math.Abs(current.X - dragStart.X) <= SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(current.Y - dragStart.Y) <= SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            isDraggingProfile = true;
            draggedItem.Opacity = 0.72;
            Panel.SetZIndex(draggedItem, 10);
            Mouse.OverrideCursor = Cursors.SizeAll;
            _ = listBox.CaptureMouse();
        }

        MoveDraggedItemTo(current, e.OriginalSource as DependencyObject);
        e.Handled = true;
    }

    private void MoveDraggedItemTo(Point current, DependencyObject? originalSource)
    {
        if (draggedItem is null)
        {
            return;
        }

        ListBoxItem? targetItem = ItemsControl.ContainerFromElement(listBox, originalSource) as ListBoxItem
            ?? ItemAt(current);
        if (targetItem is null || ReferenceEquals(targetItem, draggedItem))
        {
            return;
        }

        int sourceIndex = listBox.Items.IndexOf(draggedItem);
        int targetIndex = listBox.Items.IndexOf(targetItem);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            return;
        }

        var animatedItems = new List<ListBoxItem>();
        int first = Math.Min(sourceIndex, targetIndex);
        int last = Math.Max(sourceIndex, targetIndex);
        for (int index = first; index <= last; index++)
        {
            if (listBox.Items[index] is ListBoxItem item && !ReferenceEquals(item, draggedItem))
            {
                animatedItems.Add(item);
            }
        }

        listBox.Items.RemoveAt(sourceIndex);
        listBox.Items.Insert(targetIndex, draggedItem);
        listBox.SelectedItem = draggedItem;
        reorderedDuringDrag = true;

        double offset = sourceIndex < targetIndex ? draggedItem.ActualHeight + 8 : -(draggedItem.ActualHeight + 8);
        foreach (ListBoxItem item in animatedItems)
        {
            AnimateRowShift(item, offset);
        }
    }

    private ListBoxItem? ItemAt(Point point)
    {
        HitTestResult? result = VisualTreeHelper.HitTest(listBox, point);
        return result is null
            ? null
            : ItemsControl.ContainerFromElement(listBox, result.VisualHit) as ListBoxItem;
    }

    private static void AnimateRowShift(ListBoxItem item, double offset)
    {
        var transform = item.RenderTransform as TranslateTransform;
        if (transform is null)
        {
            transform = new TranslateTransform();
            item.RenderTransform = transform;
        }

        transform.Y = offset;
        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!isDraggingProfile)
        {
            draggedItem = null;
            return;
        }

        FinishDrag();
        e.Handled = true;
    }

    private void FinishDrag()
    {
        if (draggedItem is not null)
        {
            draggedItem.Opacity = 1;
            Panel.SetZIndex(draggedItem, 0);
        }

        listBox.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;

        if (reorderedDuringDrag)
        {
            reorder(CurrentOrder());
        }

        draggedItem = null;
        isDraggingProfile = false;
        reorderedDuringDrag = false;
    }

    private IReadOnlyList<string> CurrentOrder() => listBox.Items
        .OfType<ListBoxItem>()
        .Select(item => ((ProfileInfo)item.Tag).Name)
        .ToArray();

    private Button FooterButton(string text, Action action, bool primary)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 124,
            Margin = new Thickness(0, 0, 10, 0),
        };
        if (primary)
        {
            button.Style = (Style)FindResource("PrimaryButtonStyle");
        }

        button.Click += (_, _) => action();
        return button;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private Border CreateAvatar(string initials, string accent)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(accent)!;
        return new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = brush,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = initials,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private SolidColorBrush Brush(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Clone();
}
