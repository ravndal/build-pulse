namespace DevOpsWatcher;

public sealed record DashboardModel(
    IReadOnlyList<ServerConfig> Servers,
    IReadOnlyList<BuildInfo> Builds,
    IReadOnlyList<ApprovalInfo> Approvals,
    IReadOnlyDictionary<string, string> Stages,
    IReadOnlyDictionary<string, IReadOnlyList<StageStatus>> StageStatuses,
    DateTime? LastPoll,
    string? Error,
    TrayState State,
    UpdateStatus Update,
    IReadOnlyCollection<string> CollapsedPanels)
{
    public static readonly DashboardModel Empty = new([], [], [], new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<StageStatus>>(),
        null, null, TrayState.Unconfigured, new UpdateStatus(UpdateStage.Unsupported), []);
}

/// <summary>
/// Compact, owner-drawn flyout. Overview: pending approvals, then one panel per repo/project with the latest run of
/// each tracked pipeline (collapsible to one status icon per pipeline). History: every polled run, newest first, paged, optionally for one repo/project.
/// </summary>
sealed class DashboardForm : PopupForm
{
    const int PageSize = 10;

    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public event Action<ApprovalInfo>? ApprovalClicked;
    public event Action? UpdateClicked;
    /// <summary>Raised with every collapsed panel key after the user collapses or expands one.</summary>
    public event Action<IReadOnlyCollection<string>>? CollapsedChanged;

    enum Tab { Overview, History }

    /// <param name="Tip">Shown as a tooltip while hovered.</param>
    sealed record Hit(Rectangle Rect, Action OnClick, string? Tip = null);

    /// <summary>A repo/project on one server: the unit of a panel and of the history filter.</summary>
    sealed record Group(ServerConfig Server, string Project, List<TrackedPipeline> Pipelines)
    {
        public string Key => $"{Server.Id}|{Project.ToLowerInvariant()}";
        public bool Contains(BuildInfo b) => b.ServerId == Server.Id && b.Project.Equals(Project, StringComparison.OrdinalIgnoreCase);
    }

