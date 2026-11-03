using System.Runtime.InteropServices;
using System.Text;

namespace BuildPulse;

public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>The tray app's GitHub-dark palette, for the terminal.</summary>
static class Colors
{
    public static readonly Rgb Green = new(0x2E, 0xA0, 0x43);
    public static readonly Rgb Blue = new(0x2F, 0x81, 0xF7);
    public static readonly Rgb Orange = new(0xFF, 0x8C, 0x00);
    public static readonly Rgb Red = new(0xF8, 0x51, 0x49);
    public static readonly Rgb Amber = new(0xD2, 0x99, 0x22);
    public static readonly Rgb Gray = new(0x8B, 0x94, 0x9E);
    public static readonly Rgb Purple = new(0xA3, 0x71, 0xF7);

    public static readonly Rgb Text = new(0xE6, 0xED, 0xF3);
    public static readonly Rgb Muted = new(0x8B, 0x94, 0x9E);
    public static readonly Rgb Faint = new(0x48, 0x4F, 0x58);
    /// <summary>Dark text on the colored status pill.</summary>
    public static readonly Rgb Ink = new(0x0D, 0x11, 0x17);
    public static readonly Rgb Selection = new(0x2A, 0x31, 0x3C);
    public static readonly Rgb ApprovalTint = new(0x3A, 0x2A, 0x12);

    public static Rgb Of(Tone tone) => tone switch
    {
        Tone.Green => Green,
        Tone.Blue => Blue,
        Tone.Orange => Orange,
        Tone.Red => Red,
        Tone.Amber => Amber,
        _ => Gray,
    };
}

readonly record struct Span(string Text, Rgb? Fg, Rgb? Bg, bool Bold);

/// <summary>One screen line of styled spans. The terminal clips it to the width and fills the rest with <see cref="Background"/>.</summary>
sealed class Line
{
    readonly List<Span> _spans = [];

    public Rgb? Background { get; init; }
    public int Length { get; private set; }
    public IReadOnlyList<Span> Spans => _spans;

    public Line Add(string text, Rgb? fg = null, bool bold = false, Rgb? bg = null)
    {
        if (text.Length == 0) return this;
        _spans.Add(new Span(text, fg, bg, bold));
        Length += text.Length;
        return this;
    }

    public Line Add(Line other)
    {
        foreach (var s in other._spans) Add(s.Text, s.Fg, s.Bold, s.Bg);
        return this;
    }

    /// <summary>This line cut to <paramref name="width"/> columns, ending in "…" when something was cut.</summary>
    public Line Clip(int width)
    {
        if (Length <= width) return this;
        var line = new Line { Background = Background };
        if (width <= 0) return line;
        int left = width - 1;
        foreach (var s in _spans)
        {
            if (left <= 0) break;
            var text = s.Text.Length > left ? s.Text[..left].TrimEnd() : s.Text;
            line.Add(text, s.Fg, s.Bold, s.Bg);
            left -= s.Text.Length;
        }
        var last = line._spans.LastOrDefault();
        line.Add("…", last.Fg ?? Colors.Muted, false, last.Bg);
        return line;
    }

    /// <summary><paramref name="left"/> and <paramref name="right"/> on one line of <paramref name="width"/>; the left side gives way.</summary>
    public static Line Spread(Line left, Line right, int width, Rgb? background = null)
    {
        var line = new Line { Background = background };
        if (right.Length >= width) return line.Add(right.Clip(width));
        var l = left.Clip(Math.Max(0, width - right.Length - 1));
        line.Add(l);
        line.Add(new string(' ', width - l.Length - right.Length), bg: background);
        return line.Add(right);
    }
}

/// <summary>
/// Full-screen drawing with ANSI escapes on the alternate screen: true color where the terminal says it has it,
/// the 256-color cube otherwise (tmux without RGB), and no color at all with NO_COLOR.
/// </summary>
sealed class Terminal((int Width, int Height)? snapshot = null)
{
    readonly bool _color = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
    readonly bool _trueColor = OperatingSystem.IsWindows()
        || Environment.GetEnvironmentVariable("COLORTERM") is "truecolor" or "24bit";
    readonly Stream _out = Console.OpenStandardOutput();
    string _lastFrame = "";
    string _lastTitle = "";

