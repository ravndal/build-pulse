using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace DevOpsWatcher;

/// <summary>Renders the dashboard off-screen with fixed sample data, at 2x, for docs and review.</summary>
static class Screenshots
{
    public static void Capture(string dir)
    {
        Directory.CreateDirectory(dir);
        var model = Sample();
        var gh = model.Servers[0];

        Shoot(dir, "overview.png", model, v => { });
        Shoot(dir, "overview-collapsed.png", model with { CollapsedPanels = [$"{gh.Id}|ravndal/devops-pipline-watcher"] }, v => { });
        Shoot(dir, "history.png", model, v => v.ShowTab(DashboardView.Tab.History));
        Shoot(dir, "history-filtered.png", model, v => v.ShowTab(DashboardView.Tab.History, $"{gh.Id}|ravndal/devops-pipline-watcher"));

        Shoot(dir, "approval.png", new ApprovalWindow(model.Approvals[0]));
        Shoot(dir, "toast.png", new ToastWindow(Palette.Red, "web-build: Failed", "#20261029.7 · main · Ola Nordmann", "https://example.invalid"));

        var demo = DemoSource.CreateSettings();
        Shoot(dir, "settings.png", new SettingsWindow(new AppSettings { Servers = [.. model.Servers, .. demo.Servers] }, demo: true));
        Shoot(dir, "server-add.png", new ServerWindow(null, new RateLimitSettings(), demo: true));
        Shoot(dir, "server-edit.png", new ServerWindow(WithToken(demo.Servers[0]), new RateLimitSettings(), demo: true));
        Shoot(dir, "rate-limits.png", new RateLimitWindow(new RateLimitSettings()));
        Console.WriteLine($"Screenshots written to {Path.GetFullPath(dir)}");
    }

    static void Shoot(string dir, string name, DashboardModel model, Action<DashboardView> setup)
    {
        var view = new DashboardView();
        view.SetModel(model);
        setup(view);
        var window = new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = Brushes.Transparent,
            Content = view,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Save(dir, name, view);
        window.Close();
    }

    /// <summary>A whole window's client area (no OS title bar in headless mode).</summary>
    static void Shoot(string dir, string name, Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Save(dir, name, window);
        window.Close();
    }

    static void Save(string dir, string name, Control control)
    {
        const double scale = 2;
        var size = control.Bounds.Size;
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(control);
        bitmap.Save(Path.Combine(dir, name), new PngBitmapEncoderOptions());
    }