    readonly List<Hit> _hits = [];
    readonly Font _titleFont = new("Segoe UI Semibold", 12f);
    readonly Font _boldFont = new("Segoe UI Semibold", 9.5f);
    readonly Font _smallFont = new("Segoe UI", 8.25f);
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 90 };
    readonly ToolTip _tip = new();
    DashboardModel _model = DashboardModel.Empty;
    /// <summary><see cref="Group.Key"/>s of panels shown as status icons only.</summary>
    HashSet<string> _collapsed = [];
    int _hover = -1, _frame;
    DateTime _hiddenAt;

    Tab _tab = Tab.Overview;
    /// <summary><see cref="Group.Key"/> the history is limited to, or null for all.</summary>
    string? _historyGroup;
    int _historyPage;
    int _scroll, _contentHeight, _viewportHeight, _maxHeight = 2000;
    bool _anchorBottom = true;

    const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;

    public DashboardForm()
    {
        Width = Px(440);
        KeyPreview = true;
        _anim.Tick += (_, _) => { _frame++; Invalidate(); };
    }

    /// <summary>True right after the flyout auto-hid, so the tray click that caused it doesn't re-open it.</summary>
    public bool RecentlyHidden => (DateTime.Now - _hiddenAt).TotalMilliseconds < 400;

    protected override bool ShowWithoutActivation => false;

    public void SetModel(DashboardModel model)
    {
        _model = model;
        _collapsed = model.CollapsedPanels.ToHashSet();
        if (Visible) Relayout();
    }

    /// <summary>Resizes to the content, keeping the edge next to the taskbar in place.</summary>
    void Relayout()
    {
        int top = Top, bottom = Bottom;
        Height = Render(null);
        Top = _anchorBottom ? bottom - Height : top;
        Invalidate();
    }

    public void ShowNearTray()
    {
        _tab = Tab.Overview;
        _scroll = 0;
        var screen = Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;
        var bounds = screen.Bounds;
        int gap = Px(10);
        _maxHeight = area.Height - gap * 2;
        Height = Render(null);
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
        _anchorBottom = !(area.Top > bounds.Top);
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
        _tip.Hide(this);
        Hide();
    }

    void ShowTab(Tab tab, string? historyGroup = null)
    {
        _tab = tab;
        _historyGroup = historyGroup;
        _historyPage = 0;
        _scroll = 0;
        Relayout();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int max = Math.Max(0, _contentHeight - _viewportHeight);
        if (max == 0) return;
        _scroll = Math.Clamp(_scroll - e.Delta / 120 * Px(48), 0, max);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = _hits.FindIndex(h => h.Rect.Contains(e.Location));
        if (index == _hover) return;
        _hover = index;
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        if (index >= 0 && _hits[index].Tip is { } tip) _tip.Show(tip, this, e.X + Px(12), e.Y + Px(18));
        else _tip.Hide(this);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _tip.Hide(this);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left)
            _hits.FirstOrDefault(h => h.Rect.Contains(e.Location))?.OnClick();
    }

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics);

    /// <summary>
    /// Draws the flyout (or only measures it when <paramref name="g"/> is null) and returns its height.
    /// Header, tabs and footer stay put; the part between them scrolls when it's taller than the screen allows.
    /// </summary>
    int Render(Graphics? g)
    {
        _hits.Clear();
        var m = _model;
        int W = Width, pad = Px(16), y = 0;
        void Text(string s, Font f, Rectangle r, Color c, TextFormatFlags extra = 0) => Draw(g, s, f, r, c, extra);

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
        int logoSize = Px(36), titleX = pad + logoSize + Px(10);
        if (g != null)
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(AppLogo(logoSize), new Rectangle(pad, (headerH - logoSize) / 2, logoSize, logoSize));
        }
        Text("Pipelines", _titleFont, new Rectangle(titleX, Px(10), W - titleX - pad * 2, Px(24)), Palette.Text);
        Text(Summary(m), Font, new Rectangle(titleX, Px(34), W - titleX - pad * 3, Px(18)), Palette.Muted);
        y += headerH;

        // Tabs
        int tabH = Px(30), tabX = pad;
        foreach (var (tab, label) in new[] { (Tab.Overview, "Overview"), (Tab.History, "History") })
        {
            int tw = TextRenderer.MeasureText(label, _boldFont).Width + Px(12);
            var r = new Rectangle(tabX, y, tw, tabH);
            AddHit(r, () => ShowTab(tab));
            bool active = _tab == tab;
            Text(label, _boldFont, r, active || Hovered(r) ? Palette.Text : Palette.Muted, TextFormatFlags.HorizontalCenter);
            if (g != null && active) { using var line = new SolidBrush(Palette.Blue); g.FillRectangle(line, r.X + Px(4), r.Bottom - Px(3), r.Width - Px(8), Px(3)); }
            tabX += tw + Px(6);
        }
        y += tabH;
        if (g != null) { using var sep = new Pen(Palette.Border); g.DrawLine(sep, 0, y - 1, W, y - 1); }

        if (m.Error != null)
        {
            int h = Px(30);
            if (g != null) { using var bg = new SolidBrush(Palette.ErrorTint); g.FillRectangle(bg, 0, y, W, h); }
            Text("⚠ " + m.Error, Font, new Rectangle(pad, y, W - pad * 2, h), Palette.Red);
            y += h;
        }

        // Scrolling content
        int footerH = Px(38);
        int contentTop = y;
        _viewportHeight = Math.Max(Px(120), _maxHeight - contentTop - footerH);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _contentHeight - _viewportHeight));
        var viewport = new Rectangle(0, contentTop, W, _viewportHeight);
        var clipState = g?.Save();
        g?.SetClip(viewport);
        int end = _tab == Tab.Overview
            ? RenderOverview(g, contentTop - _scroll, viewport)
            : RenderHistory(g, contentTop - _scroll, viewport);
        if (g != null && clipState != null) g.Restore(clipState);
        _contentHeight = end + _scroll - contentTop;
        int visible = Math.Min(_contentHeight, _viewportHeight);
        y = contentTop + visible;

        if (g != null && _contentHeight > _viewportHeight)
        {
            // Scroll thumb
            int track = visible - Px(8), thumb = Math.Max(Px(24), track * visible / _contentHeight);
            int thumbY = contentTop + Px(4) + (track - thumb) * _scroll / Math.Max(1, _contentHeight - visible);
            using var bar = new SolidBrush(Palette.Border);
            g.FillRectangle(bar, W - Px(6), thumbY, Px(3), thumb);
        }

        // Footer
        if (g != null)
        {
            using var footer = new SolidBrush(Palette.Surface);
            g.FillRectangle(footer, 0, y, W, footerH);
        }
        var updated = m.LastPoll is { } lp ? $"Updated {lp:HH:mm:ss}" : "Not updated yet";
        int updatedW = TextRenderer.MeasureText(updated, _smallFont).Width;
        Text(updated, _smallFont, new Rectangle(pad, y, updatedW + Px(4), footerH), Palette.Muted);

        // Update button: checks on click, and only installs when clicked again on "Update to vX".
        var update = m.Update;
        var updateFont = update.Stage is UpdateStage.Available or UpdateStage.Installing ? _boldFont : _smallFont;
        var updateRect = new Rectangle(pad + updatedW + Px(14), y, TextRenderer.MeasureText(update.Label, updateFont).Width + Px(8), footerH);
        if (update.Clickable) AddHit(updateRect, () => UpdateClicked?.Invoke());
        var updateColor = update.Stage switch
        {
            _ when update.Clickable && Hovered(updateRect) => Palette.Text,
            UpdateStage.Available => Palette.Green,
            UpdateStage.Failed => Palette.Red,
            _ when !update.Clickable => Palette.Muted,
            _ => Palette.Blue,
        };
        Text(update.Label, updateFont, updateRect, updateColor);

        int linkX = W - pad;
        foreach (var (label, action) in new (string, Action)[] { ("Settings", () => { HideFlyout(); SettingsRequested?.Invoke(); }), ("Refresh", () => RefreshRequested?.Invoke()) })
        {
            int lw = TextRenderer.MeasureText(label, _boldFont).Width + Px(8);
            linkX -= lw;
            var r = new Rectangle(linkX, y, lw, footerH);
            AddHit(r, action);
            Text(label, _boldFont, r, Hovered(r) ? Palette.Text : Palette.Blue, TextFormatFlags.HorizontalCenter);
            linkX -= Px(8);
        }
        y += footerH;

        if (g != null)
        {
            using var border = new Pen(Palette.Border);
            g.DrawRectangle(border, 0, 0, W - 1, y - 1);
        }
        _hoverRects = _hits.Select(h => h.Rect).ToList();
        return y;

        void AddHit(Rectangle r, Action onClick) => _hits.Add(new Hit(r, onClick));
    }

    Bitmap? _appLogo;
    int _appLogoSize;

    /// <summary>The app icon at the closest size the .ico has, cached until the DPI asks for another size.</summary>
    Bitmap AppLogo(int size)
    {
        if (_appLogo == null || _appLogoSize != size)
        {
            _appLogo?.Dispose();
            _appLogoSize = size;
            using var icon = new Icon(IconFactory.App, size, size);
            _appLogo = icon.ToBitmap();
        }
        return _appLogo;
    }

    /// <summary>Hit rectangles of the last paint, so hover highlighting survives the hit list being rebuilt mid-paint.</summary>
    List<Rectangle> _hoverRects = [];

    bool Hovered(Rectangle r) => _hover >= 0 && _hover < _hoverRects.Count && _hoverRects[_hover] == r;

    /// <summary>Registers a clickable area inside the scrolling viewport (clipped to it).</summary>
    void AddContentHit(Rectangle r, Rectangle viewport, Action onClick)
    {
        var visible = Rectangle.Intersect(r, viewport);
        if (!visible.IsEmpty) _hits.Add(new Hit(visible, onClick));
    }

    bool HoveredContent(Rectangle r, Rectangle viewport) => Hovered(Rectangle.Intersect(r, viewport));

    void Draw(Graphics? g, string s, Font f, Rectangle r, Color c, TextFormatFlags extra = 0)
    {
        if (g != null) TextRenderer.DrawText(g, s, f, r, c, Line | extra);
    }

    int RenderOverview(Graphics? g, int y, Rectangle viewport)
    {
        var m = _model;
        int W = Width, pad = Px(16);

        foreach (var a in m.Approvals)
        {
            int h = Px(54);
            var row = new Rectangle(0, y, W, h);
            AddContentHit(row, viewport, () => ApprovalClicked?.Invoke(a));
            if (g != null)
            {
                using var bg = new SolidBrush(HoveredContent(row, viewport) ? ControlPaint.Light(Palette.ApprovalTint, 0.15f) : Palette.ApprovalTint);
                g.FillRectangle(bg, row);
                using var bar = new SolidBrush(Palette.Orange);
                g.FillRectangle(bar, 0, y, Px(4), h);
                int b = Px(22);
                Badge.Draw(g, new RectangleF(pad, y + (h - b) / 2f, b, b), _frame / 5 % 2 == 1 ? Palette.Red : Palette.Orange, Glyph.Bang);
            }
            int tx = pad + Px(34), rightW = Px(80);
            Draw(g, $"Approval needed · {a.PipelineName}", _boldFont, new Rectangle(tx, y + Px(8), W - tx - rightW - pad, Px(20)), Palette.Text);
            var sub = string.Join(" · ", new[] { a.Project, a.RunName != "" ? $"#{a.RunName}" : "", a.StageName ?? "", Fmt.Ago(a.CreatedOn) }.Where(s => s != ""));
            Draw(g, sub, Font, new Rectangle(tx, y + Px(28), W - tx - rightW - pad, Px(18)), Palette.Muted);
            Draw(g, "Review ›", _boldFont, new Rectangle(W - pad - rightW, y, rightW, h), Palette.Orange, TextFormatFlags.Right);
            y += h;
        }

        var groups = Groups(m);
        if (groups.Count == 0)
        {
            int h = Px(70);
            var msg = m.Servers.Count == 0 ? "Open Settings to add a server." : "Open Settings to pick pipelines to track.";
            Draw(g, msg, Font, new Rectangle(pad, y, W - pad * 2, h), Palette.Muted, TextFormatFlags.HorizontalCenter);
            return y + h;
        }

        bool manyServers = m.Servers.Count > 1;
        var awaiting = m.Approvals.Select(a => a.RunKey).ToHashSet();
        y += Px(10);
        foreach (var group in groups)
        {
            var groupBuilds = m.Builds.Where(group.Contains).ToList();
            bool collapsed = _collapsed.Contains(group.Key);
            int panelTop = y, headerH = Px(34), panelX = pad / 2, panelW = W - pad;

            // Panel header: chevron, provider mark, repo/project, server (when there are several),
            // status icons (collapsed), History link. Clicking the header collapses or expands the panel.
            int chevron = Px(14), logo = Px(16), hx = panelX + Px(8);
            const string historyLabel = "History ›";
            int historyW = TextRenderer.MeasureText(historyLabel, _boldFont).Width + Px(8);
            var historyRect = new Rectangle(panelX + panelW - Px(8) - historyW, y, historyW, headerH);
            int nameX = hx + chevron + Px(4) + logo + Px(8), nameW = historyRect.X - nameX - Px(8);
            int projectW = Math.Min(nameW, TextRenderer.MeasureText(group.Project, _boldFont).Width + Px(4));

            // Collapsed: one status icon per pipeline, in the header when they fit, otherwise in rows under it.
            int icon = Px(16), iconGap = Px(8), iconsW = group.Pipelines.Count * (icon + iconGap) - iconGap;
            int iconsRight = historyRect.X - Px(10);
            bool iconsInline = collapsed && iconsW <= iconsRight - (nameX + projectW + Px(10));
            int perRow = Math.Max(1, (panelW - Px(20) + iconGap) / (icon + iconGap));
            int iconRows = (group.Pipelines.Count + perRow - 1) / perRow;
            int bodyH = !collapsed ? group.Pipelines.Sum(p => RowHeight(Latest(groupBuilds, p)))
                : iconsInline ? 0
                : iconRows * (icon + Px(10)) + Px(8);
            var panel = new Rectangle(panelX, panelTop, panelW, headerH + bodyH);
            var headerRect = new Rectangle(panel.X + 1, y + 1, panel.Width - 2, headerH - 1);

            // History link and icons are hit-tested before the header so they win over its collapse toggle.
            AddContentHit(historyRect, viewport, () => ShowTab(Tab.History, group.Key));
            var icons = new List<(Rectangle Rect, BuildInfo? Build)>();
            if (collapsed)
            {
                for (int i = 0; i < group.Pipelines.Count; i++)
                {
                    var pipeline = group.Pipelines[i];
                    var r = iconsInline
                        ? new Rectangle(iconsRight - iconsW + i * (icon + iconGap), y + (headerH - icon) / 2, icon, icon)
                        : new Rectangle(panel.X + Px(10) + i % perRow * (icon + iconGap), y + headerH + Px(9) + i / perRow * (icon + Px(10)), icon, icon);
                    var build = Latest(groupBuilds, pipeline);
                    icons.Add((r, build));
                    var hit = Rectangle.Intersect(Rectangle.Inflate(r, Px(4), Px(4)), viewport);
                    var tip = $"{pipeline.Name}\n{(build == null ? "No runs yet" : IconTip(build, awaiting.Contains(build.Key)))}";
                    if (!hit.IsEmpty) _hits.Add(new Hit(hit, () => { if (build != null) { Shell.Open(build.WebUrl); HideFlyout(); } }, tip));
                }
            }
            AddContentHit(headerRect, viewport, () => ToggleCollapsed(group.Key));

            if (g != null)
            {
                using var bg = new SolidBrush(Palette.Surface);
                g.FillRectangle(bg, panel);
                if (HoveredContent(headerRect, viewport)) { using var hb = new SolidBrush(Palette.Hover); g.FillRectangle(hb, headerRect); }
                using var border = new Pen(Palette.Border);
                g.DrawRectangle(border, panel.X, panel.Y, panel.Width - 1, panel.Height - 1);
                DrawChevron(g, new RectangleF(hx, y + (headerH - chevron) / 2f, chevron, chevron), collapsed);
                ProviderLogo.Draw(g, new RectangleF(hx + chevron + Px(4), y + (headerH - logo) / 2f, logo, logo), group.Server.Kind);
            }
            Draw(g, group.Project, _boldFont, new Rectangle(nameX, y, projectW, headerH), Palette.Text);
            int serverX = nameX + projectW + Px(4), serverRight = iconsInline ? iconsRight - iconsW - Px(8) : nameX + nameW;
            if (manyServers && serverRight - serverX > Px(40))
                Draw(g, group.Server.DisplayName, _smallFont, new Rectangle(serverX, y, serverRight - serverX, headerH), Palette.Muted);
            Draw(g, historyLabel, _boldFont, historyRect, HoveredContent(historyRect, viewport) ? Palette.Text : Palette.Blue, TextFormatFlags.Right);
            if (g != null)
                foreach (var (r, build) in icons)
                {
                    var around = Rectangle.Inflate(r, Px(4), Px(4));
                    if (HoveredContent(around, viewport)) { using var hb = new SolidBrush(Palette.Border); g.FillEllipse(hb, around); }
                    var (color, glyph) = build == null ? (Palette.Gray, Glyph.Ring) : Palette.ForBuild(build, awaiting.Contains(build.Key));
                    if (build != null && awaiting.Contains(build.Key) && _frame / 5 % 2 == 1) color = Palette.Red;
                    Badge.Draw(g, r, color, glyph, _frame);
                }
            y += headerH;

            if (collapsed)
            {
                if (!iconsInline && g != null) { using var sep = new Pen(Palette.Border); g.DrawLine(sep, panel.X + 1, y, panel.Right - 2, y); }
            }
            else
            {
                foreach (var pipeline in group.Pipelines)
                {
                    var build = Latest(groupBuilds, pipeline);
                    int h = RowHeight(build);
                    var row = new Rectangle(panel.X + 1, y, panel.Width - 2, h);
                    if (g != null) { using var sep = new Pen(Palette.Border); g.DrawLine(sep, row.X, y, row.Right, y); }
                    if (build == null)
                    {
                        int tx0 = panel.X + Px(42);
                        Draw(g, pipeline.Name, _boldFont, new Rectangle(tx0, y, row.Right - tx0 - Px(110), h), Palette.Muted);
                        Draw(g, "No runs yet", _smallFont, new Rectangle(row.Right - Px(110), y, Px(100), h), Palette.Muted, TextFormatFlags.Right);
                    }
                    else
                    {
                        DrawBuildRow(g, build, row, viewport, awaiting.Contains(build.Key), subtitle: Fmt.BuildLine(build), logo: null);
                    }
                    y += h;
                }
            }
            y = panel.Bottom + Px(10);
        }
        return y;
    }

    void ToggleCollapsed(string key)
    {
        if (!_collapsed.Remove(key)) _collapsed.Add(key);
        CollapsedChanged?.Invoke(_collapsed.ToList());
        Relayout();
    }

    /// <summary>A small ▸ (collapsed) or ▾ (expanded) triangle.</summary>
    static void DrawChevron(Graphics g, RectangleF r, bool collapsed)
    {
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = r.Width * 0.24f;
        PointF[] points = collapsed
            ? [new(cx - s * 0.6f, cy - s), new(cx + s * 0.9f, cy), new(cx - s * 0.6f, cy + s)]
            : [new(cx - s, cy - s * 0.6f), new(cx + s, cy - s * 0.6f), new(cx, cy + s * 0.9f)];
        var state = g.Save();
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Palette.Muted);
        g.FillPolygon(brush, points);
        g.Restore(state);
    }

    /// <summary>Second tooltip line for a collapsed panel's status icon.</summary>
    string IconTip(BuildInfo b, bool awaitingApproval)
    {
        if (awaitingApproval) return $"#{b.Number} · waiting for approval";
        if (b.IsActive)
            return _model.Stages.TryGetValue(b.Key, out var stage) ? $"#{b.Number} · {stage}" : $"#{b.Number} · {(b.StartTime != null ? "running" : "queued")}";
        return $"#{b.Number} · {Palette.ResultText(b.Result)} · {Fmt.Ago(b.FinishTime ?? b.QueueTime)}";
    }

    int RenderHistory(Graphics? g, int y, Rectangle viewport)
    {
        var m = _model;
        int W = Width, pad = Px(16);
        var groups = Groups(m);
        var filter = groups.FirstOrDefault(gr => gr.Key == _historyGroup);
        var builds = filter == null ? m.Builds.ToList() : m.Builds.Where(filter.Contains).ToList();

        // Filter line: which repo/project, with a way back to all.
        int filterH = Px(36);
        if (filter != null)
        {
            int logo = Px(16), x = pad;
            if (g != null) ProviderLogo.Draw(g, new RectangleF(x, y + (filterH - logo) / 2f, logo, logo), filter.Server.Kind);
            x += logo + Px(8);
            const string allLabel = "Show all";
            int allW = TextRenderer.MeasureText(allLabel, _boldFont).Width + Px(8);
            var allRect = new Rectangle(W - pad - allW, y, allW, filterH);
            AddContentHit(allRect, viewport, () => ShowTab(Tab.History));
            Draw(g, m.Servers.Count > 1 ? $"{filter.Project}  ·  {filter.Server.DisplayName}" : filter.Project,
                _boldFont, new Rectangle(x, y, allRect.X - x - Px(8), filterH), Palette.Text);
            Draw(g, allLabel, _boldFont, allRect, HoveredContent(allRect, viewport) ? Palette.Text : Palette.Blue, TextFormatFlags.Right);
        }
        else
        {
            Draw(g, "All repositories and projects", _boldFont, new Rectangle(pad, y, W - pad * 2, filterH), Palette.Text);
        }
        y += filterH;

        if (builds.Count == 0)
        {
            int h = Px(60);
            Draw(g, "No runs yet.", Font, new Rectangle(pad, y, W - pad * 2, h), Palette.Muted, TextFormatFlags.HorizontalCenter);
            return y + h;
        }

        int pages = (builds.Count + PageSize - 1) / PageSize;
        _historyPage = Math.Clamp(_historyPage, 0, pages - 1);
        var awaiting = m.Approvals.Select(a => a.RunKey).ToHashSet();
        var kinds = m.Servers.ToDictionary(s => s.Id, s => s.Kind);
        foreach (var b in builds.Skip(_historyPage * PageSize).Take(PageSize))
        {
            int h = RowHeight(b);
            var row = new Rectangle(0, y, W, h);
            if (g != null) { using var sep = new Pen(Palette.Border); g.DrawLine(sep, pad, y, W - pad, y); }
            // Without a filter, name the repo/project (with its provider mark) so runs can be told apart.
            var subtitle = filter == null ? $"{ShortProject(b.Project)} · {Fmt.BuildLine(b)}" : Fmt.BuildLine(b);
            ServerKind? logo = filter == null && kinds.TryGetValue(b.ServerId, out var kind) ? kind : null;
            DrawBuildRow(g, b, row, viewport, awaiting.Contains(b.Key), subtitle, logo);
            y += h;
        }

        // Pager
        int pagerH = Px(40);
        if (g != null) { using var sep = new Pen(Palette.Border); g.DrawLine(sep, pad, y, W - pad, y); }
        Draw(g, $"Page {_historyPage + 1} of {pages} · {builds.Count} runs", _smallFont, new Rectangle(pad, y, W - pad * 2, pagerH), Palette.Muted, TextFormatFlags.HorizontalCenter);
        void PagerLink(string label, bool enabled, bool right, int delta)
        {
            int lw = TextRenderer.MeasureText(label, _boldFont).Width + Px(8);
            var r = new Rectangle(right ? W - pad - lw : pad, y, lw, pagerH);
            if (enabled) AddContentHit(r, viewport, () => { _historyPage += delta; _scroll = 0; Relayout(); });
            var color = !enabled ? Palette.Border : HoveredContent(r, viewport) ? Palette.Text : Palette.Blue;
            Draw(g, label, _boldFont, r, color, right ? TextFormatFlags.Right : 0);
        }
        PagerLink("‹ Newer", _historyPage > 0, right: false, -1);
        PagerLink("Older ›", _historyPage < pages - 1, right: true, +1);
        return y + pagerH;
    }

    int RowHeight(BuildInfo? b) =>
        Px(b != null && b.IsActive && _model.StageStatuses.TryGetValue(b.Key, out var s) && s.Count > 0 ? 66 : 48);

    /// <summary>One run: status badge, pipeline name, subtitle, timing on the right, stage dots while it runs.</summary>
    void DrawBuildRow(Graphics? g, BuildInfo b, Rectangle row, Rectangle viewport, bool awaitingApproval, string subtitle, ServerKind? logo)
    {
        var m = _model;
        int pad = Px(10) + (row.X == 0 ? Px(6) : 0), y = row.Y;
        var stageList = b.IsActive && m.StageStatuses.TryGetValue(b.Key, out var sl) ? sl : [];
        AddContentHit(row, viewport, () => { Shell.Open(b.WebUrl); HideFlyout(); });
        if (g != null)
        {
            if (HoveredContent(row, viewport)) { using var hb = new SolidBrush(Palette.Hover); g.FillRectangle(hb, row); }
            var (color, glyph) = Palette.ForBuild(b, awaitingApproval);
            int bs = Px(20);
            Badge.Draw(g, new RectangleF(row.X + pad, y + Px(14), bs, bs), color, glyph, _frame);
        }

        int tx = row.X + pad + Px(32), rightW = Px(124), leftW = row.Right - tx - rightW - Px(14);
        Draw(g, b.DefinitionName, _boldFont, new Rectangle(tx, y + Px(6), leftW, Px(20)), Palette.Text);
        int sx = tx;
        if (logo is { } kind && g != null)
        {
            int ls = Px(12);
            ProviderLogo.Draw(g, new RectangleF(sx, y + Px(25) + (Px(18) - ls) / 2f, ls, ls), kind);
            sx += ls + Px(5);
        }
        Draw(g, subtitle, Font, new Rectangle(sx, y + Px(25), leftW - (sx - tx), Px(18)), Palette.Muted);

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
        var activeColor = awaitingApproval ? Palette.Orange : Palette.Blue;
        int rx = row.Right - Px(14) - rightW;
        Draw(g, topRight, Font, new Rectangle(rx, y + Px(6), rightW, Px(20)), b.IsActive ? activeColor : Palette.Muted, TextFormatFlags.Right);
        Draw(g, bottomRight, _smallFont, new Rectangle(rx, y + Px(25), rightW, Px(18)), Palette.Muted, TextFormatFlags.Right);
        if (g != null && stageList.Count > 0)
            DrawStages(g, stageList, new Rectangle(tx, y + Px(44), row.Right - tx - Px(14), Px(16)));
    }

    /// <summary>"owner/repo" → "repo"; the provider mark and filter already say where it lives.</summary>
    static string ShortProject(string project) => project[(project.LastIndexOf('/') + 1)..];

    /// <summary>Newest run of a tracked pipeline (builds are sorted newest first).</summary>
    static BuildInfo? Latest(List<BuildInfo> builds, TrackedPipeline pipeline) =>
        builds.FirstOrDefault(b => b.DefinitionId == pipeline.DefinitionId);

    /// <summary>Tracked pipelines grouped by server and repo/project, in settings order.</summary>
    static List<Group> Groups(DashboardModel m) =>
        m.Servers
            .SelectMany(s => s.Pipelines
                .GroupBy(p => p.Project, StringComparer.OrdinalIgnoreCase)
                .Select(grp => new Group(s, grp.Key, grp.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList())))
            .ToList();

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
        if (parts.Count == 0) parts.Add(m.State == TrayState.Error ? "Can't reach a server" : "All quiet");
        return string.Join(" · ", parts);
    }
}
