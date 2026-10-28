namespace DevOpsWatcher;

public sealed record DashboardModel(
    IReadOnlyList<BuildInfo> Builds,
    IReadOnlyList<ApprovalInfo> Approvals,
    IReadOnlyDictionary<string, string> Stages,
    IReadOnlyDictionary<string, IReadOnlyList<StageStatus>> StageStatuses,
    DateTime? LastPoll,
    string? Error,
    TrayState State)
{
    public static readonly DashboardModel Empty = new([], [], new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<StageStatus>>(), null, null, TrayState.Unconfigured);
}

/// <summary>Compact, owner-drawn flyout listing pending approvals and the latest builds.</summary>
sealed class DashboardForm : PopupForm
{
    public const int MaxBuilds = 8;

    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public event Action<ApprovalInfo>? ApprovalClicked;

    sealed record Hit(Rectangle Rect, Action OnClick);

    readonly List<Hit> _hits = [];
    readonly Font _titleFont = new("Segoe UI Semibold", 12f);
    readonly Font _boldFont = new("Segoe UI Semibold", 9.5f);
    readonly Font _smallFont = new("Segoe UI", 8.25f);
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 90 };
    DashboardModel _model = DashboardModel.Empty;
    int _hover = -1, _frame;
    DateTime _hiddenAt;

    const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;

    public DashboardForm()
    {
        Width = Px(430);
        KeyPreview = true;
        _anim.Tick += (_, _) => { _frame++; Invalidate(); };
    }

    /// <summary>True right after the flyout auto-hid, so the tray click that caused it doesn't re-open it.</summary>
    public bool RecentlyHidden => (DateTime.Now - _hiddenAt).TotalMilliseconds < 400;

    protected override bool ShowWithoutActivation => false;

    public void SetModel(DashboardModel model)
    {
        _model = model;
        if (!Visible) return;
        var bottom = Bottom;
        Height = Render(null);
        Top = bottom - Height;
        Invalidate();
    }

    public void ShowNearTray()
    {
        Height = Render(null);
        var screen = Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;
        var bounds = screen.Bounds;
        int gap = Px(10);
        int x, y;
        if (area.Right < bounds.Right)          // taskbar on the right
        {
            x = area.Right - Width - gap;
            y = Math.Clamp(Cursor.Position.Y - Height / 2, area.Top + gap, area.Bottom - Height - gap);
        }
        else if (area.Left > bounds.Left)       // taskbar on the left
        {
            x = area.Left + gap;
            y = Math.Clamp(Cursor.Position.Y - Height / 2, area.Top + gap, area.Bottom - Height - gap);
        }
        else
        {
            x = Math.Clamp(Cursor.Position.X - Width / 2, area.Left + gap, area.Right - Width - gap);
            y = area.Top > bounds.Top ? area.Top + gap : area.Bottom - Height - gap;
        }
        Location = new Point(x, y);
        Show();
        Activate();
        _anim.Start();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        HideFlyout();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) HideFlyout();
        if (e.KeyCode == Keys.F5) RefreshRequested?.Invoke();
    }

    public void HideFlyout()
    {
        if (!Visible) return;
        _hiddenAt = DateTime.Now;
        _anim.Stop();
        Hide();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = _hits.FindIndex(h => h.Rect.Contains(e.Location));
        if (index == _hover) return;
        _hover = index;
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left)
            _hits.FirstOrDefault(h => h.Rect.Contains(e.Location))?.OnClick();
    }

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics);

    /// <summary>Draws the flyout (or only measures it when <paramref name="g"/> is null) and returns its height.</summary>
    int Render(Graphics? g)
    {
        _hits.Clear();
        var m = _model;
        int W = Width, pad = Px(16), y = 0;
        bool hovered(Rectangle r) => _hover >= 0 && _hover < _hits.Count && _hits[_hover].Rect == r;
        void Text(string s, Font f, Rectangle r, Color c, TextFormatFlags extra = 0)
        {
            if (g != null) TextRenderer.DrawText(g, s, f, r, c, Line | extra);
        }

        g?.Clear(Palette.Background);

        // Header
        int headerH = Px(60);
        var (stateColor, stateGlyph) = Palette.ForState(m.State);
        if (g != null)
        {
            int b = Px(26);
            Badge.Draw(g, new RectangleF(W - pad - b, (headerH - b) / 2f, b, b),
                m.State == TrayState.Approval && _frame / 5 % 2 == 1 ? Palette.Red : stateColor, stateGlyph, _frame);
        }
        Text("Pipelines", _titleFont, new Rectangle(pad, Px(10), W - pad * 3, Px(24)), Palette.Text);
        Text(Summary(m), Font, new Rectangle(pad, Px(34), W - pad * 4, Px(18)), Palette.Muted);
        y += headerH;

        if (m.Error != null)
        {
            int h = Px(30);
            if (g != null) { using var bg = new SolidBrush(Palette.ErrorTint); g.FillRectangle(bg, 0, y, W, h); }
            Text("⚠ " + m.Error, Font, new Rectangle(pad, y, W - pad * 2, h), Palette.Red);
            y += h;
        }

        // Pending approvals
        foreach (var a in m.Approvals)
        {
            int h = Px(54);
            var row = new Rectangle(0, y, W, h);
            _hits.Add(new Hit(row, () => ApprovalClicked?.Invoke(a)));
            if (g != null)
            {
                using var bg = new SolidBrush(hovered(row) ? ControlPaint.Light(Palette.ApprovalTint, 0.15f) : Palette.ApprovalTint);
                g.FillRectangle(bg, row);
                using var bar = new SolidBrush(Palette.Orange);
                g.FillRectangle(bar, 0, y, Px(4), h);
                int b = Px(22);
                Badge.Draw(g, new RectangleF(pad, y + (h - b) / 2f, b, b), _frame / 5 % 2 == 1 ? Palette.Red : Palette.Orange, Glyph.Bang);
            }
            int tx = pad + Px(34), rightW = Px(80);
            Text($"Approval needed · {a.PipelineName}", _boldFont, new Rectangle(tx, y + Px(8), W - tx - rightW - pad, Px(20)), Palette.Text);
            var sub = string.Join(" · ", new[] { a.RunName != "" ? $"#{a.RunName}" : "", a.StageName ?? "", Fmt.Ago(a.CreatedOn) }.Where(s => s != ""));
            Text(sub, Font, new Rectangle(tx, y + Px(28), W - tx - rightW - pad, Px(18)), Palette.Muted);
            Text("Review ›", _boldFont, new Rectangle(W - pad - rightW, y, rightW, h), Palette.Orange, TextFormatFlags.Right);
            y += h;
        }

        // Builds
        y += Px(8);
        Text("RECENT BUILDS", _smallFont, new Rectangle(pad, y, W - pad * 2, Px(18)), Palette.Muted);
        y += Px(22);

        if (m.Builds.Count == 0)
        {
            int h = Px(56);
            var msg = m.State == TrayState.Unconfigured ? "Open Settings to connect and pick pipelines." : "No builds yet.";
            Text(msg, Font, new Rectangle(pad, y, W - pad * 2, h), Palette.Muted, TextFormatFlags.HorizontalCenter);
            y += h;
        }

        var awaiting = m.Approvals.Select(a => a.RunKey).ToHashSet();
        foreach (var b in m.Builds.Take(MaxBuilds))
        {
            var stageList = b.IsActive && m.StageStatuses.TryGetValue(b.Key, out var sl) ? sl : [];
            int h = Px(stageList.Count > 0 ? 70 : 50);
            var row = new Rectangle(0, y, W, h);
            _hits.Add(new Hit(row, () => { Shell.Open(b.WebUrl); HideFlyout(); }));
            if (g != null)
            {
                if (hovered(row)) { using var hb = new SolidBrush(Palette.Hover); g.FillRectangle(hb, row); }
                var (color, glyph) = Palette.ForBuild(b, awaiting.Contains(b.Key));
                int bs = Px(20);
                Badge.Draw(g, new RectangleF(pad, y + (h - bs) / 2f, bs, bs), color, glyph, _frame);
                using var sep = new Pen(Palette.Border);
                g.DrawLine(sep, pad, y + h - 1, W - pad, y + h - 1);
            }

            int tx = pad + Px(32), rightW = Px(130), leftW = W - tx - rightW - pad - Px(8);
            Text(b.DefinitionName, _boldFont, new Rectangle(tx, y + Px(7), leftW, Px(20)), Palette.Text);
            Text(Fmt.BuildLine(b), Font, new Rectangle(tx, y + Px(26), leftW, Px(18)), Palette.Muted);

            string topRight, bottomRight;
            if (b.IsActive)
            {
                topRight = b.StartTime is { } s ? $"running {Fmt.Duration(DateTime.Now - s)}" : "queued";
                bottomRight = m.Stages.TryGetValue(b.Key, out var stage) ? stage : "";
            }
            else
            {
                topRight = Fmt.Ago(b.FinishTime ?? b.QueueTime);
                bottomRight = b.StartTime is { } s && b.FinishTime is { } f ? Fmt.Duration(f - s) : Palette.ResultText(b.Result);
            }
            var activeColor = awaiting.Contains(b.Key) ? Palette.Orange : Palette.Blue;
            Text(topRight, Font, new Rectangle(W - pad - rightW, y + Px(7), rightW, Px(20)), b.IsActive ? activeColor : Palette.Muted, TextFormatFlags.Right);
            Text(bottomRight, _smallFont, new Rectangle(W - pad - rightW, y + Px(26), rightW, Px(18)), Palette.Muted, TextFormatFlags.Right);
            if (g != null && stageList.Count > 0)
                DrawStages(g, stageList, new Rectangle(tx, y + Px(46), W - tx - pad, Px(16)));
            y += h;
        }

        // Footer
        y += Px(4);
        int footerH = Px(38);
        if (g != null)
        {
            using var footer = new SolidBrush(Palette.Surface);
            g.FillRectangle(footer, 0, y, W, footerH);
        }
        var updated = m.LastPoll is { } lp ? $"Updated {lp:HH:mm:ss}" : "Not updated yet";
        Text(updated, _smallFont, new Rectangle(pad, y, Px(160), footerH), Palette.Muted);

        int linkX = W - pad;
        foreach (var (label, action) in new (string, Action)[] { ("Settings", () => { HideFlyout(); SettingsRequested?.Invoke(); }), ("Refresh", () => RefreshRequested?.Invoke()) })
        {
            int lw = TextRenderer.MeasureText(label, _boldFont).Width + Px(8);
            linkX -= lw;
            var r = new Rectangle(linkX, y, lw, footerH);
            _hits.Add(new Hit(r, action));
            Text(label, _boldFont, r, hovered(r) ? Palette.Text : Palette.Blue, TextFormatFlags.HorizontalCenter);
            linkX -= Px(8);
        }
        y += footerH;

        if (g != null)
        {
            using var border = new Pen(Palette.Border);
            g.DrawRectangle(border, 0, 0, W - 1, y - 1);
        }
        return y;
    }

    /// <summary>Draws a run's stages left to right as small status badges, dropping names that don't fit.</summary>
    void DrawStages(Graphics g, IReadOnlyList<StageStatus> stages, Rectangle area)
    {
        int dot = Px(12), gap = Px(4), link = Px(14);
        var nameW = stages.Select(s => TextRenderer.MeasureText(g, s.Name, _smallFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width).ToArray();
        int TotalWidth(Func<int, bool> named) =>
            Enumerable.Range(0, stages.Count).Sum(i => dot + (named(i) ? gap + nameW[i] : 0)) + link * (stages.Count - 1);
        Func<int, bool> named = _ => true;
        if (TotalWidth(named) > area.Width) named = i => stages[i].IsCurrent;

        float cy = area.Y + area.Height / 2f;
        using var linkPen = new Pen(Palette.Border, Math.Max(1f, Px(1)));
        int x = area.X;
        for (int i = 0; i < stages.Count && x + dot <= area.Right; i++)
        {
            var s = stages[i];
            if (i > 0)
            {
                g.DrawLine(linkPen, x + Px(3), cy, x + link - Px(3), cy);
                x += link;
                if (x + dot > area.Right) break;
            }
            var (color, glyph) = Palette.ForStage(s);
            if (s.Waiting && _frame / 5 % 2 == 1) color = Palette.Red;
            Badge.Draw(g, new RectangleF(x, cy - dot / 2f, dot, dot), color, glyph, _frame);
            x += dot;
            if (!named(i)) continue;
            x += gap;
            var r = new Rectangle(x, area.Y, Math.Min(nameW[i] + Px(2), area.Right - x), area.Height);
            TextRenderer.DrawText(g, s.Name, _smallFont, r, s.State == "pending" ? Palette.Muted : Palette.Text, Line | TextFormatFlags.NoPadding);
            x += nameW[i];
        }
    }

    static string Summary(DashboardModel m)
    {
        if (m.State == TrayState.Unconfigured) return "Not configured";
        var parts = new List<string>();
        if (m.Approvals.Count > 0) parts.Add($"{m.Approvals.Count} awaiting approval");
        int running = m.Builds.Count(b => b.IsActive);
        if (running > 0) parts.Add($"{running} running");
        if (parts.Count == 0) parts.Add(m.State == TrayState.Error ? "Can't reach the server" : "All quiet");
        return string.Join(" · ", parts);
    }
}
