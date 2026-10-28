using System.Runtime.InteropServices;

namespace DevOpsWatcher;

/// <summary>Borderless, top-most, dark, rounded window used for toasts, approvals and the dashboard.</summary>
class PopupForm : Form
{
    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, CS_DROPSHADOW = 0x20000;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public PopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Palette.Surface;
        ForeColor = Palette.Text;
        Font = new Font("Segoe UI", 9f);
        DoubleBuffered = true;
    }

    protected int Px(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND (Windows 11; ignored elsewhere)
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }
}

/// <summary>Stacks popups in the bottom-right corner: approvals at the bottom, toasts above.</summary>
static class PopupHost
{
    const int MaxToasts = 4;
    static readonly List<PopupForm> Approvals = [];
    static readonly List<PopupForm> Toasts = [];

    public static void Show(PopupForm form, bool sticky)
    {
        var list = sticky ? Approvals : Toasts;
        list.Insert(0, form);
        form.FormClosed += (_, _) => { list.Remove(form); Relayout(); };
        form.SizeChanged += (_, _) => Relayout();
        while (Toasts.Count > MaxToasts) Toasts[^1].Close();
        Relayout();
        form.Show();
    }

    public static void Relayout()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        int gap = (int)(10 * GapScale());
        int y = area.Bottom - gap;
        foreach (var f in Approvals.Concat(Toasts))
        {
            y -= f.Height;
            f.Location = new Point(area.Right - f.Width - gap, y);
            y -= gap;
        }
    }

    static float GapScale() => (Approvals.Concat(Toasts).FirstOrDefault()?.DeviceDpi ?? 96) / 96f;
}

/// <summary>Transient notification that fades out after a few seconds (paused while hovered).</summary>
sealed class ToastForm : PopupForm
{
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int LifetimeMs = 6000;

    readonly Color _accent;
    readonly string _title, _body;
    readonly string? _url;
    readonly Font _titleFont = new("Segoe UI Semibold", 9.75f);
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    int _remainingMs = LifetimeMs;
    bool _hover, _fading;
    Rectangle _closeRect;