    static DashboardModel Sample()
    {
        var now = DateTime.Now;
        var gh = new ServerConfig
        {
            Id = "gh",
            Kind = ServerKind.GitHub,
            Url = ServerConfig.GitHubUrl,
            Pipelines =
            [
                new() { Project = "ravndal/devops-pipline-watcher", DefinitionId = 1, Name = "CI" },
                new() { Project = "ravndal/devops-pipline-watcher", DefinitionId = 2, Name = "Release" },
                new() { Project = "ravndal/devops-pipline-watcher", DefinitionId = 3, Name = "Copilot code review" },
            ],
        };
        var azdo = new ServerConfig
        {
            Id = "az",
            Kind = ServerKind.AzureDevOps,
            Url = "https://dev.azure.com/aurum",
            Pipelines =
            [
                new() { Project = "aurum", DefinitionId = 10, Name = "api-deploy" },
                new() { Project = "aurum", DefinitionId = 11, Name = "web-build" },
                new() { Project = "aurum", DefinitionId = 12, Name = "nightly-tests" },
                new() { Project = "aurum", DefinitionId = 13, Name = "infra-plan" },
            ],
        };

        var builds = new List<BuildInfo>
        {
            Run("gh", 101, "142", "ravndal/devops-pipline-watcher", 1, "CI", "refs/heads/claude/avalonia-prototype", "ravndal", now.AddMinutes(-3), running: true),
            Run("gh", 100, "141", "ravndal/devops-pipline-watcher", 1, "CI", "refs/pull/6/merge", "ravndal", now.AddMinutes(-52), minutes: 4),
            Run("gh", 99, "37", "ravndal/devops-pipline-watcher", 3, "Copilot code review", "refs/pull/6/merge", "Copilot", now.AddMinutes(-50), minutes: 2),
            Run("gh", 98, "v1.4.0", "ravndal/devops-pipline-watcher", 2, "Release", "refs/tags/v1.4.0", "ravndal", now.AddHours(-26), minutes: 6),
            Run("az", 5012, "20261029.3", "aurum", 10, "api-deploy", "refs/heads/main", "Kristian Ravndal", now.AddMinutes(-18), running: true),
            Run("az", 5011, "20261029.7", "aurum", 11, "web-build", "refs/heads/main", "Ola Nordmann", now.AddMinutes(-35), minutes: 7, result: "failed"),
            Run("az", 5010, "20261029.1", "aurum", 12, "nightly-tests", "refs/heads/main", "Scheduled", now.AddHours(-9), minutes: 23, result: "partiallySucceeded"),
            Run("az", 5009, "20261029.6", "aurum", 11, "web-build", "refs/heads/feature/maps", "Kari Nordmann", now.AddHours(-2), minutes: 6),
            Run("az", 5008, "20261028.2", "aurum", 10, "api-deploy", "refs/heads/main", "Kristian Ravndal", now.AddHours(-20), minutes: 14),
            Run("az", 5007, "20261028.5", "aurum", 11, "web-build", "refs/heads/main", "Ola Nordmann", now.AddHours(-22), minutes: 6),
            Run("az", 5006, "20261028.1", "aurum", 12, "nightly-tests", "refs/heads/main", "Scheduled", now.AddHours(-33), minutes: 22),
            Run("gh", 97, "140", "ravndal/devops-pipline-watcher", 1, "CI", "refs/heads/main", "ravndal", now.AddHours(-27), minutes: 4),
            Run("gh", 96, "139", "ravndal/devops-pipline-watcher", 1, "CI", "refs/pull/5/merge", "ravndal", now.AddHours(-30), minutes: 5, result: "failed"),
        }.OrderByDescending(b => b.QueueTime).ToList();

        var approval = new ApprovalInfo
        {
            ServerId = "az",
            Id = "a1",
            Project = "aurum",
            DefinitionId = 10,
            PipelineName = "api-deploy",
            RunId = 5012,
            RunName = "20261029.3",
            StageName = "Production",
            Approvers = ["Release managers"],
            Instructions = "Check the staging smoke tests before approving.",
            CreatedOn = now.AddMinutes(-6),
            WebUrl = "https://dev.azure.com/aurum",
        };

        var stages = new Dictionary<string, string>
        {
            ["gh|101"] = "test",
            ["az|5012"] = Poller.WaitingForApproval + " · Production",
        };
        var stageStatuses = new Dictionary<string, IReadOnlyList<StageStatus>>
        {
            ["gh|101"] =
            [
                new("build", "completed", "succeeded", false),
                new("test", "inProgress", null, false),
                new("publish", "pending", null, false),
            ],
            ["az|5012"] =
            [
                new("Build", "completed", "succeeded", false),
                new("Test", "completed", "succeeded", false),
                new("Staging", "completed", "succeeded", false),
                new("Production", "inProgress", null, true),
            ],
        };

        return new DashboardModel([gh, azdo], builds, [approval], stages, stageStatuses, now, null, TrayState.Approval, new UpdateStatus(UpdateStage.Available, new Version(1, 5, 0)), []);
    }

    /// <summary>A stand-in stored token so the window connects (the demo source ignores it).</summary>
    static ServerConfig WithToken(ServerConfig server)
    {
        var copy = server.Clone();
        copy.ProtectedPat = "demo";
        return copy;
    }

    static BuildInfo Run(string server, long id, string number, string project, long definition, string name, string branch, string by,
        DateTime queued, bool running = false, int minutes = 0, string result = "succeeded") => new()
    {
        ServerId = server,
        Id = id,
        Number = number,
        Status = running ? "inProgress" : "completed",
        Result = running ? null : result,
        Project = project,
        DefinitionId = definition,
        DefinitionName = name,
        Branch = branch,
        RequestedFor = by,
        QueueTime = queued,
        StartTime = queued.AddSeconds(20),
        FinishTime = running ? null : queued.AddSeconds(20).AddMinutes(minutes),
        WebUrl = "https://example.invalid",
    };
}
