namespace BuildPulse;

static class Program
{
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"TUI fatal: {e.ExceptionObject}");

        if (Has(args, "--help") || Has(args, "-h"))
        {
            Console.WriteLine($"""
                {AppInfo.Name} for the terminal: your pipelines and approvals in a small pane.

                Usage: buildpulse-tui [--demo]

                  --demo      fake pipelines, no server (an approval shows up after ~45s)
                  --version   print the version

                Servers and pipelines come from the tray app's settings:
                  {AppSettings.FilePath}
                Press ? in the app for keys.
                """);
            return 0;
        }
        if (Has(args, "--version"))
        {
            Console.WriteLine(Updater.CurrentVersion.ToString(3));
            return 0;
        }
        int snapshot = Array.FindIndex(args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
        if (snapshot >= 0) return Snapshot(args.Skip(snapshot + 1).ToArray());

        if (Console.IsOutputRedirected || Console.IsInputRedirected)
        {
            Console.Error.WriteLine("buildpulse-tui needs an interactive terminal.");
            return 1;
        }

        bool demo = Has(args, "--demo");
        if (!demo) AppInfo.MigrateLegacy();

        var loop = new MainLoop();
        var dashboard = new Dashboard(loop, demo, new Terminal());
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; loop.Post(dashboard.Quit); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.Write("\e[0m\e[?7h\e[?25h\e[?1049l");
        loop.Post(() => dashboard.Start());
        loop.Run();
        return 0;
    }

    /// <summary>
    /// <c>--snapshot 60x16 [seconds] [keys]</c>: runs the demo off-screen for a few seconds, presses the keys and
    /// prints one frame. For checking layouts at pane sizes, and as CI's smoke test of the trimmed executable.
    /// </summary>
    static int Snapshot(string[] args)
    {
        var size = (args.FirstOrDefault() ?? "60x16").Split('x');
        int width = int.Parse(size[0]), height = int.Parse(size[1]);
        double seconds = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 3;
        var keys = args.Length > 2 ? args[2].Replace(@"\t", "\t").Replace(@"\n", "\n") : "";

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var loop = new MainLoop();
        var dashboard = new Dashboard(loop, demo: true, new Terminal((width, height)));
        loop.Post(async () =>
        {
            dashboard.Start(readKeys: false);
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            foreach (var c in keys) dashboard.OnKey(Key(c));
            Console.Out.Write(dashboard.Capture());
            dashboard.Quit();
        });
        loop.Run();
        return 0;
    }

    static ConsoleKeyInfo Key(char c)
    {
        var key = c switch
        {
            '\t' => ConsoleKey.Tab,
            '\n' => ConsoleKey.Enter,
            ' ' => ConsoleKey.Spacebar,
            '?' => ConsoleKey.Oem2,
            >= '0' and <= '9' => ConsoleKey.D0 + (c - '0'),
            _ => (ConsoleKey)char.ToUpperInvariant(c),
        };
        return new ConsoleKeyInfo(c, key, char.IsUpper(c), false, false);
    }

    static bool Has(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
}