    public ToastForm(Color accent, string title, string body, string? url)
    {
        _accent = accent;
        _title = title;
        _body = body;
        _url = url;

        int width = Px(340);
        var bodySize = TextRenderer.MeasureText(body, Font, new Size(width - Px(52), 0),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        int bodyHeight = Math.Min(bodySize.Height, Font.Height * 2 + 2);
        ClientSize = new Size(width, Px(12) + _titleFont.Height + Px(3) + bodyHeight + Px(13));
        _closeRect = new Rectangle(width - Px(28), Px(6), Px(22), Px(22));
        Cursor = url != null ? Cursors.Hand : Cursors.Default;

        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE;
            return cp;
        }
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
            _remainingMs -= _timer.Interval;
            _fading = _remainingMs <= 0;
            return;
        }
        Opacity -= 0.08;
        if (Opacity <= 0.05) Close();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; base.OnMouseEnter(e); }

    protected override void OnMouseLeave(EventArgs e) { _hover = false; base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!_closeRect.Contains(e.Location)) Shell.Open(_url);
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var accent = new SolidBrush(_accent)) g.FillRectangle(accent, 0, 0, Px(5), Height);

        int x = Px(18), w = Width - x - Px(32);
        int titleY = Px(12);
        TextRenderer.DrawText(g, _title, _titleFont, new Rectangle(x, titleY, w, _titleFont.Height), Palette.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        int bodyY = titleY + _titleFont.Height + Px(3);
        TextRenderer.DrawText(g, _body, Font, new Rectangle(x, bodyY, Width - x - Px(16), Height - bodyY - Px(8)), Palette.Muted,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, "✕", Font, _closeRect, Palette.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>Sticky approval prompt. Stays on screen until approved, rejected or snoozed.</summary>
sealed class ApprovalForm : PopupForm
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<ApprovalInfo, bool, string, Task>? Decide { get; set; }
    public event Action<ApprovalInfo>? Snoozed;
    public ApprovalInfo Approval { get; private set; }

    readonly FlowLayoutPanel _flow;
    readonly Label _meta, _status;
    readonly TextBox _comment;
    readonly Button _approve, _reject, _open, _later;
    readonly System.Windows.Forms.Timer _pulse = new() { Interval = 550 };
    bool _pulseOn, _confirmReject;
    string _approveText = "Approve";

    public ApprovalForm(ApprovalInfo approval)
    {
        Approval = approval;
        int contentWidth = Px(340);

        _flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(Px(20), Px(16)),
            Margin = Padding.Empty,
            BackColor = Color.Transparent,
        };

        _flow.Controls.Add(MakeLabel("⚠  APPROVAL NEEDED", new Font("Segoe UI Semibold", 8.5f), Palette.Orange, contentWidth));
        _flow.Controls.Add(MakeLabel(approval.PipelineName, new Font("Segoe UI Semibold", 12.5f), Palette.Text, contentWidth, Px(2)));
        _meta = MakeLabel(MetaText(approval), Font, Palette.Muted, contentWidth, Px(2));
        _flow.Controls.Add(_meta);
        if (approval.Approvers.Count > 0)
            _flow.Controls.Add(MakeLabel("Approvers: " + string.Join(", ", approval.Approvers), Font, Palette.Muted, contentWidth, Px(2)));
        if (!string.IsNullOrWhiteSpace(approval.Instructions))
            _flow.Controls.Add(MakeLabel(approval.Instructions.Trim(), Font, Palette.Text, contentWidth, Px(8)));

        _comment = new TextBox
        {
            Width = contentWidth,
            PlaceholderText = "Comment (optional)",
            BackColor = Palette.Input,
            ForeColor = Palette.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, Px(12), 0, 0),
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, Px(10), 0, 0),
            BackColor = Color.Transparent,
        };
        _approve = MakeButton("Approve", Palette.Green, Color.White, filled: true);
        _reject = MakeButton("Reject", Palette.Surface, Palette.Red, filled: false);
        _open = MakeButton("Open", Palette.Surface, Palette.Text, filled: false);
        _later = MakeButton("Later", Palette.Surface, Palette.Muted, filled: false);

        bool isValidation = approval.Kind == ApprovalKind.ManualValidation;
        _approveText = isValidation ? "Resume" : "Approve";
        _approve.Text = _approveText;
        if (approval.CanApprove)
        {
            if (isValidation)
                _flow.Controls.Add(MakeLabel("Manual validation · Resume/Reject is experimental (undocumented Azure DevOps call).",
                    Font, Palette.Muted, contentWidth, Px(10)));
            else
                _flow.Controls.Add(_comment);
            buttons.Controls.AddRange([_approve, _reject, _open, _later]);
        }
        else
        {
            _flow.Controls.Add(MakeLabel(approval.BrowserHint ?? "Approve this run in the browser.",
                Font, Palette.Muted, contentWidth, Px(10)));
            buttons.Controls.AddRange([_open, _later]);
        }
        _flow.Controls.Add(buttons);

        _status = MakeLabel("", Font, Palette.Red, contentWidth, Px(8));
        _status.Visible = false;
        _flow.Controls.Add(_status);

        Controls.Add(_flow);

        _approve.Click += (_, _) => _ = DecideAsync(approve: true);
        _reject.Click += (_, _) =>
        {
            if (!_confirmReject) { _confirmReject = true; _reject.Text = "Confirm reject"; return; }
            _ = DecideAsync(approve: false);
        };
        _open.Click += (_, _) => Shell.Open(Approval.WebUrl);
        _later.Click += (_, _) => { Snoozed?.Invoke(Approval); Close(); };

        _flow.SizeChanged += (_, _) => FitToContent();
        FitToContent();

        _pulse.Tick += (_, _) => { _pulseOn = !_pulseOn; Invalidate(); };
        _pulse.Start();
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
        _status.Visible = false;
        try
        {
            await Decide(Approval, approve, _comment.Text.Trim());
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            _status.Visible = true;
            SetBusy(false);
        }
    }

    void SetBusy(bool busy)
    {
        foreach (var b in new[] { _approve, _reject, _later }) b.Enabled = !busy;
        _approve.Text = busy ? "Working…" : _approveText;
        UseWaitCursor = busy;
    }

    void FitToContent() => ClientSize = new Size(_flow.Right + Px(20), _flow.Bottom + Px(16));

    static string MetaText(ApprovalInfo a) => string.Join("  ·  ", new[]
    {
        a.RunName != "" ? $"Run {a.RunName}" : "",
        a.StageName is { Length: > 0 } s ? $"Stage: {s}" : "",
        Fmt.Ago(a.CreatedOn),
    }.Where(s => s != ""));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int t = Px(3);
        using var pen = new Pen(_pulseOn ? Palette.Red : Palette.Orange, t);
        e.Graphics.DrawRectangle(pen, t / 2f, t / 2f, Width - t, Height - t);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _pulse.Dispose();
        base.OnFormClosed(e);
    }

    static Label MakeLabel(string text, Font font, Color color, int width, int top = 0) => new()
    {
        Text = text,
        Font = font,
        ForeColor = color,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        Margin = new Padding(0, top, 0, 0),
        UseMnemonic = false,
        BackColor = Color.Transparent,
    };

    Button MakeButton(string text, Color back, Color fore, bool filled)
    {
        var b = new Button
        {
            Text = text,
            BackColor = back,
            ForeColor = fore,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 9f),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(Px(10), Px(3), Px(10), Px(3)),
            Margin = new Padding(0, 0, Px(8), 0),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = filled ? 0 : 1;
        b.FlatAppearance.BorderColor = Palette.Border;
        b.FlatAppearance.MouseOverBackColor = filled ? ControlPaint.Light(back, 0.2f) : Palette.Hover;
        return b;
    }
}
