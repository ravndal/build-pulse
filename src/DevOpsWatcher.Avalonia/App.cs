using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace DevOpsWatcher;

/// <summary>Tray / menu-bar icon with a menu, the dashboard flyout next to it, popups, settings and self-update.</summary>
sealed class App : Application
{
    public static bool Demo { get; set; }
    public static bool OpenSettings { get; set; }
    public static bool Updated { get; set; }

    readonly Dictionary<string, ApprovalWindow> _approvalWindows = [];
    /// <summary>Approvals the user snoozed: no popup until the time passes.</summary>
    readonly Dictionary<string, DateTime> _snoozedUntil = [];
    readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    IClassicDesktopStyleApplicationLifetime? _desktop;
    Watcher? _watcher;
    TrayIcon? _tray;
    Window? _flyout;
    DateTime _flyoutShownAt;
    DashboardView? _dashboard;
    SettingsWindow? _settingsWindow;
    NativeMenuItem? _muteItem, _updateItem;
    UpdateStatus _update = new(UpdateStage.Unsupported);
    UpdateInfo? _pendingUpdate;
    int _frame;

    public override void Initialize()
    {
        Name = "DevOps Watcher";
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        // Headless (--screenshots) has no lifetime: no tray, no polling.
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _desktop = desktop;

        _watcher = new Watcher(Demo);
        _dashboard = new DashboardView();
        _dashboard.RefreshRequested += () => _ = _watcher.PollAsync();
        _dashboard.SettingsRequested += ShowSettings;
        _dashboard.Navigated += () => _flyout?.Hide();
        _dashboard.CollapsedChanged += _watcher.SaveCollapsed;
        _dashboard.ApprovalClicked += ShowApproval;
        _dashboard.UpdateClicked += OnUpdateClicked;

        _flyout = new Window
        {
            Title = "DevOps Watcher",
            WindowDecorations = WindowDecorations.None,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            Topmost = true,
            CanResize = false,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Content = _dashboard,
        };
        // On macOS the window can lose focus right as it opens (the tray menu is still closing), so ignore that.
        _flyout.Deactivated += (_, _) =>
        {
            if (DateTime.UtcNow - _flyoutShownAt > TimeSpan.FromMilliseconds(500)) _flyout.Hide();
        };

        _muteItem = new NativeMenuItem("Mute status popups") { ToggleType = MenuItemToggleType.CheckBox };
        _muteItem.Click += (_, _) => _watcher.SetStatusPopups(!_watcher.Settings.ShowStatusPopups);
        var startup = new NativeMenuItem(OperatingSystem.IsMacOS() ? "Open at login" : "Start with Windows")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = Startup.IsEnabled,
            IsVisible = Startup.IsSupported,
        };
        startup.Click += (_, _) =>
        {
            try { Startup.Set(!Startup.IsEnabled); }
            catch (Exception ex) { Log.Write($"Start at login failed: {ex.Message}"); }
            startup.IsChecked = Startup.IsEnabled;
        };
        _updateItem = Item("", OnUpdateClicked);

        var menu = new NativeMenu();
        menu.Add(Item("Open dashboard", ToggleFlyout));
        menu.Add(Item("Refresh now", () => _ = _watcher.PollAsync()));
        menu.Add(Item("Settings…", ShowSettings));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(_muteItem);
        menu.Add(startup);
        menu.Add(_updateItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Quit", () => desktop.Shutdown()));
        menu.Opening += (_, _) => _muteItem.IsChecked = !_watcher.Settings.ShowStatusPopups;

        _tray = new TrayIcon { Menu = menu, ToolTipText = "DevOps Watcher", IsVisible = true };
        _tray.Clicked += (_, _) => ToggleFlyout();
        TrayIcon.SetIcons(this, [_tray]);

        _watcher.Changed += Refresh;
        _watcher.Notified += (accent, title, body, url) => Toast(accent, title, body, url);
        Ticker.Subscribe(() => { if (_watcher.State is TrayState.Running or TrayState.Approval) { _frame++; RenderIcon(); } });
        desktop.Exit += (_, _) => { _updateTimer.Stop(); _watcher.Dispose(); };

        StartUpdater();
        _watcher.Apply(_watcher.Settings);
        if (OpenSettings || !_watcher.Settings.IsConfigured) ShowSettings();
        if (Updated) Toast(Palette.Green, $"Updated to v{Updater.CurrentVersion.ToString(3)}", "DevOps Watcher is up to date.", null);
    }

