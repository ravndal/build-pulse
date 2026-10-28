namespace DevOpsWatcher;

/// <summary>Edits <see cref="RateLimitSettings"/>: the per-user budget and one budget per endpoint.</summary>
sealed class RateLimitForm : Form
{
    readonly CheckBox _enabled = new() { Text = "Limit requests to Azure DevOps", AutoSize = true };
    readonly NumericUpDown _userRequests = new() { Minimum = 0, Maximum = 100000, Width = 70 };
    readonly NumericUpDown _userSeconds = new() { Minimum = 1, Maximum = 3600, Width = 70 };
    readonly NumericUpDown _maxWait = new() { Minimum = 0, Maximum = 600, Width = 70 };
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        EditMode = DataGridViewEditMode.EditOnEnter,
        BackgroundColor = SystemColors.Window,
    };

    public RateLimitSettings? Result { get; private set; }

    public RateLimitForm(RateLimitSettings current)
    {
        Text = "Rate limits";
        Icon = IconFactory.App;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(460, 560);
        MinimumSize = new Size(420, 520);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        _enabled.Checked = current.Enabled;
        _userRequests.Value = Math.Clamp(current.PerUser?.Requests ?? 0, 0, 100000);
        _userSeconds.Value = Math.Clamp(current.PerUser?.PerSeconds ?? 60, 1, 3600);
        _maxWait.Value = Math.Clamp(current.MaxWaitSeconds, 0, 600);

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Endpoint", HeaderText = "Endpoint", ReadOnly = true, FillWeight = 50 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Requests", HeaderText = "Requests", FillWeight = 25 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Seconds", HeaderText = "Seconds", FillWeight = 25 });
        var limits = new Dictionary<string, RateLimit>(current.PerEndpoint ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in RateLimiter.Endpoints.All)
        {
            var limit = limits.GetValueOrDefault(endpoint);
            _grid.Rows.Add(endpoint, limit?.Requests ?? 0, limit?.PerSeconds ?? 60);
        }
        _grid.CellValidating += OnCellValidating;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control control, bool fill = false)
        {
            layout.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 6, 8, 0) });
            layout.Controls.Add(control);
        }
        static Label Unit(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        static FlowLayoutPanel Flow(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
            f.Controls.AddRange(controls);
            return f;
        }

        Row("", _enabled);
        Row("Per user", Flow(_userRequests, Unit("requests per"), _userSeconds, Unit("seconds")));
        Row("", new Label
        {
            Text = "Shared by all calls made with this server and sign-in. 0 = unlimited.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            MaximumSize = new Size(320, 0),
        });
        Row("Per endpoint", _grid, fill: true);
        Row("", new Label
        {
            Text = "0 requests = only the per-user limit applies.",
            AutoSize = true,
            ForeColor = Color.DimGray,
        });
        Row("Wait at most", Flow(_maxWait, Unit("seconds for a free slot")));
        Row("", new Label
        {
            Text = "After that the call fails. Azure DevOps's own Retry-After is always honoured.",
            MaximumSize = new Size(320, 0),
            AutoSize = true,
            ForeColor = Color.DimGray,
        });

        var ok = new Button { Text = "OK", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var defaults = new Button { Text = "Defaults", AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, ok, defaults]);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(buttons);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;

        _enabled.CheckedChanged += (_, _) => UpdateEnabled();
        ok.Click += (_, _) => Accept();
        defaults.Click += (_, _) => Fill(new RateLimitSettings());
        UpdateEnabled();
        Shown += (_, _) => _grid.ClearSelection();
    }

    void Fill(RateLimitSettings settings)
    {
        _enabled.Checked = settings.Enabled;
        _userRequests.Value = settings.PerUser?.Requests ?? 0;
        _userSeconds.Value = settings.PerUser?.PerSeconds ?? 60;
        _maxWait.Value = settings.MaxWaitSeconds;
        var limits = settings.PerEndpoint ?? [];
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var limit = limits.GetValueOrDefault((string)row.Cells[0].Value!);
            row.Cells[1].Value = limit?.Requests ?? 0;
            row.Cells[2].Value = limit?.PerSeconds ?? 60;
        }
    }

    void UpdateEnabled()
    {
        foreach (var control in new Control[] { _userRequests, _userSeconds, _maxWait, _grid })
            control.Enabled = _enabled.Checked;
        _grid.DefaultCellStyle.ForeColor = _enabled.Checked ? SystemColors.ControlText : SystemColors.GrayText;
    }

    void OnCellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (e.ColumnIndex == 0) return;
        int min = e.ColumnIndex == 1 ? 0 : 1;
        bool ok = int.TryParse(e.FormattedValue?.ToString(), out var value) && value >= min && value <= 100000;
        _grid.Rows[e.RowIndex].ErrorText = ok ? "" : $"Enter a whole number of {min} or more.";
        e.Cancel = !ok;
    }

    void Accept()
    {
        if (!_grid.EndEdit()) return;
        var perEndpoint = new Dictionary<string, RateLimit>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            int requests = Convert.ToInt32(row.Cells[1].Value);
            if (requests > 0)
                perEndpoint[(string)row.Cells[0].Value!] = new RateLimit { Requests = requests, PerSeconds = Convert.ToInt32(row.Cells[2].Value) };
        }
        Result = new RateLimitSettings
        {
            Enabled = _enabled.Checked,
            PerUser = new RateLimit { Requests = (int)_userRequests.Value, PerSeconds = (int)_userSeconds.Value },
            PerEndpoint = perEndpoint,
            MaxWaitSeconds = (int)_maxWait.Value,
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>One-line description for the Settings window.</summary>
    public static string Summary(RateLimitSettings s)
    {
        if (!s.Enabled) return "Off (server Retry-After still honoured)";
        var user = s.PerUser is { Requests: > 0 } u ? $"{u.Requests} per {u.PerSeconds}s per user" : "No per-user limit";
        int endpoints = s.PerEndpoint?.Values.Count(l => l.Requests > 0) ?? 0;
        return endpoints > 0 ? $"{user} · {endpoints} endpoint limit{(endpoints == 1 ? "" : "s")}" : user;
    }
}
