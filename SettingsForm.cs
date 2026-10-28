namespace DevOpsWatcher;

/// <summary>Connection details and pipeline picker. Produces <see cref="Result"/> when saved.</summary>
sealed class SettingsForm : Form
{
    sealed record DefinitionTag(string Project, DefinitionInfo Definition);

    readonly AppSettings _current;
    readonly Dictionary<string, TrackedPipeline> _tracked;
    readonly HashSet<TreeNode> _loading = [];

    readonly TextBox _url = new() { Dock = DockStyle.Fill };
    readonly TextBox _pat = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    readonly RadioButton _usePat = new() { Text = "Personal access token", AutoSize = true, Checked = true };
    readonly RadioButton _useWindows = new() { Text = "Windows (integrated)", AutoSize = true };
    readonly Button _connect = new() { Text = "Connect", AutoSize = true };
    readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left, Padding = new Padding(6, 0, 0, 0) };
    readonly TextBox _filter = new() { Dock = DockStyle.Fill, PlaceholderText = "Filter loaded pipelines…" };
    readonly TreeView _tree = new NoDoubleClickTreeView { CheckBoxes = true, Dock = DockStyle.Fill, HideSelection = false };
    readonly NumericUpDown _poll = new() { Minimum = 10, Maximum = 600, Width = 70 };
    readonly CheckBox _popups = new() { Text = "Show popups for status changes (approvals always pop up)", AutoSize = true };
    readonly CheckBox _startup = new() { Text = "Start with Windows", AutoSize = true };
    readonly CheckBox _validationActions = new()
    {
        Text = "Experimental: Resume/Reject Manual validation from the popup",
        AutoSize = true,
    };
    readonly Label _trackedCount = new() { AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left };
    readonly Button _rateLimitsButton = new() { Text = "Change…", AutoSize = true };
    readonly Label _rateLimitsSummary = new() { AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left, Padding = new Padding(6, 0, 0, 0) };

    AzDoClient? _client;
    RateLimitSettings _rateLimits;
    List<(ProjectInfo Project, List<DefinitionInfo>? Definitions)> _projects = [];

    public AppSettings? Result { get; private set; }

    public SettingsForm(AppSettings current)
    {
        _current = current;
        _tracked = current.Pipelines.ToDictionary(p => p.Key);

        Text = "DevOps Watcher – Settings";
        Icon = IconFactory.Get(TrayState.Idle, 0, false);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(640, 680);
        MinimumSize = new Size(520, 520);
        StartPosition = FormStartPosition.CenterScreen;

        _url.Text = current.ServerUrl;
        _useWindows.Checked = current.UseWindowsAuth;
        _usePat.Checked = !current.UseWindowsAuth;
        _pat.PlaceholderText = current.ProtectedPat != null ? "Saved – leave empty to keep" : "Paste your PAT";
        _poll.Value = Math.Clamp(current.PollSeconds, 10, 600);
        _popups.Checked = current.ShowStatusPopups;
        _startup.Checked = Startup.IsEnabled;
        _validationActions.Checked = current.EnableManualValidationActions;
        _rateLimits = current.RateLimits;
        _rateLimitsSummary.Text = RateLimitForm.Summary(_rateLimits);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control control, bool fill = false)
        {
            layout.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 6, 8, 0) });
            layout.Controls.Add(control);
        }
        static FlowLayoutPanel Flow(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
            f.Controls.AddRange(controls);
            return f;
        }

        Row("Server URL", _url);
        Row("", new Label
        {
            Text = "e.g. https://dev.azure.com/yourorg  or  https://tfs.company.com/tfs/DefaultCollection",
            AutoSize = true,
            ForeColor = Color.DimGray,
        });
        Row("Sign in with", Flow(_usePat, _useWindows));
        Row("Token", _pat);
        Row("", new Label
        {
            Text = "PAT scopes: Build (Read & execute) and Project and Team (Read). Stored encrypted for your Windows user.",
            AutoSize = true,
            UseMnemonic = false,
            ForeColor = Color.DimGray,
            MaximumSize = new Size(480, 0),
        });
        Row("", Flow(_connect, _status));
        Row("Filter", _filter);
        Row("Pipelines", _tree, fill: true);
        Row("", _trackedCount);
        Row("Poll every", Flow(_poll, new Label { Text = "seconds", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }));
        Row("Rate limits", Flow(_rateLimitsButton, _rateLimitsSummary));
        Row("", _popups);
        Row("", _startup);
        Row("", _validationActions);

        var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, save]);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(buttons);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;

        _usePat.CheckedChanged += (_, _) => _pat.Enabled = _usePat.Checked;
        _validationActions.CheckedChanged += (_, _) => Log.Write($"Settings: experimental option {(_validationActions.Checked ? "on" : "off")}");
        _pat.Enabled = _usePat.Checked;
        _connect.Click += async (_, _) => await ConnectAsync();
        _rateLimitsButton.Click += (_, _) =>
        {
            using var dialog = new RateLimitForm(_rateLimits);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result == null) return;
            _rateLimits = dialog.Result;
            _rateLimitsSummary.Text = RateLimitForm.Summary(_rateLimits);
            Log.Write($"Settings: rate limits changed ({_rateLimitsSummary.Text})");
        };
        _tree.BeforeExpand += async (_, e) => { if (e.Node != null) await EnsureLoadedAsync(e.Node); };
        _tree.AfterCheck += OnAfterCheck;
        _filter.TextChanged += (_, _) => RebuildTree();
        save.Click += (_, _) => SaveAndClose();
        cancel.Click += (_, _) => { Log.Write("Settings: Cancel clicked"); Close(); };

        UpdateTrackedCount();
        Shown += async (_, _) =>
        {
            if (current.ServerUrl != "" && current.HasCredentials && current.ServerUrl != DemoSource.Url)
                await ConnectAsync();
        };
    }

    async Task ConnectAsync()
    {
        var url = _url.Text.Trim();
        if (url == "") { SetStatus("Enter the server URL.", error: true); return; }
        var pat = _pat.Text.Trim();
        if (pat == "") pat = _current.Pat ?? "";
        if (_usePat.Checked && pat == "") { SetStatus("Enter a personal access token.", error: true); return; }

        _client?.Dispose();
        _client = new AzDoClient(url, _useWindows.Checked, pat, _rateLimits);
        _connect.Enabled = false;
        SetStatus("Connecting…");
        try
        {
            var projects = await _client.GetProjectsAsync();
            _projects = projects.Select(p => (p, (List<DefinitionInfo>?)null)).ToList();
            RebuildTree();
            SetStatus($"Connected – {projects.Count} project(s). Expand a project to pick pipelines.");

            // Pre-load projects that already have tracked pipelines so their checkmarks show.
            foreach (TreeNode node in _tree.Nodes)
                if (node.Tag is ProjectInfo p && _tracked.Values.Any(t => t.Project.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                    node.Expand();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, error: true);
        }
        finally
        {
            _connect.Enabled = true;
        }
    }

    async Task EnsureLoadedAsync(TreeNode projectNode)
    {
        if (projectNode.Tag is not ProjectInfo project || _client == null) return;
        int index = _projects.FindIndex(p => p.Project == project);
        if (index < 0 || _projects[index].Definitions != null || !_loading.Add(projectNode)) return;
        try
        {
            var definitions = await _client.GetDefinitionsAsync(project.Name);
            _projects[index] = (project, definitions);
            FillProjectNode(projectNode, project, definitions);
            projectNode.Expand();
        }
        catch (Exception ex)
        {
            projectNode.Nodes.Clear();
            projectNode.Nodes.Add(new TreeNode($"Failed to load: {ex.Message}") { ForeColor = Color.Firebrick });
        }
        finally
        {
            _loading.Remove(projectNode);
        }
    }

    void RebuildTree()
    {
        var filter = _filter.Text.Trim();
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        foreach (var (project, definitions) in _projects)
        {
            var node = new TreeNode(project.Name) { Tag = project };
            if (definitions == null)
            {
                if (filter != "" && !project.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                node.Nodes.Add(new TreeNode("Loading…"));
            }
            else
            {
                var matches = definitions.Where(d => filter == ""
                    || d.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || project.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
                if (filter != "" && matches.Count == 0) continue;
                FillProjectNode(node, project, matches);
                if (filter != "") node.Expand();
            }
            _tree.Nodes.Add(node);
        }
        _tree.EndUpdate();
    }

    void FillProjectNode(TreeNode node, ProjectInfo project, List<DefinitionInfo> definitions)
    {
        _tree.AfterCheck -= OnAfterCheck;
        node.Nodes.Clear();
        foreach (var d in definitions)
        {
            var tag = new DefinitionTag(project.Name, d);
            node.Nodes.Add(new TreeNode(d.DisplayName)
            {
                Tag = tag,
                Checked = _tracked.ContainsKey(new TrackedPipeline { Project = project.Name, DefinitionId = d.Id }.Key),
            });
        }
        if (definitions.Count == 0) node.Nodes.Add(new TreeNode("(no pipelines)") { ForeColor = Color.DimGray });
        _tree.AfterCheck += OnAfterCheck;
    }

    async void OnAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (e.Node == null) return;
        if (e.Node.Tag is DefinitionTag tag)
        {
            var pipeline = new TrackedPipeline { Project = tag.Project, DefinitionId = tag.Definition.Id, Name = tag.Definition.Name };
            if (e.Node.Checked) _tracked[pipeline.Key] = pipeline;
            else _tracked.Remove(pipeline.Key);
            Log.Write($"Settings: {(e.Node.Checked ? "ticked" : "unticked")} {tag.Project}/{tag.Definition.Name}");
            UpdateTrackedCount();
        }
        else if (e.Node.Tag is ProjectInfo && e.Action != TreeViewAction.Unknown)
        {
            bool check = e.Node.Checked;
            await EnsureLoadedAsync(e.Node);
            foreach (TreeNode child in e.Node.Nodes)
                if (child.Tag is DefinitionTag) child.Checked = check;
        }
        else if (e.Node.Tag == null && e.Node.Checked)
        {
            e.Node.Checked = false; // placeholder rows aren't selectable
        }
    }

    void UpdateTrackedCount() => _trackedCount.Text = $"{_tracked.Count} pipeline(s) tracked";

    void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.Firebrick : Color.DimGray;
    }

    void SaveAndClose()
    {
        Log.Write($"Settings: Save clicked ({_tracked.Count} pipelines ticked, experimental={_validationActions.Checked})");
        var url = _url.Text.Trim();
        if (url == "") { SetStatus("Enter the server URL.", error: true); Log.Write("Settings: save stopped, no URL"); return; }

        var result = new AppSettings
        {
            ServerUrl = url,
            UseWindowsAuth = _useWindows.Checked,
            ProtectedPat = _current.ProtectedPat,
            PollSeconds = (int)_poll.Value,
            ShowStatusPopups = _popups.Checked,
            AutoUpdate = _current.AutoUpdate,
            EnableManualValidationActions = _validationActions.Checked,
            Pipelines = _tracked.Values.OrderBy(p => p.Project).ThenBy(p => p.Name).ToList(),
            RateLimits = _rateLimits,
        };
        if (_pat.Text.Trim() != "") result.Pat = _pat.Text.Trim();
        if (!result.HasCredentials) { SetStatus("Enter a personal access token.", error: true); Log.Write("Settings: save stopped, no token"); return; }

        try { Startup.Set(_startup.Checked); }
        catch { /* registry blocked by policy; not fatal */ }

        if (url != DemoSource.Url)
        {
            try
            {
                result.Save();
            }
            catch (Exception ex)
            {
                Log.Write($"Settings: writing settings.json failed: {ex.Message}");
                MessageBox.Show(this, $"Couldn't save settings:\n{ex.Message}", "DevOps Watcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }
        Log.Write($"Settings: saved {result.Pipelines.Count} pipelines, experimental={result.EnableManualValidationActions}");
        Result = result;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _client?.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>
    /// Works around the native TreeView bug where double-clicking a checkbox toggles the visual state
    /// without raising AfterCheck, so the tick on screen and the saved selection disagree.
    /// </summary>
    sealed class NoDoubleClickTreeView : TreeView
    {
        const int WM_LBUTTONDBLCLK = 0x0203;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_LBUTTONDBLCLK && CheckBoxes)
            {
                var point = new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
                if (HitTest(point).Location == TreeViewHitTestLocations.StateImage)
                {
                    m.Result = IntPtr.Zero; // treat as a single click; the first click already toggled it
                    return;
                }
            }
            base.WndProc(ref m);
        }
    }
}
