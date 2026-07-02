using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexProfileOverlay;

internal sealed class PromptDialog : Window
{
    private readonly TextBox textBox;

    private PromptDialog(string title, string label, string initialValue)
    {
        Title = title;
        Width = 380;
        Height = 170;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.FindResource("OverlayBackgroundBrush");
        Foreground = (Brush)Application.Current.FindResource("StrongTextBrush");
        ShowInTaskbar = false;

        var root = new DockPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = (Brush)Application.Current.FindResource("MutedTextBrush"),
        });

        textBox = new TextBox { Text = initialValue, Margin = new Thickness(0, 28, 0, 12) };
        root.Children.Add(textBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button cancel = new() { Content = "Cancel", MinWidth = 82, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        Button ok = new() { Content = "OK", MinWidth = 82, IsDefault = true };
        ok.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
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

    public static string? Show(Window owner, string title, string label, string initialValue = "")
    {
        var dialog = new PromptDialog(title, label, initialValue) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}
