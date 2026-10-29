using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DevOpsWatcher;

/// <summary>The servers to watch plus general options. Raises <see cref="Saved"/> with the new settings.</summary>
sealed class SettingsWindow : Window
{
    readonly AppSettings _current;
    readonly bool _demo;
    readonly List<ServerConfig> _servers;
    // Every stored token seen here; whichever ones the kept settings don't reference are deleted on close.
    readonly HashSet<string> _seenPats;
    bool _saved;

    readonly ListBox _serverList = new() { MinHeight = 120 };
    readonly Button _edit = new() { Content = "Edit…" };
    readonly Button _remove = new() { Content = "Remove" };
    readonly NumericUpDown _poll;
    readonly CheckBox _popups = new() { Content = "Show popups for status changes (approvals always pop up)" };
    readonly CheckBox _startup = new()
    {
        Content = OperatingSystem.IsMacOS() ? "Open at login" : OperatingSystem.IsWindows() ? "Start with Windows" : "Start at login",
    };
    readonly CheckBox _validationActions = new() { Content = "Experimental: Resume/Reject Azure DevOps Manual validation from the popup" };
    readonly TextBlock _rateLimitsSummary = FormGrid.Hint();
    readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Palette.Red) };

    RateLimitSettings _rateLimits;

    public event Action<AppSettings>? Saved;

    public SettingsWindow(AppSettings current, bool demo)
    {
        _current = current;
        _demo = demo;
        _servers = current.Servers.Select(s => s.Clone()).ToList();
        _seenPats = PatsOf(_servers);
        _rateLimits = current.RateLimits;
        Closed += (_, _) =>
        {
            var kept = PatsOf(_saved ? _servers : _current.Servers);
            foreach (var pat in _seenPats.Except(kept)) Secrets.Delete(pat);
        };

        Title = "DevOps Watcher – Settings";
        Width = 620;
        Height = 480;
        MinWidth = 500;
        MinHeight = 420;
        Background = Palette.Background;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _poll = FormGrid.Number(10, 600, current.PollSeconds);
        _popups.IsChecked = current.ShowStatusPopups;
        _startup.IsChecked = Startup.IsEnabled;
        _startup.IsVisible = Startup.IsSupported;
        _validationActions.IsChecked = current.EnableManualValidationActions;
        _rateLimitsSummary.Text = RateLimitWindow.Summary(_rateLimits);

        var add = new Button { Content = "Add server…" };
        var rateLimits = new Button { Content = "Change…" };
        var save = new Button { Content = "Save" };
        var cancel = new Button { Content = "Cancel" };

        var form = new FormGrid();
        form.Row("Servers", _serverList, fill: true);
        form.Row("", FormGrid.Flow(add, _edit, _remove));
        form.Row("Poll every", FormGrid.Flow(_poll, FormGrid.Unit("seconds")));
        form.Row("Rate limits", FormGrid.Flow(rateLimits, _rateLimitsSummary));
        form.Row("", _popups);
        form.Row("", _startup);
        form.Row("", _validationActions);
        form.Row("", _status);
        form.Wide(FormGrid.Buttons(save, cancel));
        Content = form;

        add.Click += async (_, _) => await EditServerAsync(null);
        _edit.Click += async (_, _) => { if (Selected is { } s) await EditServerAsync(s); };
        _serverList.DoubleTapped += async (_, _) => { if (Selected is { } s) await EditServerAsync(s); };
        _remove.Click += async (_, _) => await RemoveSelectedAsync();
        _serverList.SelectionChanged += (_, _) => UpdateButtons();
        rateLimits.Click += async (_, _) =>
        {
            var result = await new RateLimitWindow(_rateLimits).ShowDialog<RateLimitSettings?>(this);
            if (result == null) return;
            _rateLimits = result;
            _rateLimitsSummary.Text = RateLimitWindow.Summary(_rateLimits);
            Log.Write($"Settings: rate limits changed ({_rateLimitsSummary.Text})");
        };
        save.Click += async (_, _) => await SaveAndCloseAsync();
        cancel.Click += (_, _) => Close();

        RefreshList();
        // Nothing to show without a server, so start with adding one.
        Opened += async (_, _) => { if (_servers.Count == 0) await EditServerAsync(null); };
    }

    ServerConfig? Selected => (_serverList.SelectedItem as Control)?.Tag as ServerConfig;

    async Task EditServerAsync(ServerConfig? existing)
    {
        var server = await new ServerWindow(existing, _rateLimits, _demo).ShowDialog<ServerConfig?>(this);
        if (server == null) return;
        if (server.ProtectedPat != null) _seenPats.Add(server.ProtectedPat);
        int index = existing != null ? _servers.IndexOf(existing) : -1;
        if (index >= 0) _servers[index] = server;
        else _servers.Add(server);
        RefreshList(select: server);
    }

    async Task RemoveSelectedAsync()
    {
        if (Selected is not { } server) return;
        if (!await Dialogs.ConfirmAsync(this, "Remove server", $"Stop watching {server.DisplayName}?", "Remove")) return;
        _servers.Remove(server);
        Log.Write($"Settings: removed server {server.Kind} {server.DisplayName}");
        RefreshList();
    }

    void RefreshList(ServerConfig? select = null)
    {
        _serverList.Items.Clear();
        foreach (var server in _servers)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Tag = server };
            var logo = ProviderLogo.Create(server.Kind, 16);
            logo.Margin = new Thickness(0, 0, 10, 0);
            var name = new TextBlock { Text = server.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var count = new TextBlock
            {
                Text = $"{server.Pipelines.Count} tracked",
                Foreground = Palette.Muted,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 1);
            Grid.SetColumn(count, 2);
            row.Children.AddRange([logo, name, count]);
            ToolTip.SetTip(row, server.KindName);
            _serverList.Items.Add(row);
            if (server == select) _serverList.SelectedItem = row;
        }
        if (_servers.Count > 0) _status.Text = "";
        UpdateButtons();
    }

    void UpdateButtons() => _edit.IsEnabled = _remove.IsEnabled = Selected != null;

    async Task SaveAndCloseAsync()
    {
        Log.Write($"Settings: Save clicked ({_servers.Count} servers, {_servers.Sum(s => s.Pipelines.Count)} pipelines)");
        if (_servers.Count == 0) { _status.Text = "Add a server to watch."; return; }

        var result = new AppSettings
        {
            Servers = _servers,
            PollSeconds = (int)(_poll.Value ?? 20),
            ShowStatusPopups = _popups.IsChecked == true,
            CollapsedPanels = _current.CollapsedPanels,
            EnableManualValidationActions = _validationActions.IsChecked == true,
            RateLimits = _rateLimits,
        };

        if (Startup.IsSupported && _startup.IsChecked != Startup.IsEnabled)
        {
            try { Startup.Set(_startup.IsChecked == true); }
            catch (Exception ex) { Log.Write($"Settings: start at login failed: {ex.Message}"); }
        }

        if (!_demo)
        {
            try { result.Save(); }
            catch (Exception ex)
            {
                Log.Write($"Settings: writing settings.json failed: {ex.Message}");
                await Dialogs.ErrorAsync(this, $"Couldn't save settings:\n{ex.Message}");
                return;
            }
        }
        Log.Write($"Settings: saved {result.Servers.Count} servers");
        _saved = !_demo;
        Saved?.Invoke(result);
        Close();
    }

    static HashSet<string> PatsOf(IEnumerable<ServerConfig> servers) =>
        servers.Select(s => s.ProtectedPat).OfType<string>().ToHashSet();
}
