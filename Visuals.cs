using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;

namespace DevOpsWatcher;

public enum TrayState { Unconfigured, Idle, Running, Approval, Error }

public enum Glyph { None, Check, Cross, Bang, Spinner, Ring, Dash, Dots }

static class Palette
{
    public static readonly Color Green = Color.FromArgb(0x2E, 0xA0, 0x43);
    public static readonly Color Blue = Color.FromArgb(0x2F, 0x81, 0xF7);
    public static readonly Color Orange = Color.FromArgb(0xFF, 0x8C, 0x00);
    public static readonly Color Red = Color.FromArgb(0xF8, 0x51, 0x49);
    public static readonly Color Amber = Color.FromArgb(0xD2, 0x99, 0x22);
    public static readonly Color Gray = Color.FromArgb(0x8B, 0x94, 0x9E);

    public static readonly Color Background = Color.FromArgb(0x16, 0x1B, 0x22);
    public static readonly Color Surface = Color.FromArgb(0x1F, 0x24, 0x2C);
    public static readonly Color Hover = Color.FromArgb(0x2A, 0x31, 0x3C);
    public static readonly Color Input = Color.FromArgb(0x0D, 0x11, 0x17);
    public static readonly Color Border = Color.FromArgb(0x30, 0x36, 0x3D);
    public static readonly Color Text = Color.FromArgb(0xE6, 0xED, 0xF3);
    public static readonly Color Muted = Color.FromArgb(0x8B, 0x94, 0x9E);
    public static readonly Color ApprovalTint = Color.FromArgb(0x3A, 0x2A, 0x12);
    public static readonly Color ErrorTint = Color.FromArgb(0x3B, 0x1D, 0x1D);

    public static (Color Color, Glyph Glyph) ForState(TrayState state) => state switch
    {
        TrayState.Approval => (Orange, Glyph.Bang),
        TrayState.Running => (Blue, Glyph.Spinner),
        TrayState.Idle => (Green, Glyph.Check),
        TrayState.Error => (Red, Glyph.Cross),
        _ => (Gray, Glyph.Dots),
    };

    public static (Color Color, Glyph Glyph) ForBuild(BuildInfo b, bool awaitingApproval)
    {
        if (awaitingApproval) return (Orange, Glyph.Bang);
        if (b.Status == "inProgress") return (Blue, Glyph.Spinner);
        if (b.IsActive) return (Blue, Glyph.Ring);
        return ForResult(b.Result);
    }

    public static (Color Color, Glyph Glyph) ForResult(string? result) => result switch
    {
        "succeeded" => (Green, Glyph.Check),
        "failed" => (Red, Glyph.Cross),
        "partiallySucceeded" => (Amber, Glyph.Bang),
        _ => (Gray, Glyph.Dash),
    };

    public static (Color Color, Glyph Glyph) ForStage(StageStatus s) => s.State switch
    {
        "inProgress" => s.Waiting ? (Orange, Glyph.Bang) : (Blue, Glyph.Spinner),
        "completed" => s.Result switch
        {
            "succeeded" => (Green, Glyph.Check),
            "succeededWithIssues" => (Amber, Glyph.Bang),
            "failed" or "abandoned" => (Red, Glyph.Cross),
            _ => (Gray, Glyph.Dash),   // skipped, canceled
        },
        _ => (Gray, Glyph.Ring),       // pending
    };

    public static string ResultText(string? result) => result switch
    {
        "succeeded" => "Succeeded",
        "failed" => "Failed",
        "partiallySucceeded" => "Partially succeeded",
        "canceled" => "Canceled",
        _ => "Finished",
    };
}

static class Badge
{
    /// <summary>Draws a filled status circle with a white glyph. Shared by tray icon and dashboard.</summary>
    public static void Draw(Graphics g, RectangleF r, Color color, Glyph glyph, int frame = 0)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = r.Width;

        if (glyph == Glyph.Ring)
        {
            using var ring = new Pen(color, Math.Max(1.5f, w * 0.14f));
            g.DrawEllipse(ring, RectangleF.Inflate(r, -w * 0.1f, -w * 0.1f));
            return;
        }

