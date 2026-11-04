using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BuildPulse;

static class Program
{
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"TUI fatal: {e.ExceptionObject}");
        // Release assets: buildpulse-tui.exe on Windows, buildpulse-tui-<runtime> elsewhere.
        Updater.AssetName = OperatingSystem.IsWindows() ? "buildpulse-tui.exe" : $"buildpulse-tui-{RuntimeInformation.RuntimeIdentifier}";
        Updater.DownloadName = "buildpulse-tui";

        if (Has(args, "--help") || Has(args, "-h"))
        {
            Console.WriteLine($"""
                {AppInfo.Name} for the terminal: your pipelines and approvals in a small pane.

                Usage: buildpulse-tui [--demo]

                  --demo      fake pipelines, no server (an approval shows up after ~45s)
                  --version   print the version

                Press s in the app to add servers and pick pipelines; ? lists the keys.
                Settings are shared with the tray app:
                  {AppSettings.FilePath}
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
        var dashboard = new Dashboard(loop, demo, new Terminal(), updated: Has(args, Updater.UpdatedArg));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; loop.Post(dashboard.Quit); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.Write("\e[0m\e[?7h\e[?25h\e[?1049l");
        loop.Post(() => dashboard.Start());
        loop.Run();
        return dashboard.Relaunch ? Relaunch(dashboard, args) : 0;
    }

    /// <summary>
    /// Starts the updated executable in this terminal and waits for it. .NET can't exec, and the child of a process
    /// that already exited would lose the terminal to the shell, so this one stays until the new version quits.
    /// </summary>
    static int Relaunch(Dashboard dashboard, string[] args)
    {
        dashboard.StopReadingKeys();
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var arg in args.Where(a => !a.Equals(Updater.UpdatedArg, StringComparison.OrdinalIgnoreCase)))
            start.ArgumentList.Add(arg);
        start.ArgumentList.Add(Updater.UpdatedArg);
        try
        {
            using var child = Process.Start(start)!;
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Write($"TUI: couldn't start the update: {ex.Message}");
            Console.Error.WriteLine($"Updated, but couldn't start the new version ({ex.Message}). Run buildpulse-tui again.");
            return 1;
        }
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
        var keys = Keys(args.Length > 2 ? args[2] : "");

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var loop = new MainLoop();
        var dashboard = new Dashboard(loop, demo: true, new Terminal((width, height)));
        loop.Post(async () =>
        {
            dashboard.Start(readKeys: false);
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            foreach (var key in keys)
            {
                if (key == null) await Task.Delay(500);
                else dashboard.OnKey(key.Value);
            }
            Console.Out.Write(dashboard.Capture());
            dashboard.Quit();
        });
        loop.Run();
        return 0;
    }

    static readonly Dictionary<string, ConsoleKey> Named = new()
    {
        ["up"] = ConsoleKey.UpArrow, ["down"] = ConsoleKey.DownArrow, ["left"] = ConsoleKey.LeftArrow, ["right"] = ConsoleKey.RightArrow,
        ["esc"] = ConsoleKey.Escape, ["enter"] = ConsoleKey.Enter, ["tab"] = ConsoleKey.Tab, ["bs"] = ConsoleKey.Backspace,
        ["space"] = ConsoleKey.Spacebar,
    };

    /// <summary>
    /// Snapshot keys: characters as typed, \t and \n, named keys in angle brackets (&lt;down&gt;, &lt;esc&gt;, ...),
    /// and &lt;wait&gt; (null) for half a second, e.g. to let a connect finish.
    /// </summary>
    static List<ConsoleKeyInfo?> Keys(string text)
    {
        var keys = new List<ConsoleKeyInfo?>();
        text = text.Replace(@"\t", "\t").Replace(@"\n", "\n");
        for (int i = 0; i < text.Length; i++)
        {
            int end = text[i] == '<' ? text.IndexOf('>', i) : -1;
            var name = end > i ? text[(i + 1)..end] : null;
            if (name == "wait") { keys.Add(null); i = end; }
            else if (name != null && Named.TryGetValue(name, out var named))
            {
                char c = named switch { ConsoleKey.Enter => '\r', ConsoleKey.Tab => '\t', ConsoleKey.Spacebar => ' ', ConsoleKey.Escape => '\e', ConsoleKey.Backspace => '\b', _ => '\0' };
                keys.Add(new ConsoleKeyInfo(c, named, false, false, false));
                i = end;
            }
            else keys.Add(Key(text[i]));
        }
        return keys;
    }

    static ConsoleKeyInfo Key(char c)
    {
        var key = c switch
        {
            '\t' => ConsoleKey.Tab,
            '\n' => ConsoleKey.Enter,
            ' ' => ConsoleKey.Spacebar,
            '?' or '/' => ConsoleKey.Oem2,
            '-' => ConsoleKey.OemMinus,
            '.' => ConsoleKey.OemPeriod,
            ',' => ConsoleKey.OemComma,
            >= '0' and <= '9' => ConsoleKey.D0 + (c - '0'),
            _ => (ConsoleKey)char.ToUpperInvariant(c),
        };
        return new ConsoleKeyInfo(c, key, char.IsUpper(c), false, false);
    }

    static bool Has(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
}
