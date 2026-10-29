using Avalonia.Media;
using Avalonia.Threading;

namespace BuildPulse;

/// <summary>
/// Polls every configured server through <see cref="Poller"/>, merges the results and reports what changed:
/// the dashboard model, status toasts for builds that start, move stage or finish, and the pending approvals.
/// </summary>
sealed class Watcher : IDisposable
{
    sealed record BuildSnapshot(string Status, string? Result, string? Stage);

    readonly bool _demo;
    readonly DispatcherTimer _timer = new();
    readonly DateTime _startedAt = DateTime.Now;
    readonly Dictionary<string, BuildSnapshot> _known = [];
    /// <summary>Approvals the user just decided: hidden entirely while the server catches up.</summary>
    readonly Dictionary<string, DateTime> _decidedUntil = [];
    Dictionary<string, IPipelineSource> _sources = [];
    List<BuildInfo> _builds = [];
    List<ApprovalInfo> _approvals = [];
    Dictionary<string, string> _stages = [];
    Dictionary<string, IReadOnlyList<StageStatus>> _stageStatuses = [];
    string? _error;
    bool _pollFailed, _polling, _baselined;
    DateTime? _lastPoll;

    /// <summary>State, model or approvals changed.</summary>
    public event Action? Changed;
    /// <summary>A status change worth a toast: accent, title, body, link.</summary>
    public event Action<Color, string, string, string?>? Notified;

    public AppSettings Settings { get; private set; }
    public TrayState State { get; private set; } = TrayState.Unconfigured;
    /// <summary>The most recent finished run of some tracked pipeline failed.</summary>
    public bool FailureDot { get; private set; }
    public IReadOnlyList<ApprovalInfo> Approvals => _approvals;

    public Watcher(bool demo)
    {
        _demo = demo;
        Settings = demo ? DemoSource.CreateSettings() : AppSettings.Load();
        _timer.Tick += (_, _) => _ = PollAsync();
    }

    /// <summary>Starts watching with <paramref name="settings"/>, dropping everything known about the previous ones.</summary>
    public void Apply(AppSettings settings)
    {
        foreach (var source in _sources.Values) source.Dispose();
        Settings = settings;
        _sources = [];
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

        _timer.Interval = TimeSpan.FromSeconds(Math.Max(10, settings.PollSeconds));
        _timer.Start();
        UpdateState();
        _ = PollAsync();
    }

    public async Task PollAsync()
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

            foreach (var server in Settings.Servers)
            {
                if (server.Pipelines.Count == 0 || !sources.TryGetValue(server.Id, out var source)) continue;
                try
                {
                    var poll = await Poller.PollServerAsync(server, source, Settings.EnableManualValidationActions);
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

            if (sources != _sources) return;
            DiffBuilds(builds, stages);

            _builds = builds;
            _stages = stages;
            _stageStatuses = stageStatuses;
            _approvals = approvals;
            _error = failures.FirstOrDefault() ?? warnings.FirstOrDefault();
            _pollFailed = failures.Count > 0;
            _lastPoll = DateTime.Now;
            Log.Write($"Poll ok: {builds.Count} builds ({builds.Count(b => b.IsActive)} active), {approvals.Count} approvals, {failures.Count} server(s) failed");
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

    /// <summary>Approves/rejects (or resumes/rejects a manual validation) and hides it while the server catches up.</summary>
    public async Task DecideAsync(ApprovalInfo approval, bool approve, string comment)
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
        Notified?.Invoke(approve ? Palette.Green : Palette.Red, verb,
            $"{approval.PipelineName} · {approval.RunName}{(approval.StageName is { } s ? $" · {s}" : "")}", approval.WebUrl);
        _ = PollAsync();
    }

    /// <summary>True for an approval this app decided in the last couple of minutes.</summary>
    public bool WasDecided(string approvalKey) => _decidedUntil.ContainsKey(approvalKey);

    public void SaveCollapsed(IReadOnlyCollection<string> collapsed)
    {
        Settings.CollapsedPanels = collapsed.Order().ToList();
        Save();
    }

    public void SetStatusPopups(bool show)
    {
        Settings.ShowStatusPopups = show;
        Save();
    }

    void Save()
    {
        if (_demo) return;
        try { Settings.Save(); }
        catch (Exception ex) { Log.Write($"Saving settings failed: {ex.Message}"); }
    }

    bool HasPipelines => Settings.Servers.Any(s => s.Pipelines.Count > 0);

    /// <summary>Names the server in messages once there's more than one.</summary>
    string Prefix(ServerConfig server, string message) =>
        Settings.Servers.Count > 1 ? $"{server.DisplayName}: {message}" : message;

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
                        Notify(Palette.Blue, $"{b.DefinitionName} {(b.Status == "inProgress" ? "started" : "queued")}", b);
                    else if (b.FinishTime > _startedAt)
                        NotifyResult(b);
                }
                else if (old.Status != "completed" && b.Status == "completed")
                    NotifyResult(b);
                else if (old.Status == "notStarted" && b.Status == "inProgress")
                    Notify(Palette.Blue, $"{b.DefinitionName} started", b);
                else if (b.IsActive && stage != null && old.Stage != null && stage != old.Stage
                         && !stage.StartsWith(Poller.WaitingForApproval, StringComparison.Ordinal))
                    Notify(Palette.Blue, $"{b.DefinitionName} → {stage}", b);
            }
            _known[b.Key] = new BuildSnapshot(b.Status, b.Result, stage);
        }

        var current = builds.Select(b => b.Key).ToHashSet();
        foreach (var gone in _known.Keys.Where(id => !current.Contains(id)).ToList()) _known.Remove(gone);
        _baselined = true;
    }

    void NotifyResult(BuildInfo b) => Notify(Palette.ForResult(b.Result).Color, $"{b.DefinitionName}: {Palette.ResultText(b.Result)}", b);

    void Notify(Color accent, string title, BuildInfo b)
    {
        if (Settings.ShowStatusPopups) Notified?.Invoke(accent, title, Fmt.BuildLine(b), b.WebUrl);
    }

    void UpdateState()
    {
        if (_sources.Count == 0 || !HasPipelines) State = TrayState.Unconfigured;
        else if (_approvals.Count > 0) State = TrayState.Approval;
        else if (_pollFailed) State = TrayState.Error;
        else if (_builds.Any(b => b.IsActive)) State = TrayState.Running;
        else State = TrayState.Idle;

        FailureDot = _builds.Where(b => b.Status == "completed")
            .GroupBy(b => b.DefinitionKey)
            .Any(g => g.OrderByDescending(b => b.FinishTime).First().Result == "failed");
        Changed?.Invoke();
    }

    public string Tooltip()
    {
        var text = State switch
        {
            TrayState.Unconfigured => "Not configured",
            TrayState.Error => "Connection problem: " + _error,
            TrayState.Approval => $"{_approvals.Count} waiting for approval",
            TrayState.Running => $"{_builds.Count(b => b.IsActive)} running",
            _ => FailureDot ? "Idle · last run failed" : "All quiet",
        };
        text = AppInfo.Name + " – " + text;
        return text.Length > 127 ? text[..124] + "…" : text;
    }

    public DashboardModel Model(UpdateStatus update) =>
        new(Settings.Servers, _builds, _approvals, _stages, _stageStatuses, _lastPoll, _error, State, update, Settings.CollapsedPanels);

    public void Dispose()
    {
        _timer.Stop();
        foreach (var source in _sources.Values) source.Dispose();
    }
}
