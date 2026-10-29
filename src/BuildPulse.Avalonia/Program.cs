using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;

namespace BuildPulse;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Fatal: {e.ExceptionObject}");
        // One self-contained executable per runtime in the GitHub release, e.g. BuildPulse-osx-arm64; Windows keeps
        // the default BuildPulse.exe. Releases also carry DevOpsWatcher* copies, the names older installs update from.
        if (!OperatingSystem.IsWindows()) Updater.AssetName = $"{AppInfo.Id}-{RuntimeInformation.RuntimeIdentifier}";

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
        // Right after a self-update the old process may still be shutting down. The DevOpsWatcher name is held too,
        // so a pre-rename version can't run beside this one and an update from it waits until it has exited.
        using var mutex = Acquire(AppInfo.Id + ".SingleInstance", updated);
        using var legacyMutex = mutex == null ? null : Acquire(AppInfo.LegacyId + ".SingleInstance", updated);
        if (legacyMutex == null) return 0;
        AppInfo.MigrateLegacy();

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

    /// <summary>The named mutex when this process got it, else null; waits for it only when <paramref name="wait"/> is set.</summary>
    static Mutex? Acquire(string name, bool wait)
    {
        var mutex = new Mutex(true, name, out bool isFirstInstance);
        if (isFirstInstance || (wait && WaitForMutex(mutex))) return mutex;
        mutex.Dispose();
        return null;
    }

    static bool WaitForMutex(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { return true; }
    }

    static AppBuilder Configure() => AppBuilder.Configure<App>().WithInterFont();
}
