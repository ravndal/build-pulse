namespace BuildPulse;

/// <summary>
/// Settings in the terminal, so servers can be set up where the tray app can't run (over SSH, on a headless box):
/// the servers to watch, adding or editing one with <see cref="ServerEditor"/>, and a few options. A server change
/// is saved as soon as it's made; options are saved on leaving settings.
/// </summary>
sealed class SettingsScreen
{
    static readonly int[] PollSteps = [10, 15, 20, 30, 45, 60, 90, 120, 180, 300, 600];

    enum Row { Server, Add, Poll, Toasts, ManualValidation }

    readonly bool _demo;
    readonly Action<AppSettings> _save;
    readonly Action _close;
    readonly Action<string, Tone> _toast;
    readonly Action<string, Tone, Func<Task>> _ask;
    readonly Action _redraw;

    /// <summary>The working copy; a copy of it is handed to <see cref="_save"/> on each change.</summary>
    readonly AppSettings _settings;
    /// <summary>Tokens the last saved settings reference; ones a save drops are deleted.</summary>
    HashSet<string> _savedPats;
    bool _optionsChanged;
    int _row, _scroll;
    ServerEditor? _editor;

    /// <param name="save">Writes and applies the settings; throws when they can't be written.</param>
    /// <param name="ask">Shows a y/n prompt and runs the action on yes.</param>
    public SettingsScreen(AppSettings current, bool demo, Action<AppSettings> save, Action close,
        Action<string, Tone> toast, Action<string, Tone, Func<Task>> ask, Action redraw)
    {
        _settings = Copy(current);
        _savedPats = PatsOf(_settings.Servers);
        _demo = demo;
        _save = save;
        _close = close;
        _toast = toast;
        _ask = ask;
        _redraw = redraw;
        // Nothing to show without a server, so start with adding one.
        if (_settings.Servers.Count == 0) Edit(null);
    }

    public string Heading => _editor?.Heading ?? "settings";

    List<(Row Row, ServerConfig? Server)> Rows() =>
    [
        .. _settings.Servers.Select(s => (Row.Server, (ServerConfig?)s)),
        (Row.Add, null), (Row.Poll, null), (Row.Toasts, null), (Row.ManualValidation, null),
    ];

    // ---- Keys ----

    public void OnKey(ConsoleKeyInfo key)
    {
        if (_editor != null) { _editor.OnKey(key); return; }

        var rows = Rows();
        _row = Math.Clamp(_row, 0, rows.Count - 1);
        var (row, server) = rows[_row];
        switch (key.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.K: _row = Math.Max(0, _row - 1); break;
            case ConsoleKey.DownArrow or ConsoleKey.J: _row = Math.Min(rows.Count - 1, _row + 1); break;
            case ConsoleKey.Home: _row = 0; break;
            case ConsoleKey.End: _row = rows.Count - 1; break;
            case ConsoleKey.A or ConsoleKey.Insert: Edit(null); break;
            case ConsoleKey.Enter or ConsoleKey.Spacebar or ConsoleKey.E:
                if (row == Row.Server) Edit(server);
                else if (row == Row.Add) Edit(null);
                else if (row == Row.Poll) StepPoll(1);
                else Toggle(row);
                break;
            case ConsoleKey.LeftArrow or ConsoleKey.H or ConsoleKey.OemMinus:
                if (row == Row.Poll) StepPoll(-1); else if (row is Row.Toasts or Row.ManualValidation) Toggle(row);
                break;
            case ConsoleKey.RightArrow or ConsoleKey.L or ConsoleKey.OemPlus:
                if (row == Row.Poll) StepPoll(1); else if (row is Row.Toasts or Row.ManualValidation) Toggle(row);
                break;
            case ConsoleKey.X or ConsoleKey.D or ConsoleKey.Delete:
                if (server != null) Remove(server);
                break;
            case ConsoleKey.Escape or ConsoleKey.Q or ConsoleKey.S or ConsoleKey.OemComma: Leave(); break;
        }
    }

    void StepPoll(int direction)
    {
        int at = Array.FindIndex(PollSteps, s => s >= _settings.PollSeconds);
        if (at < 0) at = PollSteps.Length - 1;
        else if (direction < 0 && PollSteps[at] > _settings.PollSeconds) at--;
        _settings.PollSeconds = PollSteps[Math.Clamp(at + direction, 0, PollSteps.Length - 1)];
        _optionsChanged = true;
    }

    void Toggle(Row row)
    {
        if (row == Row.Toasts) _settings.ShowStatusPopups = !_settings.ShowStatusPopups;
        else _settings.EnableManualValidationActions = !_settings.EnableManualValidationActions;
        _optionsChanged = true;
    }

    void Edit(ServerConfig? existing)
    {
        _editor = new ServerEditor(existing, _settings.RateLimits, _demo, _redraw, server =>
        {
            _editor = null;
            if (server == null) return;
            int index = existing != null ? _settings.Servers.IndexOf(existing) : -1;
            if (index >= 0) _settings.Servers[index] = server;
            else _settings.Servers.Add(server);
            _row = _settings.Servers.IndexOf(server);
            Log.Write($"Settings (TUI): server {server.Kind} {server.DisplayName} with {server.Pipelines.Count} pipelines");
            Commit($"Saved {server.DisplayName} · {server.Pipelines.Count} tracked");
        });
    }

