using System.Text.RegularExpressions;

namespace DevOpsWatcher;

/// <summary>
/// Adds or edits one server. Adding starts with a choice of target (Azure DevOps, Azure DevOps Server, GitHub,
/// GitHub Enterprise); then only the values that target needs are asked for, followed by sign-in and the pipelines to track.
/// Produces <see cref="Result"/> on OK.
/// </summary>
sealed partial class ServerForm : Form
{
    /// <summary>What the user connects to; finer than <see cref="ServerKind"/> because each asks for different values.</summary>
    enum Target { AzureDevOps, AzureDevOpsServer, GitHub, GitHubEnterprise }

    sealed record DefinitionTag(string Project, DefinitionInfo Definition);

    const string AzureDevOpsHost = "https://dev.azure.com";

    readonly ServerConfig _current;
    readonly RateLimitSettings _rateLimits;
    readonly bool _demo;
    readonly bool _adding;
    readonly Dictionary<string, TrackedPipeline> _tracked;
    readonly HashSet<TreeNode> _loading = [];

    readonly Panel _picker = new() { Dock = DockStyle.Fill, Padding = new Padding(12) };
    readonly TableLayoutPanel _details = new() { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
    readonly PictureBox _targetLogo = new() { SizeMode = PictureBoxSizeMode.CenterImage, Margin = new Padding(0, 3, 6, 0) };
    readonly Label _targetName = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f), Padding = new Padding(0, 5, 8, 0) };
    readonly LinkLabel _changeTarget = new() { Text = "Change", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    readonly TextBox _org = new() { Dock = DockStyle.Fill, PlaceholderText = "yourorg" };
    readonly Label _orgHint = Hint();
    readonly TextBox _project = new() { Dock = DockStyle.Fill, PlaceholderText = "Optional" };
    readonly Label _projectHint = Hint();
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
    readonly Button _ok;
    readonly Label _urlCaption;

    /// <summary>Rows that only some targets have: caption and control shown or hidden together.</summary>
    readonly List<(Control[] Controls, Func<Target, bool> Shown)> _targetRows = [];

    Target _target;
    IPipelineSource? _client;
    List<(ProjectInfo Project, List<DefinitionInfo>? Definitions)> _projects = [];

    public ServerConfig? Result { get; private set; }

    ServerKind Kind => _target is Target.GitHub or Target.GitHubEnterprise ? ServerKind.GitHub : ServerKind.AzureDevOps;

    /// <param name="existing">The server to edit, or null to add one.</param>
    public ServerForm(ServerConfig? existing, RateLimitSettings rateLimits, bool demo)
    {
        _current = existing?.Clone() ?? new ServerConfig();
        _rateLimits = rateLimits;
        _demo = demo;
        _adding = existing == null;
        _tracked = _current.Pipelines.ToDictionary(p => p.Key);

        Text = _adding ? "Add server" : $"Edit server – {_current.DisplayName}";
        Icon = IconFactory.App;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(620, 600);
        MinimumSize = new Size(500, 460);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        _target = TargetOf(_current);
        if (_target == Target.AzureDevOps) _org.Text = OrganizationOf(_current.Url) ?? "";
        else if (_target != Target.GitHub) _url.Text = _current.Url;
        _project.Text = _current.Project ?? "";
        _useWindows.Checked = _current.UseWindowsAuth;
        _usePat.Checked = !_current.UseWindowsAuth;
        _pat.PlaceholderText = _current.ProtectedPat != null ? "Saved – leave empty to keep" : "Paste your token";

        _details.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Label Row(string label, Control control, bool fill = false, Func<Target, bool>? shown = null)
        {
            _details.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            var caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 6, 8, 0) };
            // Explicit cells: auto-placement skips hidden controls, which would shift later rows up.
            int row = _details.RowStyles.Count - 1;
            _details.Controls.Add(caption, 0, row);
            _details.Controls.Add(control, 1, row);
            if (shown != null) _targetRows.Add(([caption, control], shown));
            return caption;
        }

        _authRow = Flow(_usePat, _useWindows);
        Row("Type", Flow(_targetLogo, _targetName, _changeTarget));
        Row("Organization", _org, shown: t => t == Target.AzureDevOps);
        Row("", _orgHint, shown: t => t == Target.AzureDevOps);
        Row("Project", _project, shown: t => t == Target.AzureDevOps);
        Row("", _projectHint, shown: t => t == Target.AzureDevOps);
        _urlCaption = Row("Server URL", _url, shown: t => t is Target.AzureDevOpsServer or Target.GitHubEnterprise);
        Row("", _urlHint, shown: t => t is Target.AzureDevOpsServer or Target.GitHubEnterprise);
        Row("Sign in with", _authRow, shown: t => t == Target.AzureDevOpsServer);
        Row("Token", _pat);
        Row("", _patHint);
        Row("", Flow(_connect, _status));
        Row("Filter", _filter);
        Row("Pipelines", _tree, fill: true);
        Row("", _trackedCount);

        _ok = new Button { Text = _adding ? "Add" : "OK", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, _ok]);
        _details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _details.Controls.Add(buttons, 0, _details.RowStyles.Count - 1);
        _details.SetColumnSpan(buttons, 2);

        BuildPicker();
        Controls.Add(_details);
        Controls.Add(_picker);
        CancelButton = cancel;

        _orgHint.Text = "Your organization's name, as in dev.azure.com/yourorg.";
        _projectHint.Text = "Only list this project. Leave empty to see every project in the organization.";
        _changeTarget.Visible = _adding;
        _changeTarget.LinkClicked += (_, _) => ShowPicker();
        _usePat.CheckedChanged += (_, _) => _pat.Enabled = _usePat.Checked;
        _connect.Click += async (_, _) => await ConnectAsync();
        _tree.BeforeExpand += async (_, e) => { if (e.Node != null) await EnsureLoadedAsync(e.Node); };
        _tree.AfterCheck += OnAfterCheck;
        _filter.TextChanged += (_, _) => RebuildTree();
        _ok.Click += (_, _) => Accept();

        _pat.Enabled = _usePat.Checked;
        ApplyTarget(_target);
        if (_adding) ShowPicker();
        else { _picker.Visible = false; AcceptButton = _ok; }
        UpdateTrackedCount();
        Shown += async (_, _) =>
        {
            if (_adding) return;
            if (_current.Url != "" && _current.HasCredentials) await ConnectAsync();
            else _pat.Focus();
        };
    }

    /// <summary>Shows the details page's fields for <paramref name="target"/>.</summary>
    void ApplyTarget(Target target)
    {
        _target = target;
        bool gitHub = Kind == ServerKind.GitHub;
        _targetName.Text = TargetName(target);
        _targetLogo.Image?.Dispose();
        _targetLogo.Image = LogoBitmap(Kind, LogicalToDeviceUnits(18));
        _targetLogo.Size = new Size(LogicalToDeviceUnits(20), LogicalToDeviceUnits(22));
        foreach (var (controls, shown) in _targetRows)
            foreach (var control in controls)
                control.Visible = shown(target);

        _urlCaption.Text = target == Target.AzureDevOpsServer ? "Collection URL" : "Server URL";
        _url.PlaceholderText = target == Target.AzureDevOpsServer ? "https://tfs.company.com/tfs/DefaultCollection" : "https://github.company.com";
        _urlHint.Text = target == Target.AzureDevOpsServer
            ? "The full URL of your Azure DevOps Server collection."
            : "The full URL of your GitHub Enterprise Server (https://github.company.com) or GHE.com (https://company.ghe.com).";
        _patHint.Text = gitHub
            ? "Fine-grained token with repository permissions Actions (Read) and Deployments (Read & write, to approve), "
              + "or a classic token with the repo scope. Stored encrypted for your Windows user."
            : "PAT scopes: Build (Read & execute) and Project and Team (Read). Stored encrypted for your Windows user.";
        _filter.PlaceholderText = gitHub ? "Filter loaded repositories and workflows…" : "Filter loaded pipelines…";
        if (target != Target.AzureDevOpsServer) _usePat.Checked = true;
        UpdateTrackedCount();
    }

    void BuildPicker()
    {
        var title = new Label
        {
            Text = "What do you want to watch?",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 11f),
            Dock = DockStyle.Top,
            Padding = new Padding(0, 0, 0, 10),
        };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foreach (var (target, detail) in new (Target, string)[]
        {
            (Target.AzureDevOps, "dev.azure.com organization"),
            (Target.AzureDevOpsServer, "On-premises collection URL"),
            (Target.GitHub, "github.com"),
            (Target.GitHubEnterprise, "Your GitHub server's URL"),
        })
        {
            var kind = target is Target.GitHub or Target.GitHubEnterprise ? ServerKind.GitHub : ServerKind.AzureDevOps;
            var button = new Button
            {
                Text = $"{TargetName(target)}\n{detail}",
                Image = LogoBitmap(kind, LogicalToDeviceUnits(28)),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Dock = DockStyle.Fill,
                Height = 72,
                Padding = new Padding(10, 0, 6, 0),
                Margin = new Padding(4),
                UseMnemonic = false,
            };
            button.Click += (_, _) => PickTarget(target);
            grid.Controls.Add(button);
        }
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, Dock = DockStyle.Right };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = cancel.PreferredSize.Height + 6 };
        bottom.Controls.Add(cancel);
        _picker.Controls.Add(bottom);
        _picker.Controls.Add(grid);
        _picker.Controls.Add(title);
    }

    void ShowPicker()
    {
        Text = "Add server";
        _picker.Visible = true;
        _details.Visible = false;
        AcceptButton = null;
    }

    void PickTarget(Target target)
    {
        if (target != _target)
        {
            // Tracked pipelines belong to one server, so a different target starts over.
            _client?.Dispose();
            _client = null;
            _tracked.Clear();
            _projects = [];
            RebuildTree();
            SetStatus("");
        }
        ApplyTarget(target);
        Text = $"Add server – {TargetName(target)}";
        _picker.Visible = false;
        _details.Visible = true;
        AcceptButton = _ok;
        (target switch
        {
            Target.AzureDevOps => _org,
            Target.GitHub => _pat,
            _ => (Control)_url,
        }).Focus();
    }

    static string TargetName(Target target) => target switch
    {
        Target.AzureDevOps => "Azure DevOps",
        Target.AzureDevOpsServer => "Azure DevOps Server",
        Target.GitHub => "GitHub",
        _ => "GitHub Enterprise",
    };

    static Target TargetOf(ServerConfig server)
    {
        if (server.Kind == ServerKind.GitHub)
            return server.Url == "" || server.Url.TrimEnd('/').Equals(ServerConfig.GitHubUrl, StringComparison.OrdinalIgnoreCase)
                ? Target.GitHub : Target.GitHubEnterprise;
        return server.Url == "" || OrganizationOf(server.Url) != null ? Target.AzureDevOps : Target.AzureDevOpsServer;
    }

    /// <summary>
    /// The organization of an Azure DevOps Services URL (dev.azure.com/org/... or org.visualstudio.com),
    /// a bare organization name, or null for anything else.
    /// </summary>
    static string? OrganizationOf(string text)
    {
        text = text.Trim().TrimEnd('/');
        if (OrgName().IsMatch(text)) return text;
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
            return uri.Segments.Length > 1 && OrgName().IsMatch(uri.Segments[1].TrimEnd('/')) ? uri.Segments[1].TrimEnd('/') : null;
        if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
            return uri.Host[..uri.Host.IndexOf('.')];
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]*$")]
    private static partial Regex OrgName();

    static Bitmap LogoBitmap(ServerKind kind, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        ProviderLogo.Draw(g, new RectangleF(0, 0, size, size), kind, onLight: true);
        return bitmap;
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
        string url;
        switch (_target)
        {
            case Target.AzureDevOps:
                if (_org.Text.Trim() == "") { SetStatus("Enter your organization.", error: true); return null; }
                if (OrganizationOf(_org.Text) is not { } org) { SetStatus("That isn't a valid organization name.", error: true); return null; }
                url = $"{AzureDevOpsHost}/{org}";
                break;
            case Target.GitHub:
                url = ServerConfig.GitHubUrl;
                break;
            default:
                url = _url.Text.Trim().TrimEnd('/');
                if (url == "") { SetStatus("Enter the server URL.", error: true); return null; }
                if (!url.Contains("://")) url = "https://" + url;
                if (!Uri.TryCreate(url, UriKind.Absolute, out _)) { SetStatus("That isn't a valid URL.", error: true); return null; }
                if (Https.Parse(url) == null) { SetStatus("The server must use https.", error: true); return null; }
                break;
        }

        var project = _project.Text.Trim();
        var server = new ServerConfig
        {
            Id = _current.Id,
            Kind = Kind,
            Url = url,
            UseWindowsAuth = _target == Target.AzureDevOpsServer && _useWindows.Checked,
            Project = _target == Target.AzureDevOps && project != "" ? project : null,
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
            if (server.Project is { } only)
            {
                projects = projects.Where(p => p.Name.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
                if (projects.Count == 0)
                {
                    _projects = [];
                    RebuildTree();
                    SetStatus($"Connected, but there's no project '{only}' you can see.", error: true);
                    return;
                }
            }
            _projects = projects.Select(p => (p, (List<DefinitionInfo>?)null)).ToList();
            RebuildTree();
            var noun = Kind == ServerKind.GitHub ? "repositories" : "project(s)";
            SetStatus($"Connected – {projects.Count} {noun}. Expand one to pick {(Kind == ServerKind.GitHub ? "workflows" : "pipelines")}.");

            // Pre-load projects that already have tracked pipelines so their checkmarks show; a single project opens right away.
            foreach (TreeNode node in _tree.Nodes)
                if (node.Tag is ProjectInfo p && (projects.Count == 1
                    || _tracked.Values.Any(t => t.Project.Equals(p.Name, StringComparison.OrdinalIgnoreCase))))
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
