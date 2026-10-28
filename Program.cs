namespace DevOpsWatcher;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "DevOpsWatcher.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance) return;

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
        Application.Run(new TrayApp(demo, openSettings));
    }
}