    void Remove(ServerConfig server) => _ask($"Stop watching {server.DisplayName}?", Tone.Red, () =>
    {
        _settings.Servers.Remove(server);
        Log.Write($"Settings (TUI): removed server {server.Kind} {server.DisplayName}");
        Commit($"Removed {server.DisplayName}");
        return Task.CompletedTask;
    });

    void Leave()
    {
        if (_optionsChanged) Commit("Settings saved");
        _close();
    }

    void Commit(string done)
    {
        try { _save(Copy(_settings)); }
        catch (Exception ex)
        {
            Log.Write($"Settings (TUI): saving failed: {ex.Message}");
            _toast($"Couldn't save settings: {ex.Message}", Tone.Red);
            return;
        }
        _optionsChanged = false;
        var kept = PatsOf(_settings.Servers);
        foreach (var pat in _savedPats.Except(kept)) Secrets.Delete(pat);
        _savedPats = kept;
        _toast(done, Tone.Green);
    }

    static AppSettings Copy(AppSettings s) => new()
    {
        Servers = s.Servers.Select(server => server.Clone()).ToList(),
        PollSeconds = s.PollSeconds,
        ShowStatusPopups = s.ShowStatusPopups,
        CollapsedPanels = [.. s.CollapsedPanels],
        EnableManualValidationActions = s.EnableManualValidationActions,
        RateLimits = s.RateLimits,
    };

    static HashSet<string> PatsOf(IEnumerable<ServerConfig> servers) =>
        servers.Select(s => s.ProtectedPat).OfType<string>().ToHashSet();

    // ---- Drawing ----

    public IEnumerable<(string Key, string What)> Hints(int width)
    {
        if (_editor != null) return _editor.Hints();
        var row = Rows()[Math.Clamp(_row, 0, Rows().Count - 1)].Row;
        return row switch
        {
            Row.Server => [("⏎", "edit"), ("a", "add"), ("x", "remove"), ("esc", "back")],
            Row.Add => [("⏎", "add server"), ("esc", "back")],
            Row.Poll => [("← →", "change"), ("esc", "back")],
            _ => [("⏎", "toggle"), ("esc", "back")],
        };
    }

    public List<Line> Body(int width, int height)
    {
        if (_editor != null) return _editor.Body(width, height);

        var rows = Rows();
        _row = Math.Clamp(_row, 0, rows.Count - 1);
        var lines = new List<Line> { Ui.Section("Servers") };
        int selTop = 0, selBottom = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var (row, server) = rows[i];
            bool sel = i == _row;
            var bg = sel ? Colors.Selection : (Rgb?)null;
            if (row == Row.Poll)
            {
                if (height >= 10) lines.Add(new Line());
                lines.Add(Ui.Section("Options"));
            }
            if (sel) selTop = lines.Count;
            switch (row)
            {
                case Row.Server:
                    var left = Ui.Bar(sel).Add(" ● ", server!.Kind == ServerKind.GitHub ? Colors.Purple : Colors.Blue, bg: bg)
                        .Add(server.DisplayName, Colors.Text, bold: true, bg: bg);
                    var count = server.Pipelines.Count;
                    var right = new Line().Add($"{count} tracked ", count == 0 ? Colors.Amber : Colors.Muted, bg: bg);
                    lines.Add(Line.Spread(left, right, width, bg));
                    if (sel && width >= 40)
                        lines.Add(new Line { Background = bg }.Add("    " + ServerTargets.Name(ServerTargets.Of(server)), Colors.Muted, bg: bg)
                            .Add(server.HasCredentials ? "" : " · no token", Colors.Red, bg: bg));
                    break;
                case Row.Add:
                    lines.Add(new Line { Background = bg }.Add(Ui.Bar(sel)).Add(" + ", Colors.Green, bold: true, bg: bg)
                        .Add("Add server", sel ? Colors.Text : Colors.Muted, bg: bg));
                    break;
                case Row.Poll:
                    lines.Add(Option(sel, "Poll every", new Line().Add("‹ ", Colors.Faint, bg: bg)
                        .Add(PollText(_settings.PollSeconds), Colors.Blue, bold: true, bg: bg)
                        .Add(" › ", Colors.Faint, bg: bg), width));
                    break;
                case Row.Toasts:
                    lines.Add(Option(sel, "Status toasts", OnOff(_settings.ShowStatusPopups, bg), width));
                    break;
                case Row.ManualValidation:
                    lines.Add(Option(sel, width >= 56 ? "Manual validation from here (experimental)" : "Manual validation (exp.)",
                        OnOff(_settings.EnableManualValidationActions, bg), width));
                    break;
            }
            if (sel) selBottom = lines.Count - 1;
        }
        if (height >= 14)
        {
            lines.Add(new Line());
            foreach (var text in Ui.Wrap($"Tokens: {Secrets.StorageText.TrimEnd('.')}. Rate limits are in {AppSettings.FilePath}.", width - 3, 3))
                lines.Add(new Line().Add("  " + text, Colors.Faint));
        }
        return Ui.Scroll(lines, selTop, selBottom, height, ref _scroll);
    }

    static string PollText(int seconds) => seconds >= 60 && seconds % 60 == 0 ? $"{seconds / 60}m" : Fmt.Duration(TimeSpan.FromSeconds(seconds));

    static Line Option(bool sel, string label, Line value, int width)
    {
        var bg = sel ? Colors.Selection : (Rgb?)null;
        return Line.Spread(Ui.Bar(sel).Add("   " + label, sel ? Colors.Text : new Rgb(0xC9, 0xD1, 0xD9), bg: bg), value, width, bg);
    }

    static Line OnOff(bool on, Rgb? bg) =>
        new Line().Add(on ? "● on " : "○ off", on ? Colors.Green : Colors.Muted, bold: on, bg: bg).Add(" ", bg: bg);
}

