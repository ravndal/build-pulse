using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace BuildPulse;

/// <summary>Borderless, top-most, dark, rounded window used for toasts and approvals.</summary>
class PopupWindow : Window
{
    protected const double ContentWidth = 340;
    protected readonly Border Frame;

    public PopupWindow()
    {
        WindowDecorations = WindowDecorations.None;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Content = Frame = new Border
        {
            Background = Palette.Surface,
            BorderBrush = Palette.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
        };
    }
}

/// <summary>
/// Stacks popups in the screen corner next to the tray: bottom-right on Windows and Linux, top-right under the menu bar
/// on macOS. Approvals sit closest to the corner, toasts beyond them.
/// </summary>
static class PopupHost
{
    const int MaxToasts = 4;
    static readonly List<PopupWindow> Approvals = [];
    static readonly List<PopupWindow> Toasts = [];

    public static void Show(PopupWindow window, bool sticky)
    {
        var list = sticky ? Approvals : Toasts;
        list.Insert(0, window);
        window.Closed += (_, _) => { list.Remove(window); Relayout(); };
        window.PropertyChanged += (_, e) => { if (e.Property == Window.ClientSizeProperty) Relayout(); };
        while (Toasts.Count > MaxToasts) Toasts[^1].Close();
        window.Show();
        Relayout();
    }

    static void Relayout()
    {
        var all = Approvals.Concat(Toasts).ToList();
        if (all.Count == 0 || all[0].Screens.Primary is not { } screen) return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        int gap = (int)(10 * scale);
        bool fromTop = OperatingSystem.IsMacOS();
        int y = fromTop ? area.Y + gap : area.Bottom - gap;
        foreach (var w in all)
        {
            var size = PixelSize.FromSize(w.ClientSize, scale);
            if (!fromTop) y -= size.Height;
            w.Position = new PixelPoint(area.Right - size.Width - gap, y);
            y += fromTop ? size.Height + gap : -gap;
        }
    }
}

/// <summary>Transient notification that fades out after a few seconds (paused while hovered).</summary>
sealed class ToastWindow : PopupWindow
{
    const int LifetimeMs = 6000;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    int _remainingMs = LifetimeMs;
    bool _hover, _fading;

    public ToastWindow(Color accent, string title, string body, string? url)
    {
        ShowActivated = false;
        var close = new TextBlock
        {
            Text = "✕",
            FontSize = 12,
            Foreground = Palette.Muted,
            Padding = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        close.PointerPressed += (_, e) => { e.Handled = true; Close(); };
        DockPanel.SetDock(close, Dock.Right);

        var text = new StackPanel { Margin = new Thickness(13, 11, 0, 12), Spacing = 3 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Text, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = body, FontSize = 12.5, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });

        var accentBar = new Border { Width = 5, Background = new SolidColorBrush(accent) };
        DockPanel.SetDock(accentBar, Dock.Left);
        var dock = new DockPanel { Width = ContentWidth };
        dock.Children.Add(accentBar);
        dock.Children.Add(close);
        dock.Children.Add(text);
        Frame.Child = dock;

        if (url != null) Cursor = new Cursor(StandardCursorType.Hand);
        PointerEntered += (_, _) => _hover = true;
        PointerExited += (_, _) => _hover = false;
        PointerPressed += (_, _) => { Shell.Open(url); Close(); };
        _timer.Tick += OnTick;
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    void OnTick(object? sender, EventArgs e)
    {
        if (_hover)
        {
            if (_fading) { _fading = false; Opacity = 1; _remainingMs = LifetimeMs / 2; }
            return;
        }
        if (!_fading)
        {
            _remainingMs -= (int)_timer.Interval.TotalMilliseconds;
            _fading = _remainingMs <= 0;
            return;
        }
        Opacity -= 0.08;
        if (Opacity <= 0.05) Close();
    }
}

/// <summary>Sticky approval prompt. Stays on screen until approved, rejected or snoozed.</summary>
sealed class ApprovalWindow : PopupWindow
{
    public Func<ApprovalInfo, bool, string, Task>? Decide { get; set; }
    public event Action<ApprovalInfo>? Snoozed;
    public ApprovalInfo Approval { get; private set; }

    readonly TextBlock _meta, _status;
    readonly TextBox _comment = new() { PlaceholderText = "Comment (optional)", Margin = new Thickness(0, 12, 0, 0) };
    readonly Button _approve, _reject, _open, _later;
    readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromMilliseconds(550) };
    readonly string _approveText;
    bool _pulseOn, _confirmReject;

