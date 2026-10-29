using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace BuildPulse;

/// <summary>
/// Adds or edits one server. Adding starts with a choice of target (Azure DevOps, Azure DevOps Server, GitHub,
/// GitHub Enterprise); then only the values that target needs are asked for, followed by sign-in and the pipelines to track.
/// Closes with the server, or null when cancelled.
/// </summary>
sealed partial class ServerWindow : Window
{
    /// <summary>What the user connects to; finer than <see cref="ServerKind"/> because each asks for different values.</summary>
    enum Target { AzureDevOps, AzureDevOpsServer, GitHub, GitHubEnterprise }

    const string AzureDevOpsHost = "https://dev.azure.com";

    readonly ServerConfig _current;
    readonly RateLimitSettings _rateLimits;
    readonly bool _demo;
    readonly bool _adding;
    readonly Dictionary<string, TrackedPipeline> _tracked;
    readonly HashSet<TreeViewItem> _loading = [];

    readonly Control _picker;
    readonly FormGrid _details = new();
    readonly ContentControl _targetLogo = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _targetName = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _changeTarget = new() { Content = "Change", Classes = { "link" }, Padding = new Thickness(4, 0) };
    readonly TextBox _org = new() { PlaceholderText = "yourorg" };
    readonly TextBox _project = new() { PlaceholderText = "Optional" };
    readonly TextBox _url = new();
    readonly TextBlock _urlHint = FormGrid.Hint();
    readonly TextBox _pat = new() { PasswordChar = '•' };
    readonly TextBlock _patHint = FormGrid.Hint();
    readonly RadioButton _usePat = new() { Content = "Personal access token", GroupName = "auth", IsChecked = true };
    readonly RadioButton _useWindows = new() { Content = "Windows (integrated)", GroupName = "auth" };
    readonly Button _connect = new() { Content = "Connect" };
    readonly TextBlock _status = new() { Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox _filter = new();
    readonly TreeView _tree = new() { MinHeight = 160 };
    readonly TextBlock _trackedCount = FormGrid.Hint();

    // The token this window stored (a Keychain item on macOS) and the text it came from, so Connect and OK
    // reuse one item instead of adding a new one on every attempt; it's deleted if the window is cancelled.
    string? _storedPat;
    string? _storedPatText;
    bool _accepted;
    readonly TextBlock _urlCaption;

    /// <summary>Rows that only some targets have: caption and control shown or hidden together.</summary>
    readonly List<(Control[] Controls, Func<Target, bool> Shown)> _targetRows = [];

    Target _target;
    IPipelineSource? _client;
    List<(ProjectInfo Project, List<DefinitionInfo>? Definitions)> _projects = [];
    /// <summary>True once the server has listed its projects, so tracked pipelines it lacks can be shown as missing.</summary>
    bool _listed;

    const string MissingTag = "missing";

    ServerKind Kind => _target is Target.GitHub or Target.GitHubEnterprise ? ServerKind.GitHub : ServerKind.AzureDevOps;

    /// <param name="existing">The server to edit, or null to add one.</param>
    public ServerWindow(ServerConfig? existing, RateLimitSettings rateLimits, bool demo)
    {
        _current = existing?.Clone() ?? new ServerConfig();
        _rateLimits = rateLimits;
        _demo = demo;
        _adding = existing == null;
        _tracked = _current.Pipelines.ToDictionary(p => p.Key);

        Title = _adding ? "Add server" : $"Edit server – {_current.DisplayName}";
        Width = 640;
        Height = 640;
        MinWidth = 520;
        MinHeight = 480;
        Background = Palette.Background;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _target = TargetOf(_current);
        if (_target == Target.AzureDevOps) _org.Text = OrganizationOf(_current.Url) ?? "";
        else if (_target != Target.GitHub) _url.Text = _current.Url;
        _project.Text = _current.Project ?? "";
        _useWindows.IsChecked = _current.UseWindowsAuth;
        _usePat.IsChecked = !_current.UseWindowsAuth;
        _pat.PlaceholderText = _current.ProtectedPat != null ? "Saved – leave empty to keep" : "Paste your token";

        void Row(string caption, Control control, Func<Target, bool>? shown = null, bool fill = false)
        {
            var label = _details.Row(caption, control, fill);
            if (shown != null) _targetRows.Add(([label, control], shown));
        }
        Row("Type", FormGrid.Flow(_targetLogo, _targetName, _changeTarget));
        Row("Organization", _org, t => t == Target.AzureDevOps);
        Row("", FormGrid.Hint("Your organization's name, as in dev.azure.com/yourorg."), t => t == Target.AzureDevOps);
        Row("Project", _project, t => t == Target.AzureDevOps);
        Row("", FormGrid.Hint("Only list this project. Leave empty to see every project in the organization."), t => t == Target.AzureDevOps);
        _urlCaption = _details.Row("Server URL", _url);
        _targetRows.Add(([_urlCaption, _url], t => t is Target.AzureDevOpsServer or Target.GitHubEnterprise));
        Row("", _urlHint, t => t is Target.AzureDevOpsServer or Target.GitHubEnterprise);
        Row("Sign in with", FormGrid.Flow(_usePat, _useWindows), t => t == Target.AzureDevOpsServer && OperatingSystem.IsWindows());
        Row("Token", _pat);
        Row("", _patHint);
        var connectRow = new DockPanel();
        DockPanel.SetDock(_connect, Dock.Left);
        _status.Margin = new Thickness(10, 0, 0, 0);
        connectRow.Children.AddRange([_connect, _status]);
        Row("", connectRow);
        Row("Filter", _filter);
        Row("Pipelines", _tree, fill: true);
        Row("", _trackedCount);
        var ok = new Button { Content = _adding ? "Add" : "OK" };
        var cancel = new Button { Content = "Cancel" };
        _details.Wide(FormGrid.Buttons(ok, cancel));

        _picker = BuildPicker();
        var root = new Panel();
        root.Children.AddRange([_details, _picker]);
        Content = root;

        _changeTarget.IsVisible = _adding;
        _changeTarget.Click += (_, _) => ShowPicker();
        _usePat.IsCheckedChanged += (_, _) => _pat.IsEnabled = _usePat.IsChecked == true;
        _connect.Click += async (_, _) => await ConnectAsync();
        _filter.TextChanged += (_, _) => RebuildTree();
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close(null);

        _pat.IsEnabled = _usePat.IsChecked == true;
        ApplyTarget(_target);
        if (_adding) ShowPicker();
        else _picker.IsVisible = false;
        UpdateTrackedCount();
        Opened += async (_, _) =>
        {
            if (_adding) return;
            if (_current.Url != "" && _current.HasCredentials) await ConnectAsync();
            else _pat.Focus();
        };
        Closed += (_, _) =>
        {
            _client?.Dispose();
            if (!_accepted) Secrets.Delete(_storedPat);
        };
    }

    /// <summary>Shows the details page's fields for <paramref name="target"/>.</summary>
    void ApplyTarget(Target target)
    {
        _target = target;
        bool gitHub = Kind == ServerKind.GitHub;
        _targetName.Text = TargetName(target);
        _targetLogo.Content = ProviderLogo.Create(Kind, 18);
        foreach (var (controls, shown) in _targetRows)
            foreach (var control in controls)
                control.IsVisible = shown(target);

        _urlCaption.Text = target == Target.AzureDevOpsServer ? "Collection URL" : "Server URL";
        _url.PlaceholderText = target == Target.AzureDevOpsServer ? "https://tfs.company.com/tfs/DefaultCollection" : "https://github.company.com";
        _urlHint.Text = target == Target.AzureDevOpsServer
            ? "The full URL of your Azure DevOps Server collection."
            : "The full URL of your GitHub Enterprise Server (https://github.company.com) or GHE.com (https://company.ghe.com).";
        _patHint.Text = (gitHub
            ? "Fine-grained token with repository permissions Actions (Read) and Deployments (Read & write, to approve), "
              + "or a classic token with the repo scope. "
            : "PAT scopes: Build (Read & execute) and Project and Team (Read). ") + FormGrid.TokenStorage;
        _filter.PlaceholderText = gitHub ? "Filter loaded repositories and workflows…" : "Filter loaded pipelines…";
        if (target != Target.AzureDevOpsServer || !OperatingSystem.IsWindows()) _usePat.IsChecked = true;
        UpdateTrackedCount();
    }

    Control BuildPicker()
    {
        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (target, detail) in new (Target, string)[]
        {
            (Target.AzureDevOps, "dev.azure.com organization"),
            (Target.AzureDevOpsServer, "On-premises collection URL"),
            (Target.GitHub, "github.com"),
            (Target.GitHubEnterprise, "Your GitHub server's URL"),
        })
        {
            var kind = target is Target.GitHub or Target.GitHubEnterprise ? ServerKind.GitHub : ServerKind.AzureDevOps;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            text.Children.Add(new TextBlock { Text = TargetName(target), FontWeight = FontWeight.SemiBold, FontSize = 14 });
            text.Children.Add(new TextBlock { Text = detail, Foreground = Palette.Muted, FontSize = 12 });
            var logo = ProviderLogo.Create(kind, 28);
            logo.VerticalAlignment = VerticalAlignment.Center;
            var button = new Button
            {
                Content = FormGrid.Flow(logo, text),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 72,
                Padding = new Thickness(14, 0),
                Margin = new Thickness(4),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ((StackPanel)button.Content).Spacing = 12;
            button.Click += (_, _) => PickTarget(target);
            grid.Children.Add(button);
        }
        var cancel = new Button { Content = "Cancel", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) => Close(null);
        DockPanel.SetDock(cancel, Dock.Bottom);
        var title = new TextBlock { Text = "What do you want to watch?", FontSize = 16, FontWeight = FontWeight.SemiBold };
        DockPanel.SetDock(title, Dock.Top);
        var dock = new DockPanel { Margin = new Thickness(16), Background = Palette.Background };
        grid.VerticalAlignment = VerticalAlignment.Top;
        dock.Children.AddRange([title, cancel, grid]);
        return dock;
    }

    void ShowPicker()
    {
        Title = "Add server";
        _picker.IsVisible = true;
        _details.IsVisible = false;
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
            _listed = false;
            RebuildTree();
            SetStatus("");
        }
        ApplyTarget(target);
        Title = $"Add server – {TargetName(target)}";
        _picker.IsVisible = false;
        _details.IsVisible = true;
        (target switch
        {
            Target.AzureDevOps => _org,
            Target.GitHub => _pat,
            _ => _url,
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

    /// <summary>The window's values as a server; null (with a message shown) when something required is missing.</summary>
    ServerConfig? ReadServer()
    {
        string url;
        switch (_target)
        {
            case Target.AzureDevOps:
                var orgText = _org.Text?.Trim() ?? "";
                if (orgText == "") { SetStatus("Enter your organization.", error: true); return null; }
                if (OrganizationOf(orgText) is not { } org) { SetStatus("That isn't a valid organization name.", error: true); return null; }
                url = $"{AzureDevOpsHost}/{org}";
                break;
            case Target.GitHub:
                url = ServerConfig.GitHubUrl;
                break;
            default:
                url = (_url.Text ?? "").Trim().TrimEnd('/');
                if (url == "") { SetStatus("Enter the server URL.", error: true); return null; }
                if (!url.Contains("://")) url = "https://" + url;
                if (!Uri.TryCreate(url, UriKind.Absolute, out _)) { SetStatus("That isn't a valid URL.", error: true); return null; }
                if (Https.Parse(url) == null) { SetStatus("The server must use https.", error: true); return null; }
                break;
        }

        var project = _project.Text?.Trim() ?? "";
        var server = new ServerConfig
        {
            Id = _current.Id,
            Kind = Kind,
            Url = url,
            UseWindowsAuth = _target == Target.AzureDevOpsServer && _useWindows.IsChecked == true,
            Project = _target == Target.AzureDevOps && project != "" ? project : null,
            ProtectedPat = _current.ProtectedPat,
            Pipelines = _tracked.Values.OrderBy(p => p.Project).ThenBy(p => p.Name).ToList(),
        };
        try
        {
            if (_pat.Text?.Trim() is { Length: > 0 } pat)
            {
                if (pat != _storedPatText)
                {
                    var stored = Secrets.Protect(pat, _storedPat);
                    (_storedPat, _storedPatText) = (stored, pat);
                }
                server.ProtectedPat = _storedPat;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't store the token: {ex.Message}", error: true);
            return null;
        }
        if (!server.HasCredentials) { SetStatus("Enter an access token.", error: true); return null; }
        return server;
    }

    async Task ConnectAsync()
    {
        var server = ReadServer();
        if (server == null) return;

        _client?.Dispose();
        _client = server.CreateSource(_rateLimits, _demo);
        _connect.IsEnabled = false;
        SetStatus("Connecting…");
        try
        {
            var projects = await _client.GetProjectsAsync();
            _listed = true;
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

            // Pre-load projects that already have tracked pipelines so their ticks show; a single project opens right away.
            foreach (var node in _tree.Items.OfType<TreeViewItem>().ToList())
                if (node.Tag is ProjectInfo p && (projects.Count == 1
                    || _tracked.Values.Any(t => t.Project.Equals(p.Name, StringComparison.OrdinalIgnoreCase))))
                    node.IsExpanded = true;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, error: true);
        }
        finally
        {
            _connect.IsEnabled = true;
        }
    }

    async Task EnsureLoadedAsync(TreeViewItem projectNode)
    {
        if (projectNode.Tag is not ProjectInfo project || _client == null) return;
        int index = _projects.FindIndex(p => p.Project == project);
        if (index < 0 || _projects[index].Definitions != null || !_loading.Add(projectNode)) return;
        try
        {
            var definitions = await _client.GetDefinitionsAsync(project.Name);
            _projects[index] = (project, definitions);
            FillProjectNode(projectNode, project, definitions);
            UpdateMissingNode();
        }
        catch (Exception ex)
        {
            projectNode.Items.Clear();
            projectNode.Items.Add(new TreeViewItem { Header = $"Failed to load: {ex.Message}", Foreground = new SolidColorBrush(Palette.Red) });
        }
        finally
        {
            _loading.Remove(projectNode);
        }
    }

    void RebuildTree()
    {
        var filter = _filter.Text?.Trim() ?? "";
        _tree.Items.Clear();
        foreach (var (project, definitions) in _projects)
        {
            var box = new CheckBox { Content = project.Name };
            var node = new TreeViewItem { Header = box, Tag = project };
            box.Click += async (_, _) => await OnProjectClickedAsync(node, box.IsChecked == true);
            node.PropertyChanged += async (_, e) =>
            {
                if (e.Property == TreeViewItem.IsExpandedProperty && node.IsExpanded) await EnsureLoadedAsync(node);
            };
            if (definitions == null)
            {
                if (filter != "" && !project.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                node.Items.Add(new TreeViewItem { Header = "Loading…", Foreground = Palette.Muted });
            }
            else
            {
                var matches = definitions.Where(d => filter == ""
                    || d.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || project.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
                if (filter != "" && matches.Count == 0) continue;
                FillProjectNode(node, project, matches);
                if (filter != "") node.IsExpanded = true;
            }
            _tree.Items.Add(node);
        }
        UpdateMissingNode();
    }

    /// <summary>
    /// Tracked pipelines the server no longer lists (a deleted or renamed repo, project or pipeline) get a node of
    /// their own at the top, so they can still be unticked. Rebuilt whenever more of the server has been loaded.
    /// </summary>
    void UpdateMissingNode()
    {
        if (_tree.Items.OfType<TreeViewItem>().FirstOrDefault(n => n.Tag is MissingTag) is { } old) _tree.Items.Remove(old);
        if (!_listed) return;
        var missing = _tracked.Values.Where(IsMissing).OrderBy(p => p.Project).ThenBy(p => p.Name).ToList();
        if (missing.Count == 0) return;
        var node = new TreeViewItem
        {
            Header = new TextBlock { Text = "Not found on the server – untick to stop tracking", Foreground = new SolidColorBrush(Palette.Red) },
            Tag = MissingTag,
            IsExpanded = true,
        };
        foreach (var pipeline in missing)
        {
            var box = new CheckBox { Content = $"{pipeline.Project} / {pipeline.Name}", IsChecked = true };
            box.Click += (_, _) => SetTracked(pipeline, box.IsChecked == true);
            node.Items.Add(new TreeViewItem { Header = box });
        }
        _tree.Items.Insert(0, node);
    }

    /// <summary>Its project isn't listed, or the project's pipelines are loaded and it isn't among them.</summary>
    bool IsMissing(TrackedPipeline pipeline)
    {
        var (project, definitions) = _projects.FirstOrDefault(p => p.Project.Name.Equals(pipeline.Project, StringComparison.OrdinalIgnoreCase));
        return project == null || (definitions != null && definitions.All(d => d.Id != pipeline.DefinitionId));
    }

    void FillProjectNode(TreeViewItem node, ProjectInfo project, List<DefinitionInfo> definitions)
    {
        node.Items.Clear();
        foreach (var d in definitions)
        {
            var pipeline = new TrackedPipeline { Project = project.Name, DefinitionId = d.Id, Name = d.Name };
            var box = new CheckBox { Content = d.DisplayName, IsChecked = _tracked.ContainsKey(pipeline.Key), Tag = pipeline };
            box.Click += (_, _) => SetTracked(pipeline, box.IsChecked == true);
            node.Items.Add(new TreeViewItem { Header = box });
        }
        if (definitions.Count == 0)
            node.Items.Add(new TreeViewItem { Header = Kind == ServerKind.GitHub ? "(no workflows)" : "(no pipelines)", Foreground = Palette.Muted });
    }

    /// <summary>Ticking a project ticks (or unticks) every pipeline in it, loading them first if needed.</summary>
    async Task OnProjectClickedAsync(TreeViewItem node, bool check)
    {
        await EnsureLoadedAsync(node);
        node.IsExpanded = true;
        foreach (var child in node.Items.OfType<TreeViewItem>())
            if (child.Header is CheckBox { Tag: TrackedPipeline pipeline } box)
            {
                box.IsChecked = check;
                SetTracked(pipeline, check);
            }
    }

    void SetTracked(TrackedPipeline pipeline, bool tracked)
    {
        if (tracked) _tracked[pipeline.Key] = pipeline;
        else _tracked.Remove(pipeline.Key);
        Log.Write($"Settings: {(tracked ? "ticked" : "unticked")} {pipeline.Project}/{pipeline.Name}");
        UpdateTrackedCount();
    }

    void UpdateTrackedCount() => _trackedCount.Text = $"{_tracked.Count} {(Kind == ServerKind.GitHub ? "workflow(s)" : "pipeline(s)")} tracked";

    void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.Foreground = error ? new SolidColorBrush(Palette.Red) : Palette.Muted;
    }

    void Accept()
    {
        var server = ReadServer();
        if (server == null) return;
        Log.Write($"Settings: server {server.Kind} {server.DisplayName} with {server.Pipelines.Count} pipelines");
        _accepted = true;
        Close(server);
    }
}
