namespace DevOpsWatcher;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool updated = args.Any(a => a.Equals(Updater.UpdatedArg, StringComparison.OrdinalIgnoreCase));
        using var mutex = new Mutex(true, "DevOpsWatcher.SingleInstance", out bool isFirstInstance);
        // Right after a self-update the old process may still be shutting down.
        if (!isFirstInstance && !(updated && WaitForMutex(mutex))) return;

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Write($"Unhandled: {e.Exception}");
            MessageBox.Show(e.Exception.Message, "DevOps Watcher error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Fatal: {e.ExceptionObject}");
        bool demo = args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        bool openSettings = args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase));
        Application.Run(new TrayApp(demo, openSettings, updated));
    }

    static bool WaitForMutex(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { return true; }
    }
}