    public ApprovalWindow(ApprovalInfo approval)
    {
        Approval = approval;
        Title = "Approval needed";
        var stack = new StackPanel { Width = ContentWidth, Margin = new Thickness(20, 16) };

        stack.Children.Add(Text("⚠  APPROVAL NEEDED", 11.5, new SolidColorBrush(Palette.Orange), FontWeight.SemiBold));
        stack.Children.Add(Text(approval.PipelineName, 17, Palette.Text, FontWeight.SemiBold, top: 2));
        stack.Children.Add(_meta = Text(MetaText(approval), 12.5, Palette.Muted, top: 2));
        if (approval.Approvers.Count > 0)
            stack.Children.Add(Text("Approvers: " + string.Join(", ", approval.Approvers), 12.5, Palette.Muted, top: 2));
        if (!string.IsNullOrWhiteSpace(approval.Instructions))
            stack.Children.Add(Text(approval.Instructions.Trim(), 12.5, Palette.Text, top: 8));

        _approve = MakeButton("Approve", new SolidColorBrush(Palette.Green), Brushes.White);
        _reject = MakeButton("Reject", Palette.Surface, new SolidColorBrush(Palette.Red));
        _open = MakeButton("Open", Palette.Surface, Palette.Text);
        _later = MakeButton("Later", Palette.Surface, Palette.Muted);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };

        bool isValidation = approval.Kind == ApprovalKind.ManualValidation;
        _approveText = isValidation ? "Resume" : "Approve";
        _approve.Content = _approveText;
        if (approval.CanApprove)
        {
            if (isValidation)
                stack.Children.Add(Text("Manual validation · Resume/Reject is experimental (undocumented Azure DevOps call).", 12.5, Palette.Muted, top: 10));
            else
                stack.Children.Add(_comment);
            buttons.Children.AddRange([_approve, _reject, _open, _later]);
        }
        else
        {
            stack.Children.Add(Text(approval.BrowserHint ?? "Approve this run in the browser.", 12.5, Palette.Muted, top: 10));
            buttons.Children.AddRange([_open, _later]);
        }
        stack.Children.Add(buttons);
        stack.Children.Add(_status = Text("", 12.5, new SolidColorBrush(Palette.Red), top: 8));
        _status.IsVisible = false;

        Frame.Child = stack;
        Frame.BorderThickness = new Thickness(3);
        Frame.BorderBrush = new SolidColorBrush(Palette.Orange);

        _approve.Click += (_, _) => _ = DecideAsync(approve: true);
        _reject.Click += (_, _) =>
        {
            if (!_confirmReject) { _confirmReject = true; _reject.Content = "Confirm reject"; return; }
            _ = DecideAsync(approve: false);
        };
        _open.Click += (_, _) => Shell.Open(Approval.WebUrl);
        _later.Click += (_, _) => { Snoozed?.Invoke(Approval); Close(); };

        _pulse.Tick += (_, _) =>
        {
            _pulseOn = !_pulseOn;
            Frame.BorderBrush = new SolidColorBrush(_pulseOn ? Palette.Red : Palette.Orange);
        };
        _pulse.Start();
        Closed += (_, _) => _pulse.Stop();
    }

    public void UpdateApproval(ApprovalInfo approval)
    {
        Approval = approval;
        _meta.Text = MetaText(approval);
    }

    async Task DecideAsync(bool approve)
    {
        if (Decide == null) return;
        SetBusy(true);
        _status.IsVisible = false;
        try
        {
            await Decide(Approval, approve, _comment.Text?.Trim() ?? "");
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            _status.IsVisible = true;
            SetBusy(false);
        }
    }

    void SetBusy(bool busy)
    {
        foreach (var b in new[] { _approve, _reject, _later }) b.IsEnabled = !busy;
        _approve.Content = busy ? "Working…" : _approveText;
        Cursor = busy ? new Cursor(StandardCursorType.Wait) : Cursor.Default;
    }

    static string MetaText(ApprovalInfo a) => string.Join("  ·  ", new[]
    {
        a.RunName != "" ? $"Run {a.RunName}" : "",
        a.StageName is { Length: > 0 } s ? $"Stage: {s}" : "",
        Fmt.Ago(a.CreatedOn),
    }.Where(s => s != ""));

    static TextBlock Text(string text, double size, IBrush color, FontWeight weight = FontWeight.Normal, double top = 0) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = color,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, 0),
    };

    static Button MakeButton(string text, IBrush back, IBrush fore) => new()
    {
        Content = text,
        Background = back,
        Foreground = fore,
        BorderBrush = Palette.Border,
        BorderThickness = new Thickness(1),
        FontWeight = FontWeight.SemiBold,
        Padding = new Thickness(12, 5),
        Cursor = new Cursor(StandardCursorType.Hand),
    };
}
