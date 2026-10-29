using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DevOpsWatcher;

/// <summary>Edits <see cref="RateLimitSettings"/>: the per-user budget and one budget per endpoint. Closes with the result, or null.</summary>
sealed class RateLimitWindow : Window
{
    readonly CheckBox _enabled = new() { Content = "Limit requests to Azure DevOps" };
    readonly NumericUpDown _userRequests = FormGrid.Number(0, 100000, 0);
    readonly NumericUpDown _userSeconds = FormGrid.Number(1, 3600, 60);
    readonly NumericUpDown _maxWait = FormGrid.Number(0, 600, 0);
    readonly Dictionary<string, (NumericUpDown Requests, NumericUpDown Seconds)> _endpoints = [];
    readonly Grid _grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto") };

    public RateLimitWindow(RateLimitSettings current)
    {
        Title = "Rate limits";
        Width = 640;
        Height = 640;
        MinWidth = 460;
        MinHeight = 520;
        Background = Palette.Background;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        void Cell(Control control, int row, int column)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            control.Margin = new Thickness(column == 0 ? 0 : 8, 2, 0, 2);
            control.VerticalAlignment = VerticalAlignment.Center;
            _grid.Children.Add(control);
        }
        Cell(new TextBlock { Text = "Endpoint", Foreground = Palette.Muted }, 0, 0);
        Cell(new TextBlock { Text = "Requests", Foreground = Palette.Muted }, 0, 1);
        Cell(new TextBlock { Text = "Seconds", Foreground = Palette.Muted }, 0, 2);
        foreach (var endpoint in RateLimiter.Endpoints.All)
        {
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            int row = _grid.RowDefinitions.Count - 1;
            var requests = FormGrid.Number(0, 100000, 0);
            var seconds = FormGrid.Number(1, 100000, 60);
            Cell(new TextBlock { Text = endpoint }, row, 0);
            Cell(requests, row, 1);
            Cell(seconds, row, 2);
            _endpoints[endpoint] = (requests, seconds);
        }

        var ok = new Button { Content = "OK" };
        var defaults = new Button { Content = "Defaults" };
        var cancel = new Button { Content = "Cancel" };

        var form = new FormGrid();
        form.Row("", _enabled);
        form.Row("Per user", FormGrid.Flow(_userRequests, FormGrid.Unit("requests per"), _userSeconds, FormGrid.Unit("seconds")));
        form.Row("", FormGrid.Hint("Shared by all calls made with this server and sign-in. 0 = unlimited."));
        form.Row("Per endpoint", new ScrollViewer { Content = _grid }, fill: true);
        form.Row("", FormGrid.Hint("0 requests = only the per-user limit applies."));
        form.Row("Wait at most", FormGrid.Flow(_maxWait, FormGrid.Unit("seconds for a free slot")));
        form.Row("", FormGrid.Hint("After that the call fails. Azure DevOps's own Retry-After is always honoured."));
        var buttons = FormGrid.Buttons(ok, cancel);
        buttons.Children.Insert(0, defaults);
        form.Wide(buttons);
        Content = form;

        _enabled.IsCheckedChanged += (_, _) => UpdateEnabled();
        ok.Click += (_, _) => Close(Read());
        defaults.Click += (_, _) => Fill(new RateLimitSettings());
        cancel.Click += (_, _) => Close(null);
        Fill(current);
    }

    void Fill(RateLimitSettings settings)
    {
        _enabled.IsChecked = settings.Enabled;
        _userRequests.Value = settings.PerUser?.Requests ?? 0;
        _userSeconds.Value = settings.PerUser?.PerSeconds ?? 60;
        _maxWait.Value = settings.MaxWaitSeconds;
        var limits = new Dictionary<string, RateLimit>(settings.PerEndpoint ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var (endpoint, (requests, seconds)) in _endpoints)
        {
            var limit = limits.GetValueOrDefault(endpoint);
            requests.Value = limit?.Requests ?? 0;
            seconds.Value = limit?.PerSeconds ?? 60;
        }
        UpdateEnabled();
    }

    void UpdateEnabled()
    {
        bool on = _enabled.IsChecked == true;
        _userRequests.IsEnabled = _userSeconds.IsEnabled = _maxWait.IsEnabled = _grid.IsEnabled = on;
    }

    RateLimitSettings Read()
    {
        var perEndpoint = new Dictionary<string, RateLimit>();
        foreach (var (endpoint, (requests, seconds)) in _endpoints)
            if ((int)(requests.Value ?? 0) is > 0 and var n)
                perEndpoint[endpoint] = new RateLimit { Requests = n, PerSeconds = (int)(seconds.Value ?? 60) };
        return new RateLimitSettings
        {
            Enabled = _enabled.IsChecked == true,
            PerUser = new RateLimit { Requests = (int)(_userRequests.Value ?? 0), PerSeconds = (int)(_userSeconds.Value ?? 60) },
            PerEndpoint = perEndpoint,
            MaxWaitSeconds = (int)(_maxWait.Value ?? 0),
        };
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