/// <summary>
/// Adds or edits one server in three steps, like the tray app's server window: pick what to connect to (when
/// adding), enter the values that target needs and a token, then connect and tick the pipelines to track.
/// </summary>
sealed class ServerEditor
{
    enum Page { Target, Details, Pipelines }
    enum Field { Organization, Project, Url, SignIn, Token, Connect }
    enum Kind { MissingHeader, Missing, Project, Pipeline, Note }

    sealed class ProjectNode(ProjectInfo project)
    {
        public ProjectInfo Project { get; } = project;
        public List<DefinitionInfo>? Definitions { get; set; }
        public string? Error { get; set; }
        public bool Expanded { get; set; }
        public bool Loading { get; set; }
    }

    sealed record Item(Kind Kind, string Key, ProjectNode? Node = null, TrackedPipeline? Pipeline = null, string Text = "")
    {
        public bool Selectable => Kind is Kind.Missing or Kind.Project or Kind.Pipeline;
    }

    readonly ServerConfig _current;
    readonly RateLimitSettings _rateLimits;
    readonly bool _demo;
    readonly bool _adding;
    readonly Action _redraw;
    readonly Action<ServerConfig?> _done;
    readonly Dictionary<string, TrackedPipeline> _tracked;

    Page _page;
    ServerTarget _target;
    int _targetRow, _scroll;
    string _org = "", _project = "", _url = "", _token = "";
    bool _windowsAuth;
    Field _field;
    string _status = "";
    bool _statusError, _connecting;

    // The token this editor stored (a Keychain item or token file) and the text it came from, so connecting twice
    // reuses one instead of storing another; it's deleted if the editor is cancelled.
    string? _storedPat, _storedPatText;

    IPipelineSource? _client;
    List<ProjectNode> _projects = [];
    /// <summary>True once the server has listed its projects, so tracked pipelines it lacks can be shown as missing.</summary>
    bool _listed;
    string _filter = "";
    bool _filtering;
    string? _selected;
    List<Item> _items = [];

    /// <param name="existing">The server to edit, or null to add one.</param>
    /// <param name="done">Called with the server to save, or null when cancelled.</param>
    public ServerEditor(ServerConfig? existing, RateLimitSettings rateLimits, bool demo, Action redraw, Action<ServerConfig?> done)
    {
        _current = existing?.Clone() ?? new ServerConfig();
        _rateLimits = rateLimits;
        _demo = demo;
        _adding = existing == null;
        _redraw = redraw;
        _done = done;
        _tracked = _current.Pipelines.ToDictionary(p => p.Key);

        _target = ServerTargets.Of(_current);
        _targetRow = Array.IndexOf(ServerTargets.All, _target);
        if (_target == ServerTarget.AzureDevOps) _org = ServerTargets.OrganizationOf(_current.Url) ?? "";
        else if (_target != ServerTarget.GitHub) _url = _current.Url;
        _project = _current.Project ?? "";
        _windowsAuth = _current.UseWindowsAuth;

        if (_adding) _page = Page.Target;
        else
        {
            _page = Page.Details;
            _field = Fields()[0];
            if (_current.Url != "" && (_current.HasCredentials || IsDemo(_current.Url))) _ = ConnectAsync();
        }
    }

    ServerKind ServerKind => ServerTargets.KindOf(_target);
    string Noun => ServerKind == ServerKind.GitHub ? "workflows" : "pipelines";

    public string Heading => _page switch
    {
        Page.Target => "add server",
        Page.Details => _adding ? "add server" : "edit server",
        _ => Noun,
    };

    List<Field> Fields()
    {
        var fields = new List<Field>();
        if (_target == ServerTarget.AzureDevOps) fields.AddRange([Field.Organization, Field.Project]);
        if (_target is ServerTarget.AzureDevOpsServer or ServerTarget.GitHubEnterprise) fields.Add(Field.Url);
        if (_target == ServerTarget.AzureDevOpsServer && OperatingSystem.IsWindows()) fields.Add(Field.SignIn);
        if (!WindowsAuth) fields.Add(Field.Token);
        fields.Add(Field.Connect);
        return fields;
    }

    /// <summary>--demo's fake server, which needs no token.</summary>
    bool IsDemo(string url) => _demo && url == DemoSource.Url;

    bool WindowsAuth => _windowsAuth && _target == ServerTarget.AzureDevOpsServer && OperatingSystem.IsWindows();

