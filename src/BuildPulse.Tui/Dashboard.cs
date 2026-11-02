namespace BuildPulse;

/// <summary>
/// The terminal dashboard: a status header, pending approvals, then the tracked pipelines grouped by repo/project
/// (or every run, in History), and a footer for key hints, prompts and toasts. Built to stay readable down to
/// about 40×8, so it fits a tmux split; columns drop out as the pane gets narrower.
/// </summary>
sealed class Dashboard
{
    static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    static readonly TimeSpan ToastTime = TimeSpan.FromSeconds(6);

    enum View { Pipelines, History }
    enum Kind { Section, Group, Approval, Build, NoRuns, Note }

    /// <summary>One entry in the list; everything but sections and notes can be selected.</summary>
    sealed record Item(Kind Kind, string Key, string Text = "")
    {
        public ApprovalInfo? Approval { get; init; }
        public BuildInfo? Build { get; init; }
        public Group? Group { get; init; }
        public bool Selectable => Kind is Kind.Group or Kind.Approval or Kind.Build or Kind.NoRuns;
    }

    sealed record Group(ServerConfig Server, string Project, List<TrackedPipeline> Pipelines)
    {
        public string Key => $"{Server.Id}|{Project.ToLowerInvariant()}";
        public bool Contains(BuildInfo b) => b.ServerId == Server.Id && b.Project.Equals(Project, StringComparison.OrdinalIgnoreCase);
    }

    sealed record Prompt(string Question, Tone Tone, Func<Task> Confirmed);

    readonly MainLoop _loop;
    readonly Terminal _term;
    readonly Watcher _watcher;
    readonly bool _demo;
    readonly HashSet<string> _collapsed;
    readonly HashSet<string> _knownApprovals = [];
    readonly Dictionary<string, ApprovalInfo> _lastApprovals = [];

    DashboardModel _model = DashboardModel.Empty;
    View _view = View.Pipelines;
    List<Item> _items = [];
    string? _selected;
    int _scroll;
    /// <summary>Lines of the list above and below what fits, for the scroll hints.</summary>
    int _above, _below;
    int _frame;
    int _width, _height;
    (string Text, Tone Tone, DateTime Until)? _toast;
    Prompt? _prompt;
    string? _busy;
    bool _help, _muted;
    DateTime _settingsStamp;

    public Dashboard(MainLoop loop, bool demo, Terminal term)
    {
        _loop = loop;
        _demo = demo;
        _term = term;
        _watcher = new Watcher(demo);
        _collapsed = [.. _watcher.Settings.CollapsedPanels];
        _settingsStamp = SettingsStamp();
    }

