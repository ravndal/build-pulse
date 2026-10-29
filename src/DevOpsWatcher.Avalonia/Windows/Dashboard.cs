using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

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
    public static readonly DashboardModel Empty = new([], [], [], new Dictionary<string, string>(),
        new Dictionary<string, IReadOnlyList<StageStatus>>(), null, null, TrayState.Unconfigured, new UpdateStatus(UpdateStage.Unsupported), []);
}

/// <summary>
/// The flyout, as Avalonia controls rather than owner-drawn: header, Overview (approvals, then one collapsible panel
/// per repo/project with the latest run of each tracked pipeline) or History (all runs, paged), and a footer.
/// </summary>
sealed class DashboardView : UserControl
{
    const int PageSize = 10;
    public const double FlyoutWidth = 440;

    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    /// <summary>A link was followed; the window hides itself.</summary>
    public event Action? Navigated;
    public event Action<IReadOnlyCollection<string>>? CollapsedChanged;
    public event Action<ApprovalInfo>? ApprovalClicked;
    public event Action? UpdateClicked;

    public enum Tab { Overview, History }

    /// <summary>A repo/project on one server: the unit of a panel and of the history filter.</summary>
    sealed record Group(ServerConfig Server, string Project, List<TrackedPipeline> Pipelines)
    {
        public string Key => $"{Server.Id}|{Project.ToLowerInvariant()}";
        public bool Contains(BuildInfo b) => b.ServerId == Server.Id && b.Project.Equals(Project, StringComparison.OrdinalIgnoreCase);
    }

    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    readonly DockPanel _root = new() { Background = Palette.Background };
    DashboardModel _model = DashboardModel.Empty;
    HashSet<string> _collapsed = [];
    Tab _tab = Tab.Overview;
    string? _historyGroup;
    int _historyPage;

    public DashboardView()
    {
        Width = FlyoutWidth;
        Styles.Add(new Style(x => x.OfType<Border>().Class("row").Class(":pointerover")) { Setters = { new Setter(Border.BackgroundProperty, Palette.Hover) } });
        Styles.Add(new Style(x => x.OfType<TextBlock>().Class("link").Class(":pointerover")) { Setters = { new Setter(TextBlock.ForegroundProperty, Palette.Text) } });
        Content = new Border
        {
            BorderBrush = Palette.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = _root,
        };
        Rebuild();
    }

    /// <summary>Upper bound for the scrolling middle, so the flyout never outgrows the screen.</summary>
    public double MaxContentHeight { get => _scroll.MaxHeight; set => _scroll.MaxHeight = value; }

    public void SetModel(DashboardModel model)
    {
        _model = model;
        _collapsed = model.CollapsedPanels.ToHashSet();
        Rebuild();
    }

    public void ShowTab(Tab tab, string? historyGroup = null)
    {
        _tab = tab;
        _historyGroup = historyGroup;
        _historyPage = 0;
        _scroll.Offset = default;
        Rebuild();
    }

    void Rebuild()
    {
        var m = _model;
        _root.Children.Clear();

        var header = Header(m);
        DockPanel.SetDock(header, Dock.Top);
        _root.Children.Add(header);

        var tabs = Tabs();
        DockPanel.SetDock(tabs, Dock.Top);
        _root.Children.Add(tabs);

        if (m.Error != null)
        {
            var error = new Border
            {
                Background = Palette.ErrorTint,
                Padding = new Thickness(16, 7),
                Child = Label("⚠ " + m.Error, 13, new SolidColorBrush(Palette.Red)),
            };
            DockPanel.SetDock(error, Dock.Top);
            _root.Children.Add(error);
        }

        var footer = Footer(m);
        DockPanel.SetDock(footer, Dock.Bottom);
        _root.Children.Add(footer);

        // The scroll viewer survives rebuilds, so a poll doesn't jump the list back to the top.
        _scroll.Content = _tab == Tab.Overview ? Overview(m) : History(m);
        _root.Children.Add(_scroll);
    }

