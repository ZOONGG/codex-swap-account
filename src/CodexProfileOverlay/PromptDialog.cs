using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexProfileOverlay;

internal sealed class PromptDialog : Window
{
    private readonly TextBox textBox;

    private PromptDialog(string title, string label, string initialValue, string primaryText, string cancelText)
    {
        Title = title;
        Width = 460;
        Height = 224;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Foreground = (Brush)Application.Current.FindResource("StrongTextBrush");
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

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
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("StrongTextBrush"),
        });

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
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        root.Children.Add(header);

        var labelText = new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = (Brush)Application.Current.FindResource("MutedTextBrush"),
            FontSize = 13.5,
        };
        Grid.SetRow(labelText, 1);
        root.Children.Add(labelText);

        textBox = new TextBox { Text = initialValue, Margin = new Thickness(0, 0, 0, 18), MinHeight = 42 };
        Grid.SetRow(textBox, 2);
        root.Children.Add(textBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button ok = new()
        {
            Content = primaryText,
            MinWidth = 108,
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
        Button cancel = new() { Content = cancelText, MinWidth = 96, MinHeight = 36, IsCancel = true };
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);
        chrome.Child = root;
        Content = chrome;

        Loaded += (_, _) =>
        {
            Activate();
            textBox.Focus();
            textBox.SelectAll();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        };
    }

    public string Value => textBox.Text.Trim();

    public static string? Show(Window? owner, IntPtr nativeOwner, string title, string label, string initialValue = "", string primaryText = "OK", string cancelText = "Cancel")
    {
        var dialog = new PromptDialog(title, label, initialValue, primaryText, cancelText);
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

        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}