    /// <summary>Takes over the terminal; runs on the main loop until the user quits.</summary>
    public void Start(bool readKeys = true)
    {
        _term.Enter();
        _watcher.Changed += OnChanged;
        _watcher.Notified += (tone, title, body, _) => { if (!_muted) Toast($"{title} · {body}", tone); };
        _watcher.Apply(_watcher.Settings);

        var ticker = new Timer(_ => _loop.Post(Tick), null, 0, 100);
        var input = new Thread(() =>
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                _loop.Post(() => OnKey(key));
            }
        }) { IsBackground = true, Name = "Keys" };
        if (readKeys) input.Start();
        Quit = () => { ticker.Dispose(); _watcher.Dispose(); _term.Leave(); _loop.Stop(); };
    }

    public Action Quit { get; private set; } = () => { };

    /// <summary>Draws a frame and returns it; for <c>--snapshot</c>.</summary>
    public string Capture()
    {
        Draw();
        return _term.Snapshot;
    }

    // ---- Model ----

    void OnChanged()
    {
        _model = _watcher.Model(new UpdateStatus(UpdateStage.Unsupported));
        var pending = _model.Approvals.ToDictionary(a => a.Key);

        var fresh = pending.Values.Where(a => !_knownApprovals.Contains(a.Key)).ToList();
        if (fresh.Count > 0)
        {
            _term.Bell();
            var a = fresh[0];
            Toast(fresh.Count == 1 ? $"Approval needed · {Describe(a)}" : $"{fresh.Count} approvals needed", Tone.Orange);
            // Jump to it unless the user is busy elsewhere in the list.
            if (_selected == null || !_items.Any(i => i.Key == _selected && i.Kind == Kind.Approval)) _selected = "a:" + a.Key;
        }
        foreach (var (key, gone) in _lastApprovals.Where(kv => !pending.ContainsKey(kv.Key)))
            if (!_watcher.WasDecided(key)) Toast($"No longer pending · {Describe(gone)}", Tone.Gray);

        _knownApprovals.Clear();
        _knownApprovals.UnionWith(pending.Keys);
        _lastApprovals.Clear();
        foreach (var (key, a) in pending) _lastApprovals[key] = a;
        Draw();
    }

    void Tick()
    {
        _frame++;
        if (_toast is { } t && t.Until < DateTime.Now) _toast = null;
        // The tray app may have changed the servers or pipelines.
        if (!_demo && _frame % 50 == 0 && SettingsStamp() is var stamp && stamp != _settingsStamp)
        {
            _settingsStamp = stamp;
            _watcher.Apply(AppSettings.Load());
            Toast("Settings reloaded", Tone.Blue);
        }
        Draw();
    }

    static DateTime SettingsStamp()
    {
        try { return File.GetLastWriteTimeUtc(AppSettings.FilePath); }
        catch { return default; }
    }

    void Toast(string text, Tone tone) => _toast = (text, tone, DateTime.Now + ToastTime);

    static string Describe(ApprovalInfo a) =>
        string.Join(" · ", new[] { a.PipelineName, a.RunName, a.StageName }.Where(s => !string.IsNullOrEmpty(s)));

    List<Group> Groups() =>
        _model.Servers
            .SelectMany(s => s.Pipelines
                .GroupBy(p => p.Project, StringComparer.OrdinalIgnoreCase)
                .Select(grp => new Group(s, grp.Key, grp
                    .OrderByDescending(p => _model.Builds
                        .Where(b => b.ServerId == s.Id && b.DefinitionId == p.DefinitionId && b.Project.Equals(p.Project, StringComparison.OrdinalIgnoreCase))
                        .Max(b => b.QueueTime) ?? DateTime.MinValue)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList())))
            .ToList();

    List<Item> BuildItems()
    {
        var items = new List<Item>();
        if (_model.Approvals.Count > 0)
        {
            items.Add(new Item(Kind.Section, "s:approvals", $"Approvals · {_model.Approvals.Count}"));
            foreach (var a in _model.Approvals.OrderBy(a => a.CreatedOn))
                items.Add(new Item(Kind.Approval, "a:" + a.Key) { Approval = a });
        }

        if (_view == View.Pipelines)
        {
            foreach (var group in Groups())
            {
                items.Add(new Item(Kind.Group, "g:" + group.Key) { Group = group });
                if (_collapsed.Contains(group.Key)) continue;
                var builds = _model.Builds.Where(group.Contains).ToList();
                foreach (var pipeline in group.Pipelines)
                {
                    var build = builds.FirstOrDefault(b => b.DefinitionId == pipeline.DefinitionId);
                    items.Add(build == null
                        ? new Item(Kind.NoRuns, $"n:{group.Key}|{pipeline.DefinitionId}", pipeline.Name)
                        : new Item(Kind.Build, $"p:{group.Key}|{pipeline.DefinitionId}", pipeline.Name) { Build = build });
                }
            }
        }
        else
        {
            items.Add(new Item(Kind.Section, "s:history", $"History · {_model.Builds.Count} runs"));
            foreach (var b in _model.Builds)
                items.Add(new Item(Kind.Build, "b:" + b.Key, b.DefinitionName) { Build = b });
        }

        if (_model.State == TrayState.Unconfigured)
        {
            items.Add(new Item(Kind.Note, "x:1", "Nothing to watch yet."));
            items.Add(new Item(Kind.Note, "x:2", "Add servers and pipelines in the Build Pulse"));
            items.Add(new Item(Kind.Note, "x:3", "tray app (Settings), or try --demo."));
        }
        else if (_model.LastPoll == null && items.Count == 0)
            items.Add(new Item(Kind.Note, "x:1", "Loading…"));
        return items;
    }

    Item? Selected => _items.FirstOrDefault(i => i.Key == _selected);

    // ---- Keys ----

    public void OnKey(ConsoleKeyInfo key)
    {
        bool ctrlC = key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (ctrlC) { Quit(); return; }

        if (_prompt is { } prompt)
        {
            if (key.Key is ConsoleKey.Y or ConsoleKey.Enter) { _prompt = null; _ = Run(prompt.Confirmed); }
            else if (key.Key is ConsoleKey.N or ConsoleKey.Escape or ConsoleKey.Q) _prompt = null;
            Draw();
            return;
        }
        if (_help)
        {
            _help = false;
            if (key.Key == ConsoleKey.Q) Quit(); else Draw();
            return;
        }

        var selected = Selected;
        switch (key.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.K: Move(-1); break;
            case ConsoleKey.DownArrow or ConsoleKey.J: Move(1); break;
            case ConsoleKey.PageUp: Move(-Math.Max(1, BodyHeight - 1)); break;
            case ConsoleKey.PageDown: Move(Math.Max(1, BodyHeight - 1)); break;
            case ConsoleKey.Home: Move(int.MinValue / 2); break;
            case ConsoleKey.End: Move(int.MaxValue / 2); break;
            case ConsoleKey.G: Move(key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? int.MaxValue / 2 : int.MinValue / 2); break;
            case ConsoleKey.Tab or ConsoleKey.H or ConsoleKey.L:
                _view = _view == View.Pipelines ? View.History : View.Pipelines;
                _scroll = 0;
                break;
            case ConsoleKey.D1: _view = View.Pipelines; _scroll = 0; break;
            case ConsoleKey.D2: _view = View.History; _scroll = 0; break;
            case ConsoleKey.Enter or ConsoleKey.O or ConsoleKey.Spacebar or ConsoleKey.LeftArrow or ConsoleKey.RightArrow:
                if (selected?.Group is { } g)
                {
                    if (key.Key == ConsoleKey.LeftArrow) _collapsed.Add(g.Key);
                    else if (key.Key == ConsoleKey.RightArrow) _collapsed.Remove(g.Key);
                    else if (!_collapsed.Remove(g.Key)) _collapsed.Add(g.Key);
                }
                else if (key.Key is ConsoleKey.Enter or ConsoleKey.O && Link(selected) is { } url)
                {
                    Shell.Open(url);
                    Toast("Opened in the browser", Tone.Blue);
                }
                break;
            case ConsoleKey.A: Decide(selected, approve: true); break;
            case ConsoleKey.X or ConsoleKey.D: Decide(selected, approve: false); break;
            case ConsoleKey.R:
                Toast("Refreshing…", Tone.Blue);
                _ = _watcher.PollAsync();
                break;
            case ConsoleKey.M:
                _muted = !_muted;
                Toast(_muted ? "Status toasts muted" : "Status toasts on", Tone.Gray);
                break;
            case ConsoleKey.Oem2 or ConsoleKey.F1: _help = true; break; // '?' and '/'
            case ConsoleKey.Q or ConsoleKey.Escape: Quit(); return;
            default:
                if (key.KeyChar == '?') _help = true;
                break;
        }
        Draw();
    }

    static string? Link(Item? item) => item?.Approval?.WebUrl ?? item?.Build?.WebUrl;

    void Move(int delta)
    {
        var selectable = _items.Where(i => i.Selectable).ToList();
        if (selectable.Count == 0) return;
        int at = selectable.FindIndex(i => i.Key == _selected);
        at = at < 0 ? 0 : Math.Clamp(at + delta, 0, selectable.Count - 1);
        _selected = selectable[at].Key;
    }

    void Decide(Item? item, bool approve)
    {
        if (item?.Approval is not { } a)
        {
            Toast("Select an approval first", Tone.Gray);
            return;
        }
        if (!a.CanApprove)
        {
            Toast(a.BrowserHint ?? "Approve this one in the browser (⏎)", Tone.Amber);
            return;
        }
        var validation = a.Kind == ApprovalKind.ManualValidation;
        var verb = approve ? (validation ? "Resume" : "Approve") : "Reject";
        var busy = approve ? (validation ? "resuming…" : "approving…") : "rejecting…";
        _prompt = new Prompt($"{verb} {Describe(a)}?", approve ? Tone.Green : Tone.Red, async () =>
        {
            _busy = busy;
            try { await _watcher.DecideAsync(a, approve, ""); }
            catch (Exception ex) { Toast($"{verb} failed: {ex.Message}", Tone.Red); }
            finally { _busy = null; }
        });
    }

    async Task Run(Func<Task> action)
    {
        Draw();
        await action();
        Draw();
    }

    // ---- Drawing ----

    int BodyHeight => Math.Max(0, _height - (_height >= 5 ? 3 : 1));

    void Draw()
    {
        int width = _term.Width, height = _term.Height;
        if (width != _width || height != _height)
        {
            _width = width;
            _height = height;
            _term.Invalidate();
        }

        _items = BuildItems();
        if (Selected is not { Selectable: true })
            _selected = (_items.FirstOrDefault(i => i.Kind is Kind.Approval or Kind.Build or Kind.NoRuns) ?? _items.FirstOrDefault(i => i.Selectable))?.Key;

        _above = _below = 0;
        var body = (_help ? Help() : Body()).ToList();   // first: it works out the scroll hints
        var lines = new List<Line> { Header() };
        if (height >= 5) lines.Add(SubHeader());
        lines.AddRange(body);
        while (lines.Count < height - 1) lines.Add(new Line());
        if (height >= 3) lines.Add(Footer());

        _term.Title(Title());
        _term.Draw(lines, width, height);
    }

    string Title() => _model.State switch
    {
        TrayState.Approval => $"{AppInfo.Name} ! {_model.Approvals.Count} waiting",
        TrayState.Running => $"{AppInfo.Name} ⟳ {_model.Builds.Count(b => b.IsActive)} running",
        TrayState.Error => $"{AppInfo.Name} ✕ offline",
        _ => AppInfo.Name,
    };

    string Glyph(Glyph glyph) => glyph switch
    {
        BuildPulse.Glyph.Check => "✓",
        BuildPulse.Glyph.Cross => "✕",
        BuildPulse.Glyph.Bang => "!",
        BuildPulse.Glyph.Spinner => Spinner[_frame % Spinner.Length],
        BuildPulse.Glyph.Ring => "○",
        BuildPulse.Glyph.Dash => "–",
        BuildPulse.Glyph.Dots => "⋯",
        _ => " ",
    };

    /// <summary>Orange that pulses to red, like the tray icon, for anything waiting on the user.</summary>
    Rgb Flash => _frame / 5 % 2 == 0 ? Colors.Orange : Colors.Red;

    Line Header()
    {
        var (tone, glyph) = Status.ForState(_model.State);
        var color = _model.State == TrayState.Approval ? Flash : Colors.Of(tone);
        var label = _model.State switch
        {
            TrayState.Approval => $"{_model.Approvals.Count} waiting",
            TrayState.Running => $"{_model.Builds.Count(b => b.IsActive)} running",
            TrayState.Error => "offline",
            TrayState.Unconfigured => "not set up",
            _ => _watcher.FailureDot ? "last run failed" : "all quiet",
        };
        if (_model.State == TrayState.Idle && _watcher.FailureDot) color = Colors.Red;

        var left = new Line()
            .Add($" {Glyph(glyph)} {label} ", Colors.Ink, bold: true, bg: color)
            .Add(_width >= 48 ? "  Build Pulse" : "", Colors.Text, bold: true)
            .Add(_demo && _width >= 44 ? " demo" : "", Colors.Purple);

        var right = new Line();
        if (_width >= 30)
        {
            right.Add(_view == View.Pipelines ? "pipelines" : "history", Colors.Blue);
            right.Add(" · ", Colors.Faint);
        }
        right.Add(_busy ?? (_model.LastPoll is { } t ? Since(t) : "…"), _busy != null ? Colors.Amber : Colors.Muted);
        right.Add(" ");
        return Line.Spread(left, right, _width);
    }

    static string Since(DateTime t)
    {
        var s = (int)(DateTime.Now - t).TotalSeconds;
        return s < 60 ? $"{s}s" : Fmt.Ago(t).Replace(" ago", "");
    }

    Line SubHeader()
    {
        if (_model.Error is { } error)
            return new Line().Add(" ✕ ", Colors.Red, bold: true).Add(error.ReplaceLineEndings(" "), Colors.Red);
        var rule = new Line().Add(new string('─', _width), Colors.Faint);
        return _above > 0 ? Line.Spread(rule, new Line().Add($" ▲ {_above} ", Colors.Muted), _width) : rule;
    }

    IEnumerable<Line> Body()
    {
        int height = BodyHeight;
        if (height <= 0) yield break;

        // Lay out every item (the selected one may take two lines), then scroll so the selection is visible.
        var rows = new List<Line>();
        int selTop = 0, selBottom = 0;
        var stripWidth = Math.Min(8, _items.Select(i => i.Build is { } b && _model.StageStatuses.TryGetValue(b.Key, out var s) ? s.Count : 0).DefaultIfEmpty().Max());
        foreach (var item in _items)
        {
            bool sel = item.Key == _selected;
            if (item.Kind == Kind.Section && rows.Count > 0 && height >= 10) rows.Add(new Line());
            if (item.Kind == Kind.Group && rows.Count > 0 && height >= 14) rows.Add(new Line());
            if (sel) selTop = rows.Count;
            rows.AddRange(Render(item, sel, stripWidth));
            if (sel) selBottom = rows.Count - 1;
        }

        if (_selected != null)
        {
            if (selTop < _scroll) _scroll = selTop;
            if (selBottom >= _scroll + height) _scroll = selBottom - height + 1;
        }
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, rows.Count - height));

        _above = _scroll;
        _below = Math.Max(0, rows.Count - _scroll - height);
        foreach (var line in rows.Skip(_scroll).Take(height)) yield return line;
    }

    IEnumerable<Line> Render(Item item, bool sel, int stripWidth)
    {
        switch (item.Kind)
        {
            case Kind.Section:
                var color = item.Key == "s:approvals" ? Flash : Colors.Muted;
                yield return new Line().Add(" ").Add(item.Text.ToUpperInvariant(), color, bold: true);
                break;

            case Kind.Note:
                yield return new Line().Add("   " + item.Text, Colors.Muted);
                break;

            case Kind.Group:
                yield return GroupLine(item.Group!, sel);
                break;

            case Kind.NoRuns:
            {
                var bg = sel ? Colors.Selection : (Rgb?)null;
                var left = Bar(sel).Add(" – ", Colors.Faint, bg: bg).Add(item.Text, Colors.Muted, bg: bg);
                yield return Line.Spread(left, new Line().Add("no runs ", Colors.Faint, bg: bg), _width, bg);
                break;
            }

            case Kind.Approval:
                foreach (var line in ApprovalLines(item.Approval!, sel)) yield return line;
                break;

            case Kind.Build:
                foreach (var line in BuildLines(item, sel, stripWidth)) yield return line;
                break;
        }
    }

    Line Bar(bool sel) => new Line().Add(sel ? "▌" : " ", Colors.Blue, bg: sel ? Colors.Selection : null);

    Line GroupLine(Group group, bool sel)
    {
        var bg = sel ? Colors.Selection : (Rgb?)null;
        bool collapsed = _collapsed.Contains(group.Key);
        var mark = group.Server.Kind == ServerKind.GitHub ? Colors.Purple : Colors.Blue;
        var left = Bar(sel)
            .Add(collapsed ? "▸ " : "▾ ", Colors.Muted, bg: bg)
            .Add("● ", mark, bg: bg)
            .Add(group.Project, Colors.Text, bold: true, bg: bg);
        if (_model.Servers.Count > 1 && _width >= 50) left.Add("  " + group.Server.DisplayName, Colors.Faint, bg: bg);

        var right = new Line();
        if (collapsed)
        {
            // One status glyph per pipeline, like the tray app's collapsed panel.
            var builds = _model.Builds.Where(group.Contains).ToList();
            var awaiting = _model.Approvals.Select(a => a.RunKey).ToHashSet();
            foreach (var p in group.Pipelines.Take(Math.Max(1, _width / 4)))
            {
                var b = builds.FirstOrDefault(b => b.DefinitionId == p.DefinitionId);
                if (b == null) { right.Add("·", Colors.Faint, bg: bg); continue; }
                var waiting = awaiting.Contains(b.Key);
                var (tone, glyph) = Status.ForBuild(b, waiting);
                right.Add(Glyph(glyph), waiting ? Flash : Colors.Of(tone), bold: true, bg: bg);
            }
            right.Add(" ", bg: bg);
        }
        return Line.Spread(left, right, _width, bg);
    }

    IEnumerable<Line> ApprovalLines(ApprovalInfo a, bool sel)
    {
        var bg = sel ? new Rgb(0x4A, 0x36, 0x14) : Colors.ApprovalTint;
        var left = new Line()
            .Add(sel ? "▌" : " ", Colors.Orange, bg: bg)
            .Add(" ! ", Flash, bold: true, bg: bg)
            .Add(a.PipelineName, Colors.Text, bold: true, bg: bg)
            .Add(_width >= 44 && a.RunName != "" ? $" · {a.RunName}" : "", Colors.Muted, bg: bg);
        var right = new Line();
        if (a.StageName is { } stage && _width >= 56) right.Add(Fit(stage, 16) + "  ", Colors.Orange, bg: bg);
        right.Add(Pad(Fmt.Ago(a.CreatedOn).Replace("just now", "now"), 7) + " ", Colors.Muted, bg: bg);
        yield return Line.Spread(left, right, _width, bg);

        if (!sel) yield break;
        var detail = new Line { Background = bg }.Add("    ", bg: bg);
        if (!a.CanApprove)
            detail.Add(a.BrowserHint ?? "Approve in the browser", Colors.Amber, bg: bg).Add("  ⏎ open", Colors.Muted, bg: bg);
        else
        {
            var validation = a.Kind == ApprovalKind.ManualValidation;
            detail.Add("a", Colors.Green, bold: true, bg: bg).Add(validation ? " resume  " : " approve  ", Colors.Text, bg: bg)
                .Add("x", Colors.Red, bold: true, bg: bg).Add(" reject  ", Colors.Text, bg: bg)
                .Add("⏎", Colors.Blue, bold: true, bg: bg).Add(" open", Colors.Text, bg: bg);
            if (_width >= 60 && a.Approvers.Count > 0) detail.Add("  · " + string.Join(", ", a.Approvers), Colors.Muted, bg: bg);
        }
        yield return detail;
        if (a.Instructions != "" && _height >= 14)
            yield return new Line { Background = bg }.Add("    " + a.Instructions.ReplaceLineEndings(" "), Colors.Muted, bg: bg);
    }

    IEnumerable<Line> BuildLines(Item item, bool sel, int stripWidth)
    {
        var b = item.Build!;
        var bg = sel ? Colors.Selection : (Rgb?)null;
        bool waiting = _model.Approvals.Any(a => a.RunKey == b.Key);
        var (tone, glyph) = Status.ForBuild(b, waiting);
        var color = waiting ? Flash : Colors.Of(tone);

        var left = Bar(sel)
            .Add($" {Glyph(glyph)} ", color, bold: true, bg: bg)
            .Add(item.Text, b.IsActive || sel ? Colors.Text : new Rgb(0xC9, 0xD1, 0xD9), bold: b.IsActive, bg: bg);
        if (_view == View.History && _width >= 40 && b.Number != "")
            left.Add(" " + (char.IsDigit(b.Number[0]) ? "#" + b.Number : b.Number), Colors.Faint, bg: bg);

        var right = new Line();
        if (_width >= 44)
        {
            int w = _width >= 76 ? 20 : _width >= 60 ? 14 : 10;
            if (b.IsActive)
            {
                var stage = waiting ? "needs approval"
                    : _model.Stages.TryGetValue(b.Key, out var s) ? s : b.StartTime != null ? "running" : "queued";
                right.Add(Fit(stage, w) + "  ", waiting ? Colors.Orange : Colors.Blue, bg: bg);
            }
            else right.Add(Fit(Fmt.Branch(b.Branch), w) + "  ", Colors.Muted, bg: bg);
        }
        if (_width >= 60 && stripWidth > 0)
        {
            var strip = new Line();
            if (_model.StageStatuses.TryGetValue(b.Key, out var stages))
                foreach (var st in stages.Take(stripWidth))
                {
                    var (stTone, stGlyph) = Status.ForStage(st);
                    strip.Add(Glyph(stGlyph), st.Waiting ? Flash : Colors.Of(stTone), bg: bg);
                }
            right.Add(strip).Add(new string(' ', stripWidth - strip.Length + 2), bg: bg);
        }
        var time = b.IsActive
            ? b.StartTime is { } start ? Fmt.Duration(DateTime.Now - start) : ""
            : Fmt.Ago(b.FinishTime ?? b.QueueTime).Replace(" ago", "").Replace("just now", "now");
        right.Add(Pad(time, 7) + " ", b.IsActive ? Colors.Blue : Colors.Muted, bg: bg);
        yield return Line.Spread(left, right, _width, bg);

        if (!sel) yield break;
        var detail = new Line { Background = bg }.Add("    ", bg: bg);
        // History already shows the run number on the row.
        var info = _view == View.History
            ? string.Join(" · ", new[] { Fmt.Branch(b.Branch), b.RequestedFor }.Where(s => s != ""))
            : Fmt.BuildLine(b);
        if (!b.IsActive) info = $"{Status.ResultText(b.Result)} · {info}";
        detail.Add(info, Colors.Muted, bg: bg);
        if (_model.Servers.Count > 1 && _width >= 60)
            detail.Add(" · " + _model.Servers.FirstOrDefault(s => s.Id == b.ServerId)?.DisplayName, Colors.Faint, bg: bg);
        yield return detail;
    }

    static string Fit(string text, int width) => text.Length > width ? text[..(width - 1)] + "…" : text.PadRight(width);
    static string Pad(string text, int width) => text.PadLeft(width);

    IEnumerable<Line> Help()
    {
        (string Key, string What)[] keys =
        [
            ("↑↓ j k", "move"), ("⏎ o", "open in browser / fold"), ("← →", "fold / unfold repo"),
            ("a", "approve or resume"), ("x d", "reject"), ("tab 1 2", "pipelines / history"),
            ("r", "refresh now"), ("m", "mute status toasts"), ("q esc", "quit"),
        ];
        yield return new Line().Add(" KEYS", Colors.Blue, bold: true);
        foreach (var (key, what) in keys.Take(Math.Max(0, BodyHeight - 1)))
            yield return new Line().Add("  " + key.PadRight(9), Colors.Text, bold: true).Add(what, Colors.Muted);
    }

    Line Footer()
    {
        if (_prompt is { } p)
        {
            var bg = p.Tone == Tone.Red ? new Rgb(0x3B, 0x1D, 0x1D) : new Rgb(0x12, 0x2D, 0x1A);
            var right = new Line().Add(" y", Colors.Of(p.Tone), bold: true, bg: bg).Add("/", Colors.Muted, bg: bg)
                .Add("n ", Colors.Text, bold: true, bg: bg);
            return Line.Spread(new Line().Add(" " + p.Question, Colors.Text, bold: true, bg: bg), right, _width, bg);
        }
        if (_toast is { } t)
        {
            var c = Colors.Of(t.Tone);
            return new Line().Add("▌", c).Add(" " + t.Text, t.Tone == Tone.Gray ? Colors.Muted : c);
        }

        var hints = new Line();
        void Hint(string key, string what)
        {
            if (hints.Length > 0) hints.Add("  ");
            hints.Add(key, Colors.Blue, bold: true).Add(" " + what, Colors.Muted);
        }
        if (_width >= 64)
        {
            Hint("↑↓", "move"); Hint("⏎", "open");
            if (_model.Approvals.Count > 0) { Hint("a", "approve"); Hint("x", "reject"); }
            Hint("tab", "view"); Hint("r", "refresh"); Hint("?", "help"); Hint("q", "quit");
        }
        else
        {
            if (_model.Approvals.Count > 0) { Hint("a", "ok"); Hint("x", "no"); }
            Hint("⏎", "open"); Hint("tab", "view"); Hint("?", "help");
        }
        var more = _below > 0 ? new Line().Add($"▼ {_below} ", Colors.Muted) : new Line();
        return Line.Spread(new Line().Add(" ").Add(hints), more, _width);
    }
}
