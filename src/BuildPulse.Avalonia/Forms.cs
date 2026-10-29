using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BuildPulse;

/// <summary>Two-column "caption | control" layout shared by the settings windows.</summary>
sealed class FormGrid : Grid
{
    public FormGrid()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*");
        Margin = new Thickness(16);
    }

    /// <summary>Adds a row; <paramref name="fill"/> rows take the leftover height. Returns the caption.</summary>
    public TextBlock Row(string caption, Control control, bool fill = false)
    {
        RowDefinitions.Add(new RowDefinition(fill ? GridLength.Star : GridLength.Auto));
        int row = RowDefinitions.Count - 1;
        var label = new TextBlock { Text = caption, Margin = new Thickness(0, 6, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        SetRow(label, row);
        SetRow(control, row);
        SetColumn(control, 1);
        control.Margin = new Thickness(0, 3);
        Children.Add(label);
        Children.Add(control);
        return label;
    }

    /// <summary>A control across both columns, e.g. the OK/Cancel row.</summary>
    public void Wide(Control control)
    {
        RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        SetRow(control, RowDefinitions.Count - 1);
        SetColumnSpan(control, 2);
        Children.Add(control);
    }

    public static StackPanel Flow(params Control[] controls)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.AddRange(controls);
        foreach (var c in controls) c.VerticalAlignment = VerticalAlignment.Center;
        return panel;
    }

    public static TextBlock Hint(string text = "") => new()
    {
        Text = text,
        Foreground = Palette.Muted,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 460,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    public static TextBlock Unit(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    public static NumericUpDown Number(int min, int max, int value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = Math.Clamp(value, min, max),
        Increment = 1,
        FormatString = "0",
        Width = 130,
    };

    /// <summary>Right-aligned buttons; the first is the default (Enter), the last cancels (Esc).</summary>
    public static StackPanel Buttons(params Button[] buttons)
    {
        buttons[0].IsDefault = true;
        buttons[0].Classes.Add("accent");
        buttons[^1].IsCancel = true;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.AddRange(buttons);
        return panel;
    }

    /// <summary>Where tokens end up, for the hint under the token box.</summary>
    public static string TokenStorage =>
        OperatingSystem.IsWindows() ? "Stored encrypted for your Windows user."
        : OperatingSystem.IsMacOS() ? "Stored in your login Keychain."
        : "Tokens can't be stored securely on this OS yet.";
}

/// <summary>Avalonia has no MessageBox: a small modal with a message and OK (and optionally Cancel).</summary>
static class Dialogs
{
    public static Task<bool> ConfirmAsync(Window owner, string title, string message, string ok = "OK") => ShowAsync(owner, title, message, ok, cancel: true);

    public static Task ErrorAsync(Window owner, string message) => ShowAsync(owner, AppInfo.Name, message, "OK", cancel: false);

    static Task<bool> ShowAsync(Window owner, string title, string message, string ok, bool cancel)
    {
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            Background = Palette.Background,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var okButton = new Button { Content = ok };
        var cancelButton = new Button { Content = "Cancel", IsVisible = cancel };
        okButton.Click += (_, _) => window.Close(true);
        cancelButton.Click += (_, _) => window.Close(false);
        var stack = new StackPanel { Margin = new Thickness(20), MaxWidth = 420 };
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(FormGrid.Buttons(okButton, cancelButton));
        window.Content = stack;
        return window.ShowDialog<bool>(owner);
    }
}