    // ---- Keys ----

    public void OnKey(ConsoleKeyInfo key)
    {
        switch (_page)
        {
            case Page.Target: TargetKey(key); break;
            case Page.Details: DetailsKey(key); break;
            default: PipelinesKey(key); break;
        }
    }

    void TargetKey(ConsoleKeyInfo key)
    {
        int count = ServerTargets.All.Length;
        switch (key.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.K: _targetRow = Math.Max(0, _targetRow - 1); break;
            case ConsoleKey.DownArrow or ConsoleKey.J: _targetRow = Math.Min(count - 1, _targetRow + 1); break;
            case >= ConsoleKey.D1 and <= ConsoleKey.D4: _targetRow = key.Key - ConsoleKey.D1; Pick(); break;
            case ConsoleKey.Enter or ConsoleKey.Spacebar or ConsoleKey.RightArrow: Pick(); break;
            case ConsoleKey.Escape or ConsoleKey.Q: Cancel(); break;
        }
    }

    void Pick()
    {
        var target = ServerTargets.All[Math.Clamp(_targetRow, 0, ServerTargets.All.Length - 1)];
        if (target != _target)
        {
            // Tracked pipelines belong to one server, so a different target starts over.
            _client?.Dispose();
            _client = null;
            _tracked.Clear();
            _projects = [];
            _listed = false;
            _status = "";
        }
        _target = target;
        if (!OperatingSystem.IsWindows() || target != ServerTarget.AzureDevOpsServer) _windowsAuth = false;
        _page = Page.Details;
        _field = Fields()[0];
    }

