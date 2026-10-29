using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;

namespace DevOpsWatcher;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Fatal: {e.ExceptionObject}");
        // One self-contained executable per runtime in the GitHub release, e.g. DevOpsWatcher-osx-arm64.
        Updater.AssetName = $"DevOpsWatcher-{RuntimeInformation.RuntimeIdentifier}{(OperatingSystem.IsWindows() ? ".exe" : "")}";

        int shots = Array.FindIndex(args, a => a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase));
        if (shots >= 0)
        {
            // Renders the windows off-screen with sample data; no tray icon, no network.
            var dir = shots + 1 < args.Length ? args[shots + 1] : "screenshots";
            Configure()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            Screenshots.Capture(dir);
            return 0;
        }

        bool updated = Has(args, Updater.UpdatedArg);
        using var mutex = new Mutex(true, "DevOpsWatcher.SingleInstance", out bool isFirstInstance);
        // Right after a self-update the old process may still be shutting down.
        if (!isFirstInstance && !(updated && WaitForMutex(mutex))) return 0;

        App.Demo = Has(args, "--demo");
        App.OpenSettings = Has(args, "--settings");
        App.Updated = updated;
        return Configure()
            .UsePlatformDetect()
            // Menu-bar app on macOS: no Dock icon, no app menu.
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    static bool Has(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    static bool WaitForMutex(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { return true; }
    }

    static AppBuilder Configure() => AppBuilder.Configure<App>().WithInterFont();
}
