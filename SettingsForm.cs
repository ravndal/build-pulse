namespace DevOpsWatcher;

/// <summary>The servers to watch plus general options. Produces <see cref="Result"/> when saved.</summary>
sealed class SettingsForm : Form
{
    readonly AppSettings _current;
    readonly bool _demo;
    readonly List<ServerConfig> _servers;

    readonly ListView _serverList = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    readonly Button _add = new() { Text = "Add server…", AutoSize = true };
    readonly Button _edit = new() { Text = "Edit…", AutoSize = true };
    readonly Button _remove = new() { Text = "Remove", AutoSize = true };
    readonly NumericUpDown _poll = new() { Minimum = 10, Maximum = 600, Width = 70 };
    readonly CheckBox _popups = new() { Text = "Show popups for status changes (approvals always pop up)", AutoSize = true };
    readonly CheckBox _startup = new() { Text = "Start with Windows", AutoSize = true };
    readonly CheckBox _validationActions = new()
    {
        Text = "Experimental: Resume/Reject Azure DevOps Manual validation from the popup",
        AutoSize = true,
    };
    readonly Button _rateLimitsButton = new() { Text = "Change…", AutoSize = true };
    readonly Label _rateLimitsSummary = new() { AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left, Padding = new Padding(6, 0, 0, 0) };
    readonly Label _status = new() { AutoSize = true, ForeColor = Color.Firebrick, Anchor = AnchorStyles.Left };

    RateLimitSettings _rateLimits;

    public AppSettings? Result { get; private set; }

    public SettingsForm(AppSettings current, bool demo)
    {
        _current = current;
        _demo = demo;
        _servers = current.Servers.Select(s => s.Clone()).ToList();

        Text = "DevOps Watcher – Settings";
        Icon = IconFactory.App;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(600, 440);
        MinimumSize = new Size(500, 400);
        StartPosition = FormStartPosition.CenterScreen;

        _serverList.Columns.Add("Type", 120);
        _serverList.Columns.Add("Server", 280);
        _serverList.Columns.Add("Tracked", 80);
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

        Row("Servers", _serverList, fill: true);
        Row("", Flow(_add, _edit, _remove));
        Row("Poll every", Flow(_poll, new Label { Text = "seconds", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }));
        Row("Rate limits", Flow(_rateLimitsButton, _rateLimitsSummary));
        Row("", _popups);
        Row("", _startup);
        Row("", _validationActions);
        Row("", _status);

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

        _add.Click += (_, _) => EditServer(null);
        _edit.Click += (_, _) => { if (Selected is { } s) EditServer(s); };
        _serverList.DoubleClick += (_, _) => { if (Selected is { } s) EditServer(s); };
        _remove.Click += (_, _) => RemoveSelected();
        _serverList.SelectedIndexChanged += (_, _) => UpdateButtons();
        _validationActions.CheckedChanged += (_, _) => Log.Write($"Settings: experimental option {(_validationActions.Checked ? "on" : "off")}");
        _rateLimitsButton.Click += (_, _) =>
        {
            using var dialog = new RateLimitForm(_rateLimits);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result == null) return;
            _rateLimits = dialog.Result;
            _rateLimitsSummary.Text = RateLimitForm.Summary(_rateLimits);
            Log.Write($"Settings: rate limits changed ({_rateLimitsSummary.Text})");
        };
        save.Click += (_, _) => SaveAndClose();
        cancel.Click += (_, _) => { Log.Write("Settings: Cancel clicked"); Close(); };

        RefreshList();
        // Nothing to show without a server, so start with adding one.
        Shown += (_, _) => { if (_servers.Count == 0) EditServer(null); };
    }

    ServerConfig? Selected => _serverList.SelectedItems.Count > 0 ? _serverList.SelectedItems[0].Tag as ServerConfig : null;

    void EditServer(ServerConfig? existing)
    {
        using var dialog = new ServerForm(existing, _rateLimits, _demo);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is not { } server) return;
        int index = existing != null ? _servers.IndexOf(existing) : -1;
        if (index >= 0) _servers[index] = server;
        else _servers.Add(server);
        RefreshList(select: server);
    }

    void RemoveSelected()
    {
        if (Selected is not { } server) return;
        var answer = MessageBox.Show(this, $"Stop watching {server.DisplayName}?", "Remove server",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;
        _servers.Remove(server);
        Log.Write($"Settings: removed server {server.Kind} {server.DisplayName}");
        RefreshList();
    }

    void RefreshList(ServerConfig? select = null)
    {
        _serverList.BeginUpdate();
        _serverList.Items.Clear();
        foreach (var server in _servers)
        {
            var item = new ListViewItem([server.KindName, server.DisplayName, server.Pipelines.Count.ToString()]) { Tag = server };
            _serverList.Items.Add(item);
            if (server == select) item.Selected = true;
        }
        _serverList.EndUpdate();
        if (_servers.Count > 0) _status.Text = "";
        UpdateButtons();
    }

    void UpdateButtons() => _edit.Enabled = _remove.Enabled = Selected != null;

    void SaveAndClose()
    {
        Log.Write($"Settings: Save clicked ({_servers.Count} servers, {_servers.Sum(s => s.Pipelines.Count)} pipelines, experimental={_validationActions.Checked})");
        if (_servers.Count == 0) { _status.Text = "Add a server to watch."; Log.Write("Settings: save stopped, no servers"); return; }

        var result = new AppSettings
        {
            Servers = _servers,
            PollSeconds = (int)_poll.Value,
            ShowStatusPopups = _popups.Checked,
            AutoUpdate = _current.AutoUpdate,
            EnableManualValidationActions = _validationActions.Checked,
            RateLimits = _rateLimits,
        };

        try { Startup.Set(_startup.Checked); }
        catch { /* registry blocked by policy; not fatal */ }

        if (!_demo)
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
        Log.Write($"Settings: saved {result.Servers.Count} servers, experimental={result.EnableManualValidationActions}");
        Result = result;
        Close();
    }
}