    Control Header(DashboardModel m)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 60, Margin = new Thickness(16, 0) };
        using (var png = AssetLoader.Open(new Uri("avares://DevOpsWatcher/Assets/app.png")))
            grid.Children.Add(new Image { Source = new Bitmap(png), Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center });
        var titles = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        titles.Children.Add(Label("Pipelines", 16, Palette.Text, FontWeight.SemiBold));
        titles.Children.Add(Label(Summary(m), 12.5, Palette.Muted));
        Grid.SetColumn(titles, 1);
        grid.Children.Add(titles);
        var (color, glyph) = Palette.ForState(m.State);
        var badge = new Badge(color, glyph, 26, flash: m.State == TrayState.Approval) { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);
        return grid;
    }

    Control Tabs()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0), Spacing = 6 };
        foreach (var (tab, label) in new[] { (Tab.Overview, "Overview"), (Tab.History, "History") })
        {
            bool active = _tab == tab;
            var text = Label(label, 13, active ? Palette.Text : Palette.Muted, FontWeight.SemiBold);
            text.Classes.Add("link");
            row.Children.Add(Clickable(new Border
            {
                Padding = new Thickness(6, 6, 6, 5),
                BorderThickness = new Thickness(0, 0, 0, 3),
                BorderBrush = active ? Palette.Link : Brushes.Transparent,
                Child = text,
            }, () => ShowTab(tab)));
        }
        return new Border { BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
    }

    Control Footer(DashboardModel m)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Height = 38, Margin = new Thickness(16, 0) };
        grid.Children.Add(Label(m.LastPoll is { } lp ? $"Updated {lp:HH:mm:ss}" : "Not updated yet", 11.5, Palette.Muted));

        // Update button: checks on click, and only installs when clicked again on "Update to vX".
        var update = m.Update;
        var updateColor = update.Stage switch
        {
            UpdateStage.Available => new SolidColorBrush(Palette.Green),
            UpdateStage.Failed => new SolidColorBrush(Palette.Red),
            _ when !update.Clickable => Palette.Muted,
            _ => Palette.Link,
        };
        bool prominent = update.Stage is UpdateStage.Available or UpdateStage.Installing;
        var updateLabel = Label(update.Label, prominent ? 12.5 : 11.5, updateColor, prominent ? FontWeight.SemiBold : FontWeight.Normal);
        updateLabel.Margin = new Thickness(14, 0, 8, 0);
        if (update.Clickable)
        {
            updateLabel.Classes.Add("link");
            Clickable(updateLabel, () => UpdateClicked?.Invoke());
        }
        Grid.SetColumn(updateLabel, 1);
        grid.Children.Add(updateLabel);

        var refresh = Link("Refresh", () => RefreshRequested?.Invoke());
        refresh.Margin = new Thickness(0, 0, 14, 0);
        Grid.SetColumn(refresh, 2);
        grid.Children.Add(refresh);
        var settings = Link("Settings", () => SettingsRequested?.Invoke());
        Grid.SetColumn(settings, 3);
        grid.Children.Add(settings);
        return new Border { Background = Palette.Surface, BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = grid };
    }

    Control Overview(DashboardModel m)
    {
        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var a in m.Approvals) list.Children.Add(ApprovalRow(a));

        var groups = Groups(m);
        if (groups.Count == 0)
        {
            var msg = m.Servers.Count == 0 ? "Open Settings to add a server." : "Open Settings to pick pipelines to track.";
            list.Children.Add(Centered(msg, 70));
            return list;
        }

        var awaiting = m.Approvals.Select(a => a.RunKey).ToHashSet();
        foreach (var group in groups) list.Children.Add(Panel(m, group, awaiting));
        return list;
    }

    Control ApprovalRow(ApprovalInfo a)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 54, Margin = new Thickness(16, 0) };
        grid.Children.Add(new Badge(Palette.Orange, Glyph.Bang, 22, flash: true) { VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(Label($"Approval needed · {a.PipelineName}", 13, Palette.Text, FontWeight.SemiBold));
        text.Children.Add(Label(string.Join(" · ", new[] { a.Project, a.RunName != "" ? $"#{a.RunName}" : "", a.StageName ?? "", Fmt.Ago(a.CreatedOn) }.Where(s => s != "")), 12.5, Palette.Muted));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var review = Label("Review ›", 13, new SolidColorBrush(Palette.Orange), FontWeight.SemiBold);
        Grid.SetColumn(review, 2);
        grid.Children.Add(review);
        return Clickable(new Border
        {
            Background = Palette.ApprovalTint,
            BorderBrush = new SolidColorBrush(Palette.Orange),
            BorderThickness = new Thickness(4, 0, 0, 0),
            Child = grid,
        }, () => ApprovalClicked?.Invoke(a));
    }

    Control Panel(DashboardModel m, Group group, HashSet<string> awaiting)
    {
        var groupBuilds = m.Builds.Where(group.Contains).ToList();
        bool collapsed = _collapsed.Contains(group.Key);

        // Header: chevron, provider mark, repo/project, server (when there are several), status icons (collapsed), History link.
        var header = new DockPanel { Height = 34, Margin = new Thickness(8, 0) };
        var history = Link("History ›", () => ShowTab(Tab.History, group.Key));
        DockPanel.SetDock(history, Dock.Right);
        header.Children.Add(history);

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new Path
        {
            Data = Geometry.Parse(collapsed ? "M0,0 L5,4 L0,8 Z" : "M0,0 L8,0 L4,5 Z"),
            Fill = Palette.Muted,
            Width = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        left.Children.Add(ProviderLogo.Create(group.Server.Kind, 16));
        left.Children.Add(Label(group.Project, 13, Palette.Text, FontWeight.SemiBold));
        if (m.Servers.Count > 1) left.Children.Add(Label(group.Server.DisplayName, 11.5, Palette.Muted));
        header.Children.Add(left);

        var icons = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        if (collapsed)
            foreach (var pipeline in group.Pipelines)
            {
                var build = Latest(groupBuilds, pipeline);
                bool waiting = build != null && awaiting.Contains(build.Key);
                var (color, glyph) = build == null ? (Palette.Gray, Glyph.Ring) : Palette.ForBuild(build, waiting);
                var icon = new Badge(color, glyph, 16, flash: waiting);
                ToolTip.SetTip(icon, $"{pipeline.Name}\n{(build == null ? "No runs yet" : IconTip(build, waiting))}");
                icons.Children.Add(build == null ? icon : Clickable(icon, () => Open(build.WebUrl)));
            }

        var panel = new StackPanel();
        panel.Children.Add(Clickable(new Border { Classes = { "row" }, Background = Brushes.Transparent, Child = header }, () => ToggleCollapsed(group.Key)));
        if (collapsed)
        {
            panel.Children.Add(new Border
            {
                BorderBrush = Palette.Border,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(10, 9),
                Child = icons,
            });
        }
        else
        {
            foreach (var pipeline in group.Pipelines)
            {
                var build = Latest(groupBuilds, pipeline);
                Control row = build == null
                    ? NoRunsRow(pipeline)
                    : BuildRow(m, build, awaiting.Contains(build.Key), Fmt.BuildLine(build), logo: null);
                panel.Children.Add(new Border { BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = row });
            }
        }

        return new Border
        {
            Margin = new Thickness(8, 10, 8, 0),
            Background = Palette.Surface,
            BorderBrush = Palette.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = panel,
        };
    }

    static Control NoRunsRow(TrackedPipeline pipeline)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 48, Margin = new Thickness(42, 0, 14, 0) };
        grid.Children.Add(Label(pipeline.Name, 13, Palette.Muted, FontWeight.SemiBold));
        var none = Label("No runs yet", 11.5, Palette.Muted);
        Grid.SetColumn(none, 1);
        grid.Children.Add(none);
        return grid;
    }

    void ToggleCollapsed(string key)
    {
        if (!_collapsed.Remove(key)) _collapsed.Add(key);
        CollapsedChanged?.Invoke(_collapsed.ToList());
        Rebuild();
    }

    string IconTip(BuildInfo b, bool awaitingApproval)
    {
        if (awaitingApproval) return $"#{b.Number} · waiting for approval";
        if (b.IsActive)
            return _model.Stages.TryGetValue(b.Key, out var stage) ? $"#{b.Number} · {stage}" : $"#{b.Number} · {(b.StartTime != null ? "running" : "queued")}";
        return $"#{b.Number} · {Palette.ResultText(b.Result)} · {Fmt.Ago(b.FinishTime ?? b.QueueTime)}";
    }

    Control History(DashboardModel m)
    {
        var list = new StackPanel();
        var groups = Groups(m);
        var filter = groups.FirstOrDefault(gr => gr.Key == _historyGroup);
        var builds = filter == null ? m.Builds.ToList() : m.Builds.Where(filter.Contains).ToList();

        // Filter line: which repo/project, with a way back to all.
        var filterRow = new DockPanel { Height = 36, Margin = new Thickness(16, 0) };
        if (filter != null)
        {
            var all = Link("Show all", () => ShowTab(Tab.History));
            DockPanel.SetDock(all, Dock.Right);
            filterRow.Children.Add(all);
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            name.Children.Add(ProviderLogo.Create(filter.Server.Kind, 16));
            name.Children.Add(Label(m.Servers.Count > 1 ? $"{filter.Project}  ·  {filter.Server.DisplayName}" : filter.Project, 13, Palette.Text, FontWeight.SemiBold));
            filterRow.Children.Add(name);
        }
        else filterRow.Children.Add(Label("All repositories and projects", 13, Palette.Text, FontWeight.SemiBold));
        list.Children.Add(filterRow);

        if (builds.Count == 0)
        {
            list.Children.Add(Centered("No runs yet.", 60));
            return list;
        }

        int pages = (builds.Count + PageSize - 1) / PageSize;
        _historyPage = Math.Clamp(_historyPage, 0, pages - 1);
        var awaiting = m.Approvals.Select(a => a.RunKey).ToHashSet();
        var kinds = m.Servers.ToDictionary(s => s.Id, s => s.Kind);
        foreach (var b in builds.Skip(_historyPage * PageSize).Take(PageSize))
        {
            // Without a filter, name the repo/project (with its provider mark) so runs can be told apart.
            var subtitle = filter == null ? $"{ShortProject(b.Project)} · {Fmt.BuildLine(b)}" : Fmt.BuildLine(b);
            ServerKind? logo = filter == null && kinds.TryGetValue(b.ServerId, out var kind) ? kind : null;
            list.Children.Add(new Border
            {
                Margin = new Thickness(8, 0),
                BorderBrush = Palette.Border,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = BuildRow(m, b, awaiting.Contains(b.Key), subtitle, logo),
            });
        }

        // Pager
        var pager = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 40, Margin = new Thickness(16, 0) };
        pager.Children.Add(PagerLink("‹ Newer", _historyPage > 0, -1));
        var info = Label($"Page {_historyPage + 1} of {pages} · {builds.Count} runs", 11.5, Palette.Muted);
        info.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(info, 1);
        pager.Children.Add(info);
        var older = PagerLink("Older ›", _historyPage < pages - 1, +1);
        Grid.SetColumn(older, 2);
        pager.Children.Add(older);
        list.Children.Add(new Border { BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(8, 0), Child = pager });
        return list;

        Control PagerLink(string label, bool enabled, int delta) => enabled
            ? Link(label, () => { _historyPage += delta; _scroll.Offset = default; Rebuild(); })
            : Label(label, 13, Palette.Border, FontWeight.SemiBold);
    }

    /// <summary>One run: status badge, pipeline name, subtitle, timing on the right, stage dots while it runs.</summary>
    Control BuildRow(DashboardModel m, BuildInfo b, bool awaitingApproval, string subtitle, ServerKind? logo)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            Margin = new Thickness(10, 6, 14, 7),
        };
        var (color, glyph) = Palette.ForBuild(b, awaitingApproval);
        var badge = new Badge(color, glyph, 20, flash: awaitingApproval) { Margin = new Thickness(0, 8, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRowSpan(badge, 3);
        grid.Children.Add(badge);

        var name = Label(b.DefinitionName, 13, Palette.Text, FontWeight.SemiBold);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        // A DockPanel (not a StackPanel) so the subtitle gets a finite width and trims instead of running under the timing.
        var sub = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        if (logo is { } kind)
        {
            var mark = ProviderLogo.Create(kind, 12);
            mark.Margin = new Thickness(0, 0, 5, 0);
            sub.Children.Add(mark);
        }
        sub.Children.Add(Label(subtitle, 12.5, Palette.Muted));
        Grid.SetColumn(sub, 1);
        Grid.SetRow(sub, 1);
        grid.Children.Add(sub);

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
        var activeColor = new SolidColorBrush(awaitingApproval ? Palette.Orange : Palette.Blue);
        var tr = Label(topRight, 12.5, b.IsActive ? activeColor : Palette.Muted);
        tr.HorizontalAlignment = HorizontalAlignment.Right;
        tr.MaxWidth = 150;
        tr.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(tr, 2);
        grid.Children.Add(tr);
        var br = Label(bottomRight, 11.5, Palette.Muted);
        br.HorizontalAlignment = HorizontalAlignment.Right;
        br.MaxWidth = 150;
        br.Margin = new Thickness(8, 2, 0, 0);
        Grid.SetColumn(br, 2);
        Grid.SetRow(br, 1);
        grid.Children.Add(br);

        if (b.IsActive && m.StageStatuses.TryGetValue(b.Key, out var stages) && stages.Count > 0)
        {
            var strip = Stages(stages);
            Grid.SetColumn(strip, 1);
            Grid.SetColumnSpan(strip, 2);
            Grid.SetRow(strip, 2);
            grid.Children.Add(strip);
        }

        return Clickable(new Border { Classes = { "row" }, Background = Brushes.Transparent, Child = grid }, () => Open(b.WebUrl));
    }

    /// <summary>A run's stages left to right as small status badges; only the current one is named when there are many.</summary>
    static Control Stages(IReadOnlyList<StageStatus> stages)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), ClipToBounds = true };
        bool nameAll = stages.Count <= 3;
        for (int i = 0; i < stages.Count; i++)
        {
            var s = stages[i];
            if (i > 0) strip.Children.Add(new Border { Width = 10, Height = 1, Background = Palette.Border, Margin = new Thickness(3, 0), VerticalAlignment = VerticalAlignment.Center });
            var (color, glyph) = Palette.ForStage(s);
            strip.Children.Add(new Badge(color, glyph, 12, flash: s.Waiting) { VerticalAlignment = VerticalAlignment.Center });
            if (nameAll || s.IsCurrent)
                strip.Children.Add(new TextBlock
                {
                    Text = s.Name,
                    FontSize = 11.5,
                    Foreground = s.State == "pending" ? Palette.Muted : Palette.Text,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
        }
        return strip;
    }

    void Open(string url)
    {
        Shell.Open(url);
        Navigated?.Invoke();
    }

    static TextBlock Label(string text, double size, IBrush color, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = color,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static Control Centered(string text, double height)
    {
        var label = Label(text, 13, Palette.Muted);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border { Height = height, Child = label };
    }

    static TextBlock Link(string text, Action onClick)
    {
        var link = Label(text, 13, Palette.Link, FontWeight.SemiBold);
        link.Classes.Add("link");
        Clickable(link, onClick);
        return link;
    }

    static T Clickable<T>(T control, Action onClick) where T : Control
    {
        control.Cursor = new Cursor(StandardCursorType.Hand);
        control.Tapped += (_, e) => { e.Handled = true; onClick(); };
        return control;
    }

    /// <summary>"owner/repo" → "repo"; the provider mark and filter already say where it lives.</summary>
    static string ShortProject(string project) => project[(project.LastIndexOf('/') + 1)..];

    /// <summary>Newest run of a tracked pipeline (builds are sorted newest first).</summary>
    static BuildInfo? Latest(List<BuildInfo> builds, TrackedPipeline pipeline) =>
        builds.FirstOrDefault(b => b.DefinitionId == pipeline.DefinitionId);

    /// <summary>Tracked pipelines grouped by server and repo/project, in settings order;
    /// within a group the most recently run pipeline comes first, never-run ones last by name.</summary>
    static List<Group> Groups(DashboardModel m) =>
        m.Servers
            .SelectMany(s => s.Pipelines
                .GroupBy(p => p.Project, StringComparer.OrdinalIgnoreCase)
                .Select(grp => new Group(s, grp.Key, grp
                    .OrderByDescending(p => m.Builds
                        .Where(b => b.ServerId == s.Id && b.DefinitionId == p.DefinitionId && b.Project.Equals(p.Project, StringComparison.OrdinalIgnoreCase))
                        .Max(b => b.QueueTime) ?? DateTime.MinValue)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList())))
            .ToList();

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
