namespace DevOpsWatcher;

/// <summary>Adds or edits one server: type, URL, sign-in and the pipelines to track. Produces <see cref="Result"/> on OK.</summary>
sealed class ServerForm : Form
{
    sealed record DefinitionTag(string Project, DefinitionInfo Definition);

    readonly ServerConfig _current;
    readonly RateLimitSettings _rateLimits;
    readonly bool _demo;
    readonly Dictionary<string, TrackedPipeline> _tracked;
    readonly HashSet<TreeNode> _loading = [];

    readonly RadioButton _azureDevOps = new() { Text = ServerConfig.KindNameOf(ServerKind.AzureDevOps), AutoSize = true };
    readonly RadioButton _gitHub = new() { Text = ServerConfig.KindNameOf(ServerKind.GitHub), AutoSize = true };
    readonly TextBox _url = new() { Dock = DockStyle.Fill };
    readonly Label _urlHint = Hint();
    readonly FlowLayoutPanel _authRow;
    readonly TextBox _pat = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    readonly Label _patHint = Hint();
    readonly RadioButton _usePat = new() { Text = "Personal access token", AutoSize = true, Checked = true };
    readonly RadioButton _useWindows = new() { Text = "Windows (integrated)", AutoSize = true };
    readonly Button _connect = new() { Text = "Connect", AutoSize = true };
    readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left, Padding = new Padding(6, 0, 0, 0) };
    readonly TextBox _filter = new() { Dock = DockStyle.Fill, PlaceholderText = "Filter loaded pipelines…" };
    readonly TreeView _tree = new NoDoubleClickTreeView { CheckBoxes = true, Dock = DockStyle.Fill, HideSelection = false };
    readonly Label _trackedCount = new() { AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left };

    IPipelineSource? _client;
    List<(ProjectInfo Project, List<DefinitionInfo>? Definitions)> _projects = [];

    public ServerConfig? Result { get; private set; }

    ServerKind Kind => _gitHub.Checked ? ServerKind.GitHub : ServerKind.AzureDevOps;

    /// <param name="existing">The server to edit, or null to add one.</param>
    public ServerForm(ServerConfig? existing, RateLimitSettings rateLimits, bool demo)
    {
        _current = existing?.Clone() ?? new ServerConfig();
        _rateLimits = rateLimits;
        _demo = demo;
        _tracked = _current.Pipelines.ToDictionary(p => p.Key);
        bool adding = existing == null;

        Text = adding ? "Add server" : $"Edit server – {_current.DisplayName}";
        Icon = IconFactory.Get(TrayState.Idle, 0, false);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(620, 600);
        MinimumSize = new Size(500, 460);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        _azureDevOps.Checked = _current.Kind == ServerKind.AzureDevOps;
        _gitHub.Checked = _current.Kind == ServerKind.GitHub;
        // The tracked pipelines belong to the server type, so it's fixed once the server exists.
        _azureDevOps.Enabled = _gitHub.Enabled = adding;
        _url.Text = _current.Url;
        _useWindows.Checked = _current.UseWindowsAuth;
        _usePat.Checked = !_current.UseWindowsAuth;
        _pat.PlaceholderText = _current.ProtectedPat != null ? "Saved – leave empty to keep" : "Paste your token";

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Label Row(string label, Control control, bool fill = false)
        {
            layout.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            var caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 6, 8, 0) };
            layout.Controls.Add(caption);
            layout.Controls.Add(control);
            return caption;
        }

        _authRow = Flow(_usePat, _useWindows);
        Row("Type", Flow(_azureDevOps, _gitHub));
        Row("Server URL", _url);
        Row("", _urlHint);
        var authCaption = Row("Sign in with", _authRow);
        Row("Token", _pat);
        Row("", _patHint);
        Row("", Flow(_connect, _status));
        Row("Filter", _filter);
        Row("Pipelines", _tree, fill: true);
        Row("", _trackedCount);

        var ok = new Button { Text = adding ? "Add" : "OK", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, ok]);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(buttons);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;

        void KindChanged(bool reset)
        {
            bool gitHub = Kind == ServerKind.GitHub;
            _urlHint.Text = gitHub
                ? "https://github.com, or your GitHub Enterprise URL (https://github.company.com, https://company.ghe.com)"
                : "e.g. https://dev.azure.com/yourorg  or  https://tfs.company.com/tfs/DefaultCollection";
            _patHint.Text = gitHub
                ? "Fine-grained token with repository permissions Actions (Read) and Deployments (Read & write, to approve), "
                  + "or a classic token with the repo scope. Stored encrypted for your Windows user."
                : "PAT scopes: Build (Read & execute) and Project and Team (Read). Stored encrypted for your Windows user.";
            _filter.PlaceholderText = gitHub ? "Filter loaded repositories and workflows…" : "Filter loaded pipelines…";
            authCaption.Visible = _authRow.Visible = !gitHub;
            if (gitHub) _usePat.Checked = true;
            if (!reset) return;
            if (gitHub && _url.Text.Trim() == "") _url.Text = ServerConfig.GitHubUrl;
            else if (!gitHub && _url.Text.Trim() == ServerConfig.GitHubUrl) _url.Text = "";
            _tracked.Clear();
            _projects = [];
            RebuildTree();
            UpdateTrackedCount();
            SetStatus("");
        }

        _gitHub.CheckedChanged += (_, _) => KindChanged(reset: true);
        _usePat.CheckedChanged += (_, _) => _pat.Enabled = _usePat.Checked;
        _connect.Click += async (_, _) => await ConnectAsync();
        _tree.BeforeExpand += async (_, e) => { if (e.Node != null) await EnsureLoadedAsync(e.Node); };
        _tree.AfterCheck += OnAfterCheck;
        _filter.TextChanged += (_, _) => RebuildTree();
        ok.Click += (_, _) => Accept();

        KindChanged(reset: false);
        _pat.Enabled = _usePat.Checked;
        UpdateTrackedCount();
        Shown += async (_, _) =>
        {
            if (_current.Url != "" && _current.HasCredentials) await ConnectAsync();
            else (adding ? (Control)_url : _pat).Focus();
        };
    }

    static Label Hint() => new()
    {
        AutoSize = true,
        UseMnemonic = false,
        ForeColor = Color.DimGray,
        MaximumSize = new Size(460, 0),
    };

    static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
        f.Controls.AddRange(controls);
        return f;
    }

    /// <summary>The form's values as a server; null (with a message shown) when something required is missing.</summary>
    ServerConfig? ReadServer()
    {
        var url = _url.Text.Trim().TrimEnd('/');
        if (url == "") { SetStatus("Enter the server URL.", error: true); return null; }
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) { SetStatus("That isn't a valid URL.", error: true); return null; }

        var server = new ServerConfig
        {
            Id = _current.Id,
            Kind = Kind,
            Url = url,
            UseWindowsAuth = Kind == ServerKind.AzureDevOps && _useWindows.Checked,
            ProtectedPat = _current.ProtectedPat,
            Pipelines = _tracked.Values.OrderBy(p => p.Project).ThenBy(p => p.Name).ToList(),
        };
        if (_pat.Text.Trim() != "") server.Pat = _pat.Text.Trim();
        if (!server.HasCredentials) { SetStatus("Enter an access token.", error: true); return null; }
        return server;
    }

    async Task ConnectAsync()
    {
        var server = ReadServer();
        if (server == null) return;

        _client?.Dispose();
        _client = server.CreateSource(_rateLimits, _demo);
        _connect.Enabled = false;
        SetStatus("Connecting…");
        try
        {
            var projects = await _client.GetProjectsAsync();
            _projects = projects.Select(p => (p, (List<DefinitionInfo>?)null)).ToList();
            RebuildTree();
            var noun = Kind == ServerKind.GitHub ? "repositories" : "project(s)";
            SetStatus($"Connected – {projects.Count} {noun}. Expand one to pick {(Kind == ServerKind.GitHub ? "workflows" : "pipelines")}.");

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
        if (definitions.Count == 0)
            node.Nodes.Add(new TreeNode(Kind == ServerKind.GitHub ? "(no workflows)" : "(no pipelines)") { ForeColor = Color.DimGray });
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

    void UpdateTrackedCount() => _trackedCount.Text = $"{_tracked.Count} {(Kind == ServerKind.GitHub ? "workflow(s)" : "pipeline(s)")} tracked";

    void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.Firebrick : Color.DimGray;
    }

    void Accept()
    {
        var server = ReadServer();
        if (server == null) return;
        Log.Write($"Settings: server {server.Kind} {server.DisplayName} with {server.Pipelines.Count} pipelines");
        Result = server;
        DialogResult = DialogResult.OK;
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