    static NativeMenuItem Item(string header, Action onClick)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => onClick();
        return item;
    }

    void Refresh()
    {
        RenderIcon();
        _tray!.ToolTipText = _watcher!.Tooltip();
        _dashboard!.SetModel(_watcher.Model(_update));
        SyncApprovals();
    }

    void RenderIcon()
    {
        // The Ticker runs at ~11 fps; the tray only needs a few frames a second.
        int step = _frame / 3;
        int frame = _watcher!.State switch
        {
            TrayState.Running => step % 12,
            TrayState.Approval => step / 2 % 2,
            _ => 0,
        };
        _tray!.Icon = Badge.TrayIcon(_watcher.State, frame, _watcher.FailureDot);
    }

    static void Toast(Color accent, string title, string body, string? url) =>
        PopupHost.Show(new ToastWindow(accent, title, body, url), sticky: false);

    // ---- Approvals ----

    /// <summary>Closes popups for approvals that went away, updates open ones and pops up new ones unless snoozed.</summary>
    void SyncApprovals()
    {
        var pending = _watcher!.Approvals.ToDictionary(a => a.Key);
        foreach (var (key, window) in _approvalWindows.Where(kv => !pending.ContainsKey(kv.Key)).ToList())
        {
            _approvalWindows.Remove(key);
            window.Close();
            if (!_watcher.WasDecided(key))
                Toast(Palette.Gray, "Approval no longer pending", $"{window.Approval.PipelineName} · {window.Approval.RunName}", window.Approval.WebUrl);
        }
        var now = DateTime.Now;
        foreach (var a in pending.Values)
        {
            if (_approvalWindows.TryGetValue(a.Key, out var window)) { window.UpdateApproval(a); continue; }
            if (_snoozedUntil.TryGetValue(a.Key, out var until) && until > now) continue;
            OpenApproval(a);
        }
    }

    void ShowApproval(ApprovalInfo approval)
    {
        _flyout?.Hide();
        _snoozedUntil.Remove(approval.Key);
        if (_approvalWindows.TryGetValue(approval.Key, out var existing)) existing.Activate();
        else OpenApproval(approval);
    }

    void OpenApproval(ApprovalInfo approval)
    {
        var window = new ApprovalWindow(approval) { Decide = _watcher!.DecideAsync };
        window.Snoozed += a => _snoozedUntil[a.Key] = DateTime.Now.AddMinutes(15);
        window.Closed += (_, _) =>
        {
            if (_approvalWindows.TryGetValue(approval.Key, out var current) && current == window)
                _approvalWindows.Remove(approval.Key);
        };
        _approvalWindows[approval.Key] = window;
        PopupHost.Show(window, sticky: true);
    }

    // ---- Settings ----

    void ShowSettings()
    {
        _flyout?.Hide();
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(_watcher!.Settings, Demo);
        _settingsWindow.Saved += settings =>
        {
            foreach (var window in _approvalWindows.Values.ToList()) window.Close();
            _watcher.Apply(settings);
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // ---- Self-update ----

    void StartUpdater()
    {
        if (Demo || !Updater.IsSupported) { SetUpdate(new UpdateStatus(UpdateStage.Unsupported)); return; }
        Updater.CleanUp();
        SetUpdate(new UpdateStatus(UpdateStage.Idle));
        // First check shortly after start, then a few times a day; silent unless something's found.
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(6);
            _ = CheckForUpdateAsync(manual: false);
        };
        _updateTimer.Start();
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
        _updateItem!.Header = status.Label;
        _updateItem.IsEnabled = status.Clickable;
        _dashboard!.SetModel(_watcher!.Model(status));
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

    /// <summary>Downloads the update the user clicked, swaps the executable and restarts into it.</summary>
    async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is not { } update) return;
        SetUpdate(new UpdateStatus(UpdateStage.Installing, update.Version));
        try
        {
            Log.Write($"Installing update v{update.Version}");
            var exe = await Updater.DownloadAsync(update);
            Updater.Install(update, exe);
            _desktop!.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Write($"Update install failed: {ex.GetType().Name}: {ex.Message}");
            SetUpdate(new UpdateStatus(UpdateStage.Available, update.Version));
            Toast(Palette.Red, "Update failed", $"Couldn't install v{update.Version.ToString(3)} ({ex.Message}). Click to download it manually.",
                update.ReleaseUrl);
        }
    }

    // ---- Flyout ----

    void ToggleFlyout()
    {
        if (_flyout!.IsVisible) { _flyout.Hide(); return; }
        _dashboard!.ShowTab(DashboardView.Tab.Overview);
        // Let the tray menu finish closing before the window takes focus.
        Dispatcher.UIThread.Post(() =>
        {
            PlaceFlyout();
            _flyoutShownAt = DateTime.UtcNow;
            _flyout.Show();
            _flyout.Activate();
        });
    }

    /// <summary>Below the menu bar on macOS, above the taskbar elsewhere; right-aligned either way.</summary>
    void PlaceFlyout()
    {
        var screen = _flyout!.Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        _dashboard!.MaxContentHeight = area.Height / scale * 0.7;
        // Measure the content, not the window: once shown and hidden, the window measures as 0×0.
        _dashboard.Measure(Size.Infinity);
        var size = PixelSize.FromSize(_dashboard.DesiredSize, scale);
        int margin = (int)(8 * scale);
        int x = area.Right - size.Width - margin;
        int y = OperatingSystem.IsMacOS() ? area.Y + margin : area.Bottom - size.Height - margin;
        _flyout.Position = new PixelPoint(x, y);
    }
}