    void DetailsKey(ConsoleKeyInfo key)
    {
        var fields = Fields();
        int at = Math.Max(0, fields.IndexOf(_field));
        bool shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
            case ConsoleKey.Tab when shift:
                _field = fields[Math.Max(0, at - 1)];
                return;
            case ConsoleKey.DownArrow or ConsoleKey.Tab:
                _field = fields[Math.Min(fields.Count - 1, at + 1)];
                return;
            case ConsoleKey.Escape:
                if (_adding) _page = Page.Target; else Cancel();
                return;
            case ConsoleKey.Enter:
                if (_field == Field.Connect || fields[at + 1] == Field.Connect) _ = ConnectAsync();
                else _field = fields[at + 1];
                return;
        }
        if (_field == Field.SignIn)
        {
            if (key.Key is ConsoleKey.Spacebar or ConsoleKey.LeftArrow or ConsoleKey.RightArrow) _windowsAuth = !_windowsAuth;
            return;
        }
        if (_field == Field.Connect)
        {
            if (key.Key == ConsoleKey.Spacebar) _ = ConnectAsync();
            return;
        }
        ref string text = ref _token;
        if (_field == Field.Organization) text = ref _org;
        else if (_field == Field.Project) text = ref _project;
        else if (_field == Field.Url) text = ref _url;
        Ui.EditText(ref text, key);
    }

    void PipelinesKey(ConsoleKeyInfo key)
    {
        if (_filtering)
        {
            switch (key.Key)
            {
                case ConsoleKey.Enter: _filtering = false; return;
                case ConsoleKey.Escape: _filtering = false; _filter = ""; return;
                case ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.Tab: _filtering = false; break;
                default: Ui.EditText(ref _filter, key); return;
            }
        }

        var item = _items.FirstOrDefault(i => i.Key == _selected);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.K: Move(-1); break;
            case ConsoleKey.DownArrow or ConsoleKey.J or ConsoleKey.Tab: Move(1); break;
            case ConsoleKey.PageUp: Move(-10); break;
            case ConsoleKey.PageDown: Move(10); break;
            case ConsoleKey.Home: Move(int.MinValue / 2); break;
            case ConsoleKey.End: Move(int.MaxValue / 2); break;
            case ConsoleKey.Spacebar or ConsoleKey.X: _ = Toggle(item); break;
            case ConsoleKey.Enter:
                if (item?.Node is { } node && item.Kind == Kind.Project) _ = Expand(node, !node.Expanded);
                else _ = Toggle(item);
                break;
            case ConsoleKey.RightArrow or ConsoleKey.L:
                if (item?.Node is { } open) _ = Expand(open, true);
                break;
            case ConsoleKey.LeftArrow or ConsoleKey.H:
                if (item?.Node is { } close)
                {
                    _ = Expand(close, false);
                    _selected = "p:" + close.Project.Name;
                }
                break;
            case ConsoleKey.Oem2 or ConsoleKey.F: _filtering = true; break;   // '/'
            case ConsoleKey.S: Accept(); break;
            case ConsoleKey.Escape or ConsoleKey.Q: _page = Page.Details; _field = Field.Connect; break;
            default:
                if (key.KeyChar == '/') _filtering = true;
                break;
        }
    }

    void Move(int delta)
    {
        var selectable = _items.Where(i => i.Selectable).ToList();
        if (selectable.Count == 0) return;
        int at = selectable.FindIndex(i => i.Key == _selected);
        at = at < 0 ? 0 : Math.Clamp(at + delta, 0, selectable.Count - 1);
        _selected = selectable[at].Key;
    }

    async Task Expand(ProjectNode node, bool expanded)
    {
        node.Expanded = expanded;
        if (expanded) await LoadAsync(node);
    }

    /// <summary>Ticks or unticks a pipeline; on a project, every pipeline in it (loading them first).</summary>
    async Task Toggle(Item? item)
    {
        if (item?.Pipeline is { } pipeline) SetTracked(pipeline, !_tracked.ContainsKey(pipeline.Key));
        else if (item?.Node is { } node)
        {
            node.Expanded = true;
            await LoadAsync(node);
            if (node.Definitions is not { Count: > 0 } definitions) return;
            var pipelines = definitions.Select(d => PipelineOf(node, d)).ToList();
            bool all = pipelines.All(p => _tracked.ContainsKey(p.Key));
            foreach (var p in pipelines) SetTracked(p, !all);
            _redraw();
        }
    }

    void SetTracked(TrackedPipeline pipeline, bool tracked)
    {
        if (tracked) _tracked[pipeline.Key] = pipeline;
        else _tracked.Remove(pipeline.Key);
    }

    static TrackedPipeline PipelineOf(ProjectNode node, DefinitionInfo d) =>
        new() { Project = node.Project.Name, DefinitionId = d.Id, Name = d.Name };

    // ---- Server ----

    void SetStatus(string text, bool error = false) => (_status, _statusError) = (text, error);

    /// <summary>The values entered as a server; null (with the status set) when something required is missing.</summary>
    ServerConfig? ReadServer()
    {
        if (ServerTargets.UrlFor(_target, _org, _url, out var error) is not { } url)
        {
            SetStatus(error, error: true);
            return null;
        }
        var project = _project.Trim();
        var server = new ServerConfig
        {
            Id = _current.Id,
            Kind = ServerKind,
            Url = url,
            UseWindowsAuth = WindowsAuth || IsDemo(url),
            Project = _target == ServerTarget.AzureDevOps && project != "" ? project : null,
            ProtectedPat = _current.ProtectedPat,
            Pipelines = _tracked.Values.OrderBy(p => p.Project).ThenBy(p => p.Name).ToList(),
        };
        try
        {
            if (_token.Trim() is { Length: > 0 } pat)
            {
                if (pat != _storedPatText)
                {
                    var stored = Secrets.Protect(pat, _storedPat);
                    (_storedPat, _storedPatText) = (stored, pat);
                }
                server.ProtectedPat = _storedPat;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't store the token: {ex.Message}", error: true);
            return null;
        }
        if (!server.HasCredentials) { SetStatus("Enter an access token.", error: true); return null; }
        return server;
    }

    async Task ConnectAsync()
    {
        if (_connecting) return;
        var server = ReadServer();
        if (server == null) return;

        _client?.Dispose();
        var client = _client = server.CreateSource(_rateLimits, _demo);
        _connecting = true;
        SetStatus("Connecting…");
        _redraw();
        try
        {
            var projects = await client.GetProjectsAsync();
            if (client != _client) return;
            _listed = true;
            if (server.Project is { } only)
            {
                projects = projects.Where(p => p.Name.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
                if (projects.Count == 0)
                {
                    _projects = [];
                    SetStatus($"Connected, but there's no project '{only}' you can see.", error: true);
                    return;
                }
            }
            _projects = projects.Select(p => new ProjectNode(p)).ToList();
            SetStatus($"Connected · {projects.Count} {(ServerKind == ServerKind.GitHub ? "repositories" : "projects")}");
            _page = Page.Pipelines;
            _selected = null;
            _scroll = 0;
            _filter = "";
            // Open projects that already have tracked pipelines so their ticks show; a single project opens right away.
            foreach (var node in _projects)
                if (projects.Count == 1 || _tracked.Values.Any(t => t.Project.Equals(node.Project.Name, StringComparison.OrdinalIgnoreCase)))
                    _ = Expand(node, true);
        }
        catch (Exception ex)
        {
            if (client == _client) SetStatus(ex.Message.ReplaceLineEndings(" "), error: true);
        }
        finally
        {
            _connecting = false;
            _redraw();
        }
    }

    async Task LoadAsync(ProjectNode node)
    {
        if (node.Definitions != null || node.Loading || _client is not { } client) return;
        node.Loading = true;
        node.Error = null;
        _redraw();
        try
        {
            var definitions = await client.GetDefinitionsAsync(node.Project.Name);
            if (client == _client) node.Definitions = definitions;
        }
        catch (Exception ex) { node.Error = ex.Message.ReplaceLineEndings(" "); }
        finally
        {
            node.Loading = false;
            _redraw();
        }
    }

    void Accept()
    {
        var server = ReadServer();
        if (server == null)
        {
            _page = Page.Details;
            return;
        }
        _client?.Dispose();
        _done(server);
    }

    void Cancel()
    {
        _client?.Dispose();
        Secrets.Delete(_storedPat);
        _done(null);
    }

    // ---- Drawing ----

    public IEnumerable<(string Key, string What)> Hints() => _page switch
    {
        Page.Target => [("⏎", "pick"), ("↑↓", "move"), ("esc", "cancel")],
        Page.Details => [("⏎", _field == Field.Connect ? "connect" : "next"), ("↑↓", "field"), ("esc", _adding ? "back" : "cancel")],
        _ when _filtering => [("⏎", "done"), ("esc", "clear filter")],
        _ => [("space", "tick"), ("s", "save"), ("⏎", "open"), ("/", "filter"), ("esc", "back")],
    };

    public List<Line> Body(int width, int height) => _page switch
    {
        Page.Target => TargetBody(width, height),
        Page.Details => DetailsBody(width, height),
        _ => PipelinesBody(width, height),
    };

    List<Line> TargetBody(int width, int height)
    {
        var lines = new List<Line> { Ui.Section("What do you want to watch?") };
        int selTop = 0;
        for (int i = 0; i < ServerTargets.All.Length; i++)
        {
            var target = ServerTargets.All[i];
            bool sel = i == _targetRow;
            var bg = sel ? Colors.Selection : (Rgb?)null;
            if (sel) selTop = lines.Count;
            var left = Ui.Bar(sel).Add($" {i + 1} ", Colors.Faint, bg: bg)
                .Add("● ", ServerTargets.KindOf(target) == ServerKind.GitHub ? Colors.Purple : Colors.Blue, bg: bg)
                .Add(ServerTargets.Name(target), sel ? Colors.Text : new Rgb(0xC9, 0xD1, 0xD9), bold: sel, bg: bg);
            var detail = width >= 56 ? new Line().Add(ServerTargets.Detail(target) + " ", Colors.Muted, bg: bg) : new Line();
            lines.Add(Line.Spread(left, detail, width, bg));
        }
        return Ui.Scroll(lines, selTop, selTop, height, ref _scroll);
    }

    List<Line> DetailsBody(int width, int height)
    {
        var title = _adding ? ServerTargets.Name(_target) : _current.DisplayName;
        var lines = new List<Line> { Ui.Section((_adding ? "Add " : "Edit ") + title) };
        var fields = Fields();
        if (!fields.Contains(_field)) _field = fields[0];
        const int labelWidth = 14;
        int selTop = 0, selBottom = 0;
        foreach (var field in fields)
        {
            bool sel = field == _field;
            var bg = sel ? Colors.Selection : (Rgb?)null;
            if (field == Field.Connect && height >= 10) lines.Add(new Line());
            if (sel) selTop = lines.Count;
            switch (field)
            {
                case Field.Connect:
                    lines.Add(new Line { Background = bg }.Add(Ui.Bar(sel))
                        .Add(_connecting ? " … " : " → ", Colors.Green, bold: true, bg: bg)
                        .Add(_connecting ? "Connecting" : "Connect", sel ? Colors.Text : Colors.Muted, bold: sel, bg: bg));
                    break;
                case Field.SignIn:
                    lines.Add(new Line { Background = bg }.Add(Ui.Bar(sel)).Add("  " + "Sign in with".PadRight(labelWidth), Colors.Muted, bg: bg)
                        .Add(WindowsAuth ? "Windows" : "Token", Colors.Blue, bold: true, bg: bg)
                        .Add(sel ? "  space to switch" : "", Colors.Faint, bg: bg));
                    break;
                default:
                    var (label, value, placeholder) = field switch
                    {
                        Field.Organization => ("Organization", _org, "yourorg"),
                        Field.Project => ("Project", _project, "optional"),
                        Field.Url => (_target == ServerTarget.AzureDevOpsServer ? "Collection URL" : "Server URL", _url,
                            _target == ServerTarget.AzureDevOpsServer ? "https://tfs.company.com/tfs/Default" : "https://github.company.com"),
                        _ => ("Token", _token, _current.ProtectedPat != null ? "saved · leave empty to keep" : "paste your token"),
                    };
                    lines.Add(Ui.Field(label, value, placeholder, field == Field.Token, sel, labelWidth, width));
                    break;
            }
            if (sel)
            {
                var hint = Hint(field);
                if (hint != "")
                    foreach (var text in Ui.Wrap(hint, width - labelWidth - 4, height >= 12 ? 3 : 1))
                        lines.Add(new Line { Background = bg }.Add(new string(' ', labelWidth + 3) + text, Colors.Muted, bg: bg));
                selBottom = lines.Count - 1;
            }
        }
        if (_status != "")
            foreach (var text in Ui.Wrap(_status, width - 4, 3))
                lines.Add(new Line().Add("   " + text, _statusError ? Colors.Red : _connecting ? Colors.Blue : Colors.Green));
        return Ui.Scroll(lines, selTop, selBottom, height, ref _scroll);
    }

    string Hint(Field field) => field switch
    {
        Field.Organization => "As in dev.azure.com/yourorg.",
        Field.Project => "Only list this project. Leave empty for every project in the organization.",
        Field.Url when _target == ServerTarget.AzureDevOpsServer => "The full URL of your Azure DevOps Server collection.",
        Field.Url => "Your GitHub Enterprise Server or GHE.com URL, e.g. https://company.ghe.com.",
        Field.Token => $"{ServerTargets.TokenHint(ServerKind)} {Secrets.StorageText}",
        _ => "",
    };

    List<Item> PipelineItems()
    {
        var items = new List<Item>();
        var filter = _filter.Trim();
        bool Matches(string text) => text.Contains(filter, StringComparison.OrdinalIgnoreCase);

        if (_listed)
        {
            var missing = _tracked.Values.Where(IsMissing).OrderBy(p => p.Project).ThenBy(p => p.Name).ToList();
            if (missing.Count > 0)
            {
                items.Add(new Item(Kind.MissingHeader, "m", Text: "Not found on the server · untick to stop tracking"));
                foreach (var p in missing) items.Add(new Item(Kind.Missing, "m:" + p.Key, Pipeline: p, Text: $"{p.Project} / {p.Name}"));
            }
        }
        foreach (var node in _projects)
        {
            var definitions = node.Definitions;
            List<DefinitionInfo>? shown = definitions;
            if (filter != "")
            {
                if (definitions == null) { if (!Matches(node.Project.Name)) continue; }
                else
                {
                    shown = Matches(node.Project.Name) ? definitions : definitions.Where(d => Matches(d.DisplayName)).ToList();
                    if (shown.Count == 0) continue;
                }
            }
            items.Add(new Item(Kind.Project, "p:" + node.Project.Name, node));
            if (!node.Expanded && filter == "") continue;
            if (node.Loading) items.Add(new Item(Kind.Note, "l:" + node.Project.Name, node, Text: "loading…"));
            else if (node.Error != null) items.Add(new Item(Kind.Note, "e:" + node.Project.Name, node, Text: "Failed to load: " + node.Error));
            else if (shown is { Count: 0 }) items.Add(new Item(Kind.Note, "0:" + node.Project.Name, node, Text: $"(no {Noun})"));
            else if (shown != null)
                foreach (var d in shown)
                {
                    var p = PipelineOf(node, d);
                    items.Add(new Item(Kind.Pipeline, "d:" + p.Key, node, p, d.DisplayName));
                }
        }
        if (items.Count == 0)
            items.Add(new Item(Kind.Note, "none", Text: filter != "" ? "Nothing matches the filter." : "No projects you can see."));
        return items;
    }

    /// <summary>Its project isn't listed, or the project's pipelines are loaded and it isn't among them.</summary>
    bool IsMissing(TrackedPipeline pipeline)
    {
        var node = _projects.FirstOrDefault(p => p.Project.Name.Equals(pipeline.Project, StringComparison.OrdinalIgnoreCase));
        return node == null || (node.Definitions != null && node.Definitions.All(d => d.Id != pipeline.DefinitionId));
    }

    List<Line> PipelinesBody(int width, int height)
    {
        _items = PipelineItems();
        if (!_items.Any(i => i.Key == _selected && i.Selectable))
            _selected = _items.FirstOrDefault(i => i.Kind is Kind.Project or Kind.Pipeline)?.Key ?? _items.FirstOrDefault(i => i.Selectable)?.Key;

        var heading = new Line().Add(" ")
            .Add((_adding ? ServerTargets.Name(_target) : _current.DisplayName).ToUpperInvariant(), Colors.Muted, bold: true);
        var count = new Line().Add($"{_tracked.Count} tracked ", _tracked.Count > 0 ? Colors.Green : Colors.Amber, bold: true);
        var lines = new List<Line> { Line.Spread(heading, count, width) };
        if (_filtering || _filter != "")
            lines.Add(new Line().Add(" / ", Colors.Blue, bold: true).Add(_filter, Colors.Text).Add(_filtering ? "▏" : "", Colors.Blue));

        int selTop = 0, selBottom = 0;
        foreach (var item in _items)
        {
            bool sel = item.Key == _selected;
            var bg = sel ? Colors.Selection : (Rgb?)null;
            if (sel) selTop = lines.Count;
            switch (item.Kind)
            {
                case Kind.MissingHeader:
                    lines.Add(new Line().Add(" ✕ ", Colors.Red, bold: true).Add(item.Text, Colors.Red));
                    break;
                case Kind.Missing:
                case Kind.Pipeline:
                    bool tracked = _tracked.ContainsKey(item.Pipeline!.Key);
                    var indent = item.Kind == Kind.Pipeline ? "    " : "  ";
                    lines.Add(new Line { Background = bg }.Add(Ui.Bar(sel)).Add(indent, bg: bg).Add(Ui.Check(tracked ? 1 : 0, bg))
                        .Add(" " + item.Text, tracked || sel ? Colors.Text : Colors.Muted, bg: bg));
                    break;
                case Kind.Project:
                    lines.Add(ProjectLine(item.Node!, sel, width));
                    break;
                case Kind.Note:
                    var color = item.Key.StartsWith("e:", StringComparison.Ordinal) ? Colors.Red : Colors.Faint;
                    lines.Add(new Line().Add((item.Node != null ? "        " : "   ") + item.Text, color));
                    break;
            }
            if (sel) selBottom = lines.Count - 1;
        }
        // The heading and filter stay put while the list scrolls under them.
        int fixedRows = _filtering || _filter != "" ? 2 : 1;
        var list = lines.Skip(fixedRows).ToList();
        var shown = Ui.Scroll(list, Math.Max(0, selTop - fixedRows), Math.Max(0, selBottom - fixedRows), Math.Max(0, height - fixedRows), ref _scroll);
        return [.. lines.Take(fixedRows), .. shown];
    }

    Line ProjectLine(ProjectNode node, bool sel, int width)
    {
        var bg = sel ? Colors.Selection : (Rgb?)null;
        int tracked = _tracked.Values.Count(t => t.Project.Equals(node.Project.Name, StringComparison.OrdinalIgnoreCase));
        int state = tracked == 0 ? 0 : node.Definitions is { } d && tracked >= d.Count ? 1 : 2;
        var left = Ui.Bar(sel)
            .Add(node.Expanded || _filter != "" ? " ▾ " : " ▸ ", Colors.Muted, bg: bg)
            .Add(Ui.Check(state, bg))
            .Add(" " + node.Project.Name, Colors.Text, bold: true, bg: bg);
        var right = new Line();
        if (node.Loading) right.Add("… ", Colors.Blue, bg: bg);
        else if (node.Definitions is { } defs) right.Add($"{tracked}/{defs.Count} ", tracked > 0 ? Colors.Green : Colors.Faint, bg: bg);
        else if (tracked > 0) right.Add($"{tracked} ", Colors.Green, bg: bg);
        return Line.Spread(left, right, width, bg);
    }
}

/// <summary>Row pieces the settings pages share.</summary>
static class Ui
{
    public static Line Section(string text) => new Line().Add(" ").Add(text.ToUpperInvariant(), Colors.Muted, bold: true);

    public static Line Bar(bool sel) => new Line().Add(sel ? "▌" : " ", Colors.Blue, bg: sel ? Colors.Selection : null);

    /// <summary>A checkbox: 0 unticked, 1 ticked, 2 partly (some pipelines of a project).</summary>
    public static Line Check(int state, Rgb? bg) => state switch
    {
        1 => new Line().Add("[", Colors.Faint, bg: bg).Add("✓", Colors.Green, bold: true, bg: bg).Add("]", Colors.Faint, bg: bg),
        2 => new Line().Add("[", Colors.Faint, bg: bg).Add("–", Colors.Amber, bold: true, bg: bg).Add("]", Colors.Faint, bg: bg),
        _ => new Line().Add("[ ]", Colors.Faint, bg: bg),
    };

    /// <summary>A labelled text field; the end of a long value stays visible, and tokens show as dots.</summary>
    public static Line Field(string label, string value, string placeholder, bool secret, bool focused, int labelWidth, int width)
    {
        var bg = focused ? Colors.Selection : (Rgb?)null;
        var line = new Line { Background = bg }.Add(Bar(focused)).Add("  " + label.PadRight(labelWidth), focused ? Colors.Text : Colors.Muted, bg: bg);
        int room = Math.Max(4, width - line.Length - 2);
        if (value == "")
        {
            if (focused) line.Add("▏", Colors.Blue, bg: bg);
            return line.Add(placeholder, Colors.Faint, bg: bg);
        }
        var text = secret ? new string('•', Math.Min(value.Length, 32)) : value;
        if (text.Length > room) text = "…" + text[^(room - 1)..];
        line.Add(text, Colors.Text, bg: bg);
        return focused ? line.Add("▏", Colors.Blue, bg: bg) : line;
    }

    /// <summary>Typing into a text field: characters, backspace, ctrl+U to clear, ctrl+W to drop a word.</summary>
    public static bool EditText(ref string text, ConsoleKeyInfo key)
    {
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (key.Key == ConsoleKey.Backspace || key.KeyChar == '\b' || key.KeyChar == '\x7f')
        {
            if (text.Length > 0) text = text[..^1];
            return true;
        }
        if ((ctrl && key.Key == ConsoleKey.U) || key.KeyChar == '\x15') { text = ""; return true; }
        if ((ctrl && key.Key == ConsoleKey.W) || key.KeyChar == '\x17')
        {
            var trimmed = text.TrimEnd();
            int cut = trimmed.LastIndexOfAny([' ', '/', '.', '-']);
            text = cut < 0 ? "" : trimmed[..(cut + 1)];
            return true;
        }
        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
        {
            text += key.KeyChar;
            return true;
        }
        return false;
    }

    /// <summary><paramref name="text"/> in lines of at most <paramref name="width"/>, cut to <paramref name="maxLines"/> with "…".</summary>
    public static List<string> Wrap(string text, int width, int maxLines)
    {
        width = Math.Max(8, width);
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line != "" && line.Length + 1 + word.Length > width)
            {
                lines.Add(line);
                line = "";
            }
            line = line == "" ? word : line + " " + word;
        }
        if (line != "") lines.Add(line);
        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            var last = lines[^1];
            lines[^1] = (last.Length >= width ? last[..(width - 1)] : last) + "…";
        }
        return lines;
    }

    /// <summary>The rows that fit in <paramref name="height"/>, scrolled so the selected rows are visible.</summary>
    public static List<Line> Scroll(List<Line> rows, int selTop, int selBottom, int height, ref int scroll)
    {
        if (selTop < scroll) scroll = selTop;
        if (selBottom >= scroll + height) scroll = selBottom - height + 1;
        scroll = Math.Clamp(scroll, 0, Math.Max(0, rows.Count - height));
        return rows.Skip(scroll).Take(height).ToList();
    }
}
