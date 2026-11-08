using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CodexProfileOverlay;

internal sealed class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string primaryText, string cancelText, bool danger)
    {
        Title = title;
        Icon = AppIcons.WindowIcon;
        Width = 500;
        Height = message.Length > 150 ? 298 : 246;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Foreground = (Brush)Application.Current.FindResource("StrongTextBrush");
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        Brush accent = danger
            ? (Brush)Application.Current.FindResource("ErrorBrush")
            : (Brush)Application.Current.FindResource("AccentBrush");

        var chrome = new Border
        {
            Background = (Brush)Application.Current.FindResource("OverlayBackgroundBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("OverlayBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(22),
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        WindowDragHelper.Enable(this, chrome);

        var header = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = accent,
            Margin = new Thickness(0, 0, 12, 0),
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(danger ? "M 12 5 L 12 14 M 12 18 L 12.01 18" : "M 6 12 L 10 16 L 18 8"),
                Stroke = Brushes.White,
                StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.Uniform,
                Width = 20,
                Height = 20,
            },
        };
        header.Children.Add(badge);

        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("StrongTextBrush"),
        });
        Grid.SetColumn(header.Children[^1], 1);

        var close = new Button
        {
            Content = "x",
            Width = 30,
            Height = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            IsCancel = true,
        };
        close.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        root.Children.Add(header);

        var messageText = new TextBlock
        {
            Text = message,
            Foreground = (Brush)Application.Current.FindResource("MutedTextBrush"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Margin = new Thickness(0, 0, 0, 22),
        };
        Grid.SetRow(messageText, 1);
        root.Children.Add(messageText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button ok = new()
        {
            Content = primaryText,
            MinWidth = 126,
            MinHeight = 36,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
            Style = (Style)Application.Current.FindResource("PrimaryButtonStyle"),
        };
        ok.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = cancelText, MinWidth = 96, MinHeight = 36, IsCancel = true });
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        chrome.Child = root;
        Content = chrome;

        Loaded += (_, _) => Activate();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        };
    }

    public static bool Show(
        Window? owner,
        IntPtr nativeOwner,
        string title,
        string message,
        string primaryText,
        string cancelText,
        bool danger = false)
    {
        var dialog = new ConfirmDialog(title, message, primaryText, cancelText, danger);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }
        else if (nativeOwner != IntPtr.Zero)
        {
            new WindowInteropHelper(dialog).Owner = nativeOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        DialogPlacement.CenterOnOwnerScreen(dialog, owner, nativeOwner);
        return dialog.ShowDialog() == true;
    }
}