    public int Width => snapshot?.Width ?? Math.Max(1, Console.WindowWidth);
    public int Height => snapshot?.Height ?? Math.Max(1, Console.WindowHeight);

    public void Enter()
    {
        if (snapshot != null) return;
        if (OperatingSystem.IsWindows()) EnableVirtualTerminal();
        Console.OutputEncoding = Encoding.UTF8;
        Console.TreatControlCAsInput = true;
        // Alternate screen, hidden cursor, no line wrap.
        Write("\e[?1049h\e[?25l\e[?7l\e[2J");
    }

    public void Leave()
    {
        if (snapshot != null) return;
        Write("\e[0m\e[?7h\e[?25h\e[?1049l");
        try { Console.TreatControlCAsInput = false; } catch { /* no console */ }
    }

    /// <summary>The terminal (and tmux pane) title.</summary>
    public void Title(string title)
    {
        if (title == _lastTitle || snapshot != null) return;
        _lastTitle = title;
        Write($"\e]2;{title}\a");
    }

    /// <summary>Rings the bell: tmux marks the window, most terminals flash or beep.</summary>
    public void Bell() { if (snapshot == null) Write("\a"); }

    /// <summary>Forces the next <see cref="Draw"/> to repaint everything, e.g. after a resize.</summary>
    public void Invalidate()
    {
        if (snapshot != null) return;
        _lastFrame = "";
        Write("\e[2J");
    }

    public void Draw(IReadOnlyList<Line> lines, int width, int height)
    {
        if (snapshot != null) { DrawSnapshot(lines, width, height); return; }
        var sb = new StringBuilder();
        for (int row = 0; row < height; row++)
        {
            sb.Append("\e[").Append(row + 1).Append(";1H");
            if (row < lines.Count) Render(sb, lines[row], width);
            sb.Append("\e[0m\e[K");
        }
        var frame = sb.ToString();
        if (frame == _lastFrame) return;
        _lastFrame = frame;
        // Synchronized output, so terminals that support it never show a half-drawn frame.
        Write("\e[?2026h" + frame + "\e[?2026l");
    }

    /// <summary>The frame as plain lines, for <c>--snapshot</c>.</summary>
    void DrawSnapshot(IReadOnlyList<Line> lines, int width, int height)
    {
        var sb = new StringBuilder();
        for (int row = 0; row < height; row++)
        {
            if (row < lines.Count) Render(sb, lines[row], width);
            sb.Append("\e[0m\n");
        }
        _lastFrame = sb.ToString();
    }

    /// <summary>The last frame drawn in snapshot mode.</summary>
    public string Snapshot => _lastFrame;

    void Render(StringBuilder sb, Line line, int width)
    {
        var clipped = line.Clip(width);
        foreach (var s in clipped.Spans)
        {
            sb.Append("\e[0m");
            if (s.Bold) sb.Append("\e[1m");
            Color(sb, s.Fg, 38);
            Color(sb, s.Bg ?? line.Background, 48);
            sb.Append(s.Text);
        }
        if (line.Background is { } bg && clipped.Length < width)
        {
            sb.Append("\e[0m");
            Color(sb, bg, 48);
            sb.Append(' ', width - clipped.Length);
        }
    }

    void Color(StringBuilder sb, Rgb? rgb, int layer)
    {
        if (!_color || rgb is not { } c) return;
        if (_trueColor) sb.Append($"\e[{layer};2;{c.R};{c.G};{c.B}m");
        else sb.Append($"\e[{layer};5;{Cube(c)}m");
    }

    /// <summary>The nearest color in the 6×6×6 cube of the 256-color palette.</summary>
    static int Cube(Rgb c)
    {
        static int Level(byte v) => v < 48 ? 0 : v < 115 ? 1 : (v - 35) / 40;
        return 16 + 36 * Level(c.R) + 6 * Level(c.G) + Level(c.B);
    }

    void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _out.Write(bytes);
        _out.Flush();
    }

    // ---- Windows: conhost only interprets escapes once asked to ----

    static void EnableVirtualTerminal()
    {
        var handle = GetStdHandle(-11);
        if (GetConsoleMode(handle, out uint mode)) SetConsoleMode(handle, mode | 0x0004 | 0x0008);
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll")]
    static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
