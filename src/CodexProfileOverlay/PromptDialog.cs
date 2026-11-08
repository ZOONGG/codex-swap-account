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
    private bool alternateSelected;

    private PromptDialog(string title, string label, string initialValue, string primaryText, string cancelText, string? alternateText)
    {
        Title = title;
        Icon = AppIcons.WindowIcon;
        Width = alternateText is null ? 460 : 560;
        Height = 224;
        WindowStartupLocation = WindowStartupLocation.Manual;
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

        WindowDragHelper.Enable(this, chrome);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
        if (alternateText is not null)
        {
            var alternate = new Button { Content = alternateText, MinWidth = 108, MinHeight = 36, Margin = new Thickness(0, 0, 8, 0) };
            alternate.Click += (_, _) =>
            {
                alternateSelected = true;
                DialogResult = true;
                Close();
            };
            buttons.Children.Add(alternate);
        }
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
        return ShowCore(owner, nativeOwner, new PromptDialog(title, label, initialValue, primaryText, cancelText, null)).Name;
    }

    public static (string? Name, bool Capture) ShowProfileChoice(Window? owner, IntPtr nativeOwner, Localizer localizer) =>
        ShowCore(owner, nativeOwner, new PromptDialog(localizer["AddProfileTitle"], localizer["ProfileDirectoryName"],
            "", localizer["BrowserLogin"], localizer["Cancel"], localizer["DesktopCapture"]));

    private static (string? Name, bool Capture) ShowCore(Window? owner, IntPtr nativeOwner, PromptDialog dialog)
    {
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
        return dialog.ShowDialog() == true ? (dialog.Value, dialog.alternateSelected) : (null, false);
    }
}
