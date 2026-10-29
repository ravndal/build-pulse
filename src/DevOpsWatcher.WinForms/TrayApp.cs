using System.Media;

namespace DevOpsWatcher;

/// <summary>Owns the tray icon, polls every configured server, diffs state and raises popups.</summary>
sealed class TrayApp : ApplicationContext
{
    sealed record BuildSnapshot(string Status, string? Result, string? Stage);

    readonly bool _demo;
    readonly NotifyIcon _tray;
    readonly DashboardForm _dashboard;
    readonly ToolStripMenuItem _muteItem, _startupItem, _updateItem;
    readonly System.Windows.Forms.Timer _pollTimer = new();
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 30_000 };
    readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 120 };
    readonly DateTime _startedAt = DateTime.Now;

    readonly Dictionary<string, BuildSnapshot> _known = [];
    readonly Dictionary<string, ApprovalForm> _approvalForms = [];
    /// <summary>Approvals the user snoozed: no popup until the time passes.</summary>
    readonly Dictionary<string, DateTime> _snoozedUntil = [];
    /// <summary>Approvals the user just decided: hidden entirely while the server catches up.</summary>
    readonly Dictionary<string, DateTime> _decidedUntil = [];

    AppSettings _settings;
    /// <summary>One source per <see cref="ServerConfig.Id"/>; replaced as a whole when settings change.</summary>
    Dictionary<string, IPipelineSource> _sources = [];
    SettingsForm? _settingsForm;
    List<BuildInfo> _builds = [];
    List<ApprovalInfo> _approvals = [];
    Dictionary<string, string> _stages = [];
    Dictionary<string, IReadOnlyList<StageStatus>> _stageStatuses = [];
    string? _error;
    bool _pollFailed, _baselined, _polling, _failureDot;
    DateTime? _lastPoll;
    TrayState _state = TrayState.Unconfigured;
    int _frame;
    UpdateInfo? _pendingUpdate;
    UpdateStatus _update;

    public TrayApp(bool demo, bool openSettings = false, bool updated = false)
    {
        _demo = demo;
        _settings = demo ? DemoSource.CreateSettings() : AppSettings.Load();

        _dashboard = new DashboardForm();
        _dashboard.RefreshRequested += () => _ = PollAsync();
        _dashboard.SettingsRequested += ShowSettings;
        _dashboard.ApprovalClicked += ShowApproval;
        _dashboard.UpdateClicked += OnUpdateClicked;
        _dashboard.CollapsedChanged += SaveCollapsed;

        _muteItem = new ToolStripMenuItem("Mute status popups") { CheckOnClick = true };
        _muteItem.CheckedChanged += (_, _) =>
        {
            _settings.ShowStatusPopups = !_muteItem.Checked;
            if (!_demo) _settings.Save();
        };
        _startupItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = Startup.IsEnabled };
        _startupItem.CheckedChanged += (_, _) => Startup.Set(_startupItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => ToggleDashboard());
        menu.Items.Add("Refresh now", null, (_, _) => _ = PollAsync());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_muteItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        _updateItem = new ToolStripMenuItem();
        _updateItem.Click += (_, _) => OnUpdateClicked();
        menu.Items.Add(_updateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = IconFactory.Get(TrayState.Unconfigured, 0, false),
            Text = "DevOps Watcher",
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleDashboard(); };

        _pollTimer.Tick += async (_, _) => await PollAsync();
        _animTimer.Tick += (_, _) => { _frame++; RenderIcon(); };

        _update = new UpdateStatus(!demo && Updater.IsSupported ? UpdateStage.Idle : UpdateStage.Unsupported);
        if (_update.Stage != UpdateStage.Unsupported)
        {
            Updater.CleanUp();
            // Look for a new version shortly after start, then every 6 hours. This only labels the update button;
            // nothing is downloaded or installed until the user clicks it.
            _updateTimer.Tick += (_, _) =>
            {
                _updateTimer.Interval = 6 * 60 * 60 * 1000;
                _ = CheckForUpdateAsync(manual: false);
            };
            _updateTimer.Start();
        }
        SetUpdate(_update);

        ApplySettings(_settings);
        if (openSettings) ShowSettings();
        if (updated) Toast(Palette.Green, $"Updated to v{Updater.CurrentVersion.ToString(3)}", "DevOps Watcher is up to date.", null, force: true);
    }

    void OnUpdateClicked()
    {
        if (!_update.Clickable) return;
        if (_pendingUpdate != null) _ = InstallUpdateAsync();
        else _ = CheckForUpdateAsync(manual: true);
    }

    void SetUpdate(UpdateStatus status)
    {
        _update = status;
        _updateItem.Text = status.Label;
        _updateItem.Enabled = status.Clickable;
        _dashboard.SetModel(CreateModel());
    }

    async Task CheckForUpdateAsync(bool manual)
    {
        if (_update.Stage is UpdateStage.Unsupported or UpdateStage.Checking or UpdateStage.Installing) return;
        if (!manual && _pendingUpdate != null) return;
        var before = _update;
        if (manual) SetUpdate(new UpdateStatus(UpdateStage.Checking));
        try
        {
            var update = await Updater.CheckAsync();
            _pendingUpdate = update;
            if (update != null)
            {
                Log.Write($"Update v{update.Version} available");
                SetUpdate(new UpdateStatus(UpdateStage.Available, update.Version));
            }
            else if (manual) SetUpdate(new UpdateStatus(UpdateStage.UpToDate));
        }
        catch (Exception ex)
        {
            Log.Write($"Update check failed: {ex.GetType().Name}: {ex.Message}");
            SetUpdate(manual ? new UpdateStatus(UpdateStage.Failed) : before);
        }
    }

    /// <summary>Downloads the update the user clicked, swaps the exe and restarts into it.</summary>
    async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is not { } update) return;
        SetUpdate(new UpdateStatus(UpdateStage.Installing, update.Version));
        try
        {
            Log.Write($"Installing update v{update.Version}");
            var exe = await Updater.DownloadAsync(update);
            Updater.Install(update, exe);
            ExitThread();
        }
        catch (Exception ex)
        {
            Log.Write($"Update install failed: {ex.GetType().Name}: {ex.Message}");
            SetUpdate(new UpdateStatus(UpdateStage.Available, update.Version));
            Toast(Palette.Red, "Update failed", $"Couldn't install v{update.Version.ToString(3)} ({ex.Message}). Click to download it manually.",
                update.ReleaseUrl, force: true);
        }
    }

    void SaveCollapsed(IReadOnlyCollection<string> collapsed)
    {
        _settings.CollapsedPanels = collapsed.Order().ToList();
        if (_demo) return;
        try { _settings.Save(); }
        catch (Exception ex) { Log.Write($"Saving collapsed panels failed: {ex.Message}"); }
    }

    void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _muteItem.Checked = !settings.ShowStatusPopups;

        foreach (var source in _sources.Values) source.Dispose();
        _sources = [];
        foreach (var form in _approvalForms.Values.ToList()) form.Close();
        _known.Clear();
        _baselined = false;
        _builds = [];
        _approvals = [];
        _stages = [];
        _stageStatuses = [];
        _error = null;
        _pollFailed = false;
        _lastPoll = null;

        foreach (var server in settings.Servers.Where(s => s.Url != "" && s.HasCredentials))
            _sources[server.Id] = server.CreateSource(settings.RateLimits, _demo);

        _pollTimer.Interval = Math.Max(10, settings.PollSeconds) * 1000;
        _pollTimer.Start();
        UpdateState();

        if (!settings.IsConfigured) ShowSettings();
        else _ = PollAsync();
    }

    async Task PollAsync()
    {
        if (_polling || _sources.Count == 0 || !HasPipelines) return;
        _polling = true;
        var sources = _sources;
        try
        {
            var builds = new List<BuildInfo>();
            var approvals = new List<ApprovalInfo>();
            var stages = new Dictionary<string, string>();
            var stageStatuses = new Dictionary<string, IReadOnlyList<StageStatus>>();
            var warnings = new List<string>();
            var failures = new List<string>();

            foreach (var server in _settings.Servers)
            {
                if (server.Pipelines.Count == 0 || !sources.TryGetValue(server.Id, out var source)) continue;
                try
                {
                    var poll = await Poller.PollServerAsync(server, source, _settings.EnableManualValidationActions);
                    builds.AddRange(poll.Builds);
                    approvals.AddRange(poll.Approvals);
                    foreach (var (key, stage) in poll.Stages) stages[key] = stage;
                    foreach (var (key, list) in poll.StageStatuses) stageStatuses[key] = list;
                    warnings.AddRange(poll.Warnings.Select(w => Prefix(server, w)));
                }
                catch (Exception ex)
                {
                    if (sources != _sources) return; // settings changed mid-poll
                    Log.Write($"Poll failed for {server.DisplayName}: {ex.GetType().Name}: {ex.Message}");
                    failures.Add(Prefix(server, ex.Message));
                    // Keep what we last knew so a flaky server's runs don't vanish and then pop up again as new.
                    var prefix = server.Id + "|";
                    builds.AddRange(_builds.Where(b => b.ServerId == server.Id));
                    approvals.AddRange(_approvals.Where(a => a.ServerId == server.Id));
                    foreach (var (key, stage) in _stages.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))) stages[key] = stage;
                    foreach (var (key, list) in _stageStatuses.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))) stageStatuses[key] = list;
                }
            }

            builds = builds.OrderByDescending(b => b.QueueTime ?? DateTime.MinValue).ToList();

            var now = DateTime.Now;
            foreach (var expired in _decidedUntil.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
                _decidedUntil.Remove(expired);
            approvals = approvals.Where(a => !_decidedUntil.ContainsKey(a.Key)).ToList();

            if (sources != _sources) return; // settings changed mid-poll
            DiffBuilds(builds, stages);
            SyncApprovals(approvals);

            _builds = builds;
            _stages = stages;
            _stageStatuses = stageStatuses;
            _approvals = approvals;
            _error = failures.FirstOrDefault() ?? warnings.FirstOrDefault();
            _pollFailed = failures.Count > 0;
            _lastPoll = DateTime.Now;
            Log.Write($"Poll ok: {builds.Count} builds ({builds.Count(b => b.IsActive)} active), {approvals.Count} approvals, {failures.Count} server(s) failed"
                + string.Concat(builds.Where(b => b.IsActive).Select(b => $" | {b.DefinitionName} #{b.Number} {b.Status} stage='{stages.GetValueOrDefault(b.Key)}'"))
                + string.Concat(approvals.Select(a => $" | approval {a.PipelineName} run={a.RunId} stage='{a.StageName}' canApprove={a.CanApprove}")));
        }
        catch (Exception ex)
        {
            if (sources != _sources) return;
            Log.Write($"Poll failed: {ex.GetType().Name}: {ex.Message}");
            _error = ex.Message;
            _pollFailed = true;
        }
        finally
        {
            _polling = false;
            UpdateState();
        }
    }

    bool HasPipelines => _settings.Servers.Any(s => s.Pipelines.Count > 0);

    /// <summary>Names the server in messages once there's more than one.</summary>
    string Prefix(ServerConfig server, string message) =>
        _settings.Servers.Count > 1 ? $"{server.DisplayName}: {message}" : message;

    void DiffBuilds(List<BuildInfo> builds, Dictionary<string, string> stages)
    {
        foreach (var b in builds)
        {
            stages.TryGetValue(b.Key, out var stage);
            if (_baselined)
            {
                if (!_known.TryGetValue(b.Key, out var old))
                {
                    if (b.IsActive)
                        Toast(Palette.Blue, $"{b.DefinitionName} {(b.Status == "inProgress" ? "started" : "queued")}", Fmt.BuildLine(b), b.WebUrl);
                    else if (b.FinishTime > _startedAt)
                        ToastResult(b);
                }
                else if (old.Status != "completed" && b.Status == "completed")
                {
                    ToastResult(b);
                }
                else if (old.Status == "notStarted" && b.Status == "inProgress")
                {
                    Toast(Palette.Blue, $"{b.DefinitionName} started", Fmt.BuildLine(b), b.WebUrl);
                }
                else if (b.IsActive && stage != null && old.Stage != null && stage != old.Stage
                         && !stage.StartsWith(Poller.WaitingForApproval, StringComparison.Ordinal))
                {
                    Toast(Palette.Blue, $"{b.DefinitionName} → {stage}", Fmt.BuildLine(b), b.WebUrl);
                }
            }
            _known[b.Key] = new BuildSnapshot(b.Status, b.Result, stage);
        }

        var current = builds.Select(b => b.Key).ToHashSet();
        foreach (var gone in _known.Keys.Where(id => !current.Contains(id)).ToList()) _known.Remove(gone);
        _baselined = true;
    }

    void ToastResult(BuildInfo b)
    {
        var (color, _) = Palette.ForResult(b.Result);
        Toast(color, $"{b.DefinitionName}: {Palette.ResultText(b.Result)}", Fmt.BuildLine(b), b.WebUrl);
    }

    void SyncApprovals(List<ApprovalInfo> approvals)
    {
        var pendingIds = approvals.Select(a => a.Key).ToHashSet();
        foreach (var (id, form) in _approvalForms.Where(kv => !pendingIds.Contains(kv.Key)).ToList())
        {
            form.Close();
            if (!_decidedUntil.ContainsKey(id))
                Toast(Palette.Gray, "Approval no longer pending", $"{form.Approval.PipelineName} · {form.Approval.RunName}", form.Approval.WebUrl);
        }

        var now = DateTime.Now;
        foreach (var a in approvals)
        {
            if (_approvalForms.TryGetValue(a.Key, out var form)) { form.UpdateApproval(a); continue; }
            if (_snoozedUntil.TryGetValue(a.Key, out var until) && until > now) continue;
            OpenApprovalForm(a, playSound: true);
        }
    }

    void ShowApproval(ApprovalInfo approval)
    {
        _dashboard.HideFlyout();
        _snoozedUntil.Remove(approval.Key);
        if (_approvalForms.TryGetValue(approval.Key, out var existing)) existing.Activate();
        else OpenApprovalForm(approval, playSound: false);
    }

    void OpenApprovalForm(ApprovalInfo approval, bool playSound)
    {
        var form = new ApprovalForm(approval) { Decide = DecideAsync };
        form.Snoozed += a => _snoozedUntil[a.Key] = DateTime.Now.AddMinutes(15);
        form.FormClosed += (_, _) =>
        {
            if (_approvalForms.TryGetValue(approval.Key, out var current) && current == form)
                _approvalForms.Remove(approval.Key);
        };
        _approvalForms[approval.Key] = form;
        PopupHost.Show(form, sticky: true);
        if (playSound) SystemSounds.Exclamation.Play();
    }

    async Task DecideAsync(ApprovalInfo approval, bool approve, string comment)
    {
        if (!_sources.TryGetValue(approval.ServerId, out var source)) throw new InvalidOperationException("Not connected.");
        try
        {
            if (approval.Kind == ApprovalKind.ManualValidation)
                await source.CompleteManualValidationAsync(approval, resume: approve);
            else
                await source.UpdateApprovalAsync(approval.Project, approval.Id, approve, comment);
            Log.Write($"{approval.Kind} {approval.Id} {(approve ? "approved" : "rejected")}");
        }
        catch (Exception ex)
        {
            Log.Write($"Approval {approval.Id} update failed: {ex.Message}");
            throw;
        }

        _decidedUntil[approval.Key] = DateTime.Now.AddMinutes(2);
        _approvals.RemoveAll(a => a.Key == approval.Key);
        UpdateState();
        var verb = approval.Kind == ApprovalKind.ManualValidation ? (approve ? "Resumed" : "Rejected") : (approve ? "Approved" : "Rejected");
        Toast(approve ? Palette.Green : Palette.Red, verb,
            $"{approval.PipelineName} · {approval.RunName}{(approval.StageName is { } s ? $" · {s}" : "")}", approval.WebUrl, force: true);
        _ = PollAsync();
    }

    void Toast(Color accent, string title, string body, string? url, bool force = false)
    {
        if (!force && !_settings.ShowStatusPopups) return;
        PopupHost.Show(new ToastForm(accent, title, body, url), sticky: false);
    }

    void UpdateState()
    {
        if (_sources.Count == 0 || !HasPipelines) _state = TrayState.Unconfigured;
        else if (_approvals.Count > 0) _state = TrayState.Approval;
        else if (_pollFailed) _state = TrayState.Error;
        else if (_builds.Any(b => b.IsActive)) _state = TrayState.Running;
        else _state = TrayState.Idle;

        // Red dot when the most recent finished run of any tracked pipeline failed.
        _failureDot = _builds.Where(b => b.Status == "completed")
            .GroupBy(b => b.DefinitionKey)
            .Any(g => g.OrderByDescending(b => b.FinishTime).First().Result == "failed");

        bool animate = _state is TrayState.Running or TrayState.Approval;
        if (animate && !_animTimer.Enabled) _animTimer.Start();
        if (!animate) _animTimer.Stop();

        RenderIcon();
        _tray.Text = Tooltip();
        _dashboard.SetModel(CreateModel());
    }

    void RenderIcon()
    {
        int frame = _state switch
        {
            TrayState.Running => _frame % 12,
            TrayState.Approval => _frame / 4 % 2,
            _ => 0,
        };
        _tray.Icon = IconFactory.Get(_state, frame, _failureDot);
    }

    string Tooltip()
    {
        var text = _state switch
        {
            TrayState.Unconfigured => "Not configured",
            TrayState.Error => "Connection problem: " + _error,
            TrayState.Approval => $"{_approvals.Count} waiting for approval",
            TrayState.Running => $"{_builds.Count(b => b.IsActive)} running",
            _ => _failureDot ? "Idle · last run failed" : "All quiet",
        };
        text = "DevOps Watcher – " + text;
        return text.Length > 127 ? text[..124] + "…" : text;
    }

    DashboardModel CreateModel() =>
        new(_settings.Servers, _builds, _approvals, _stages, _stageStatuses, _lastPoll, _error, _state, _update, _settings.CollapsedPanels);

    void ToggleDashboard()
    {
        if (_dashboard.Visible) { _dashboard.HideFlyout(); return; }
        if (_dashboard.RecentlyHidden) return;
        _dashboard.SetModel(CreateModel());
        _dashboard.ShowNearTray();
    }

    void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_settings, _demo);
        _settingsForm.FormClosed += (_, _) =>
        {
            var result = _settingsForm?.Result;
            Log.Write(result != null ? "Settings window closed, applying new settings" : "Settings window closed without saving");
            _settingsForm = null;
            _startupItem.Checked = Startup.IsEnabled;
            if (result != null) ApplySettings(result);
        };
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    protected override void ExitThreadCore()
    {
        _pollTimer.Stop();
        _animTimer.Stop();
        _updateTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        foreach (var source in _sources.Values) source.Dispose();
        base.ExitThreadCore();
    }
}