        using (var fill = new SolidBrush(color)) g.FillEllipse(fill, r);

        using var pen = new Pen(Color.White, Math.Max(1.5f, w * 0.12f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        PointF P(float x, float y) => new(r.X + x * w, r.Y + y * r.Height);
        using var white = new SolidBrush(Color.White);

        switch (glyph)
        {
            case Glyph.Check:
                g.DrawLines(pen, [P(.28f, .52f), P(.44f, .68f), P(.73f, .36f)]);
                break;
            case Glyph.Cross:
                g.DrawLine(pen, P(.34f, .34f), P(.66f, .66f));
                g.DrawLine(pen, P(.66f, .34f), P(.34f, .66f));
                break;
            case Glyph.Bang:
                g.DrawLine(pen, P(.5f, .24f), P(.5f, .55f));
                float dot = Math.Max(1.6f, w * 0.15f);
                var c = P(.5f, .75f);
                g.FillEllipse(white, c.X - dot / 2, c.Y - dot / 2, dot, dot);
                break;
            case Glyph.Dash:
                g.DrawLine(pen, P(.32f, .5f), P(.68f, .5f));
                break;
            case Glyph.Spinner:
                var arc = new RectangleF(r.X + w * .27f, r.Y + w * .27f, w * .46f, w * .46f);
                g.DrawArc(pen, arc, frame * 30 % 360, 270);
                break;
            case Glyph.Dots:
                float d = Math.Max(1.5f, w * 0.13f);
                foreach (var x in new[] { .28f, .5f, .72f })
                {
                    var p = P(x, .5f);
                    g.FillEllipse(white, p.X - d / 2, p.Y - d / 2, d, d);
                }
                break;
        }
    }
}

static class IconFactory
{
    static readonly Dictionary<string, Icon> Cache = [];

    /// <summary>Icons are cached (bounded set of states/frames), so their GDI handles are intentionally never freed.</summary>
    public static Icon Get(TrayState state, int frame, bool failureDot)
    {
        int size = SystemInformation.SmallIconSize.Width;
        var key = $"{state}:{frame}:{failureDot}:{size}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var (color, glyph) = Palette.ForState(state);
            // Approval flashes between orange and red so it stands out in the tray.
            if (state == TrayState.Approval && frame % 2 == 1) color = Palette.Red;
            var r = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);
            Badge.Draw(g, r, color, glyph, frame);

            if (failureDot && state is TrayState.Idle or TrayState.Running)
            {
                float d = size * 0.42f;
                var dot = new RectangleF(size - d - 0.5f, size - d - 0.5f, d, d);
                using var outline = new SolidBrush(Palette.Background);
                g.FillEllipse(outline, RectangleF.Inflate(dot, 1f, 1f));
                using var red = new SolidBrush(Palette.Red);
                g.FillEllipse(red, dot);
            }
        }
        var icon = Icon.FromHandle(bmp.GetHicon());
        Cache[key] = icon;
        return icon;
    }
}

static partial class Fmt
{
    public static string Ago(DateTime? time)
    {
        if (time is not { } t) return "";
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes}m {d.Seconds}s";
        return $"{d.Seconds}s";
    }

    public static string Branch(string refName)
    {
        if (PullRequestRef().Match(refName) is { Success: true } m) return $"PR {m.Groups[1].Value}";
        foreach (var prefix in new[] { "refs/heads/", "refs/tags/" })
            if (refName.StartsWith(prefix, StringComparison.Ordinal)) return refName[prefix.Length..];
        return refName;
    }

    public static string BuildLine(BuildInfo b)
    {
        var number = b.Number != "" && char.IsDigit(b.Number[0]) ? $"#{b.Number}" : b.Number;
        var branch = Branch(b.Branch);
        if (branch.Equals(b.Number, StringComparison.OrdinalIgnoreCase)) branch = "";
        return string.Join(" · ", new[] { number, branch, b.RequestedFor }.Where(s => s != ""));
    }

    [GeneratedRegex(@"^refs/pull/(\d+)/")]
    private static partial Regex PullRequestRef();
}

static class Shell
{
    public static void Open(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser registered */ }
    }
}
