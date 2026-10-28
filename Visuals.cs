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

    /// <summary>The app icon (assets/app.ico) for windows; the tray uses the status icons below.</summary>
    public static readonly Icon App = new(typeof(IconFactory).Assembly.GetManifestResourceStream("app.ico")!);

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

/// <summary>Small Azure DevOps / GitHub marks so runs can be told apart at a glance.</summary>
static class ProviderLogo
{
    static readonly Color AzureBlue = Color.FromArgb(0x1E, 0x8B, 0xE8);

    // Azure DevOps mark, 18×18 view box.
    const string AzureDevOps = "M17 4v9.74l-4 3.28-6.2-2.26V17l-3.51-4.59 10.23.8V4.44zm-3.41.49L7.85 1v2.29L2.58 4.84 1 6.87v4.61l2.26 1V6.57z";

    // GitHub mark (Octicons mark-github), 16×16 view box.
    const string GitHub = "M8 0c4.42 0 8 3.58 8 8a8.013 8.013 0 0 1-5.45 7.59c-.4.08-.55-.17-.55-.38 0-.27.01-1.13.01-2.2 "
        + "0-.75-.25-1.23-.54-1.48 1.78-.2 3.65-.88 3.65-3.95 0-.88-.31-1.59-.82-2.15.08-.2.36-1.02-.08-2.12 0 0-.67-.22-2.2.82"
        + "-.64-.18-1.32-.27-2-.27-.68 0-1.36.09-2 .27-1.53-1.03-2.2-.82-2.2-.82-.44 1.1-.16 1.92-.08 2.12-.51.56-.82 1.28-.82 2.15 "
        + "0 3.06 1.86 3.75 3.64 3.95-.23.2-.44.55-.51 1.07-.46.21-1.61.55-2.33-.66-.15-.24-.6-.83-1.23-.82-.67.01-.27.38.01.53"
        + ".34.19.73.9.82 1.13.16.45.68 1.31 2.69.94 0 .67.01 1.3.01 1.49 0 .21-.15.45-.55.38A7.995 7.995 0 0 1 0 8c0-4.42 3.58-8 8-8Z";

    static readonly GraphicsPath AzurePath = SvgPath.Parse(AzureDevOps);
    static readonly GraphicsPath GitHubPath = SvgPath.Parse(GitHub);

    /// <param name="onLight">Draws the GitHub mark dark, for light backgrounds.</param>
    public static void Draw(Graphics g, RectangleF r, ServerKind kind, bool onLight = false)
    {
        var (path, viewBox, color) = kind == ServerKind.GitHub ? (GitHubPath, 16f, onLight ? Color.FromArgb(0x1F, 0x23, 0x28) : Palette.Text) : (AzurePath, 18f, AzureBlue);
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(r.X, r.Y);
        g.ScaleTransform(r.Width / viewBox, r.Height / viewBox);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
        g.Restore(state);
    }
}

/// <summary>Just enough SVG path syntax (M L H V C S A Z, absolute and relative) for the logos above.</summary>
static class SvgPath
{
    public static GraphicsPath Parse(string d)
    {
        var path = new GraphicsPath(FillMode.Winding);
        var tokens = Regex.Matches(d, @"[A-Za-z]|-?(?:\d+\.?\d*|\.\d+)(?:e-?\d+)?").Select(m => m.Value).ToList();
        int i = 0;
        char cmd = 'M';
        PointF cur = default, start = default, lastCtrl = default;
        char prev = ' ';
        float Num() => float.Parse(tokens[i++], System.Globalization.CultureInfo.InvariantCulture);
        bool HasNum() => i < tokens.Count && !char.IsLetter(tokens[i][0]);

        while (i < tokens.Count)
        {
            if (char.IsLetter(tokens[i][0])) cmd = tokens[i++][0];
            bool rel = char.IsLower(cmd);
            PointF Pt(float x, float y) => rel ? new(cur.X + x, cur.Y + y) : new(x, y);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    path.StartFigure();
                    cur = start = Pt(Num(), Num());
                    cmd = rel ? 'l' : 'L'; // further pairs are implicit line-tos
                    break;
                case 'L':
                    { var p = Pt(Num(), Num()); path.AddLine(cur, p); cur = p; }
                    break;
                case 'H':
                    { var x = Num(); var p = new PointF(rel ? cur.X + x : x, cur.Y); path.AddLine(cur, p); cur = p; }
                    break;
                case 'V':
                    { var y = Num(); var p = new PointF(cur.X, rel ? cur.Y + y : y); path.AddLine(cur, p); cur = p; }
                    break;
                case 'C':
                    {
                        var c1 = Pt(Num(), Num()); var c2 = Pt(Num(), Num()); var p = Pt(Num(), Num());
                        path.AddBezier(cur, c1, c2, p); lastCtrl = c2; cur = p;
                    }
                    break;
                case 'S':
                    {
                        var c1 = char.ToUpperInvariant(prev) is 'C' or 'S' ? new PointF(2 * cur.X - lastCtrl.X, 2 * cur.Y - lastCtrl.Y) : cur;
                        var c2 = Pt(Num(), Num()); var p = Pt(Num(), Num());
                        path.AddBezier(cur, c1, c2, p); lastCtrl = c2; cur = p;
                    }
                    break;
                case 'A':
                    {
                        float rx = Num(), ry = Num(), rot = Num(), large = Num(), sweep = Num();
                        var p = Pt(Num(), Num());
                        AddArc(path, cur, p, rx, ry, rot, large != 0, sweep != 0);
                        cur = p;
                    }
                    break;
                case 'Z':
                    path.CloseFigure();
                    cur = start;
                    break;
                default:
                    throw new FormatException($"Unsupported path command '{cmd}'.");
            }
            prev = cmd;
            // Commands other than M/Z repeat while numbers follow.
            if (char.ToUpperInvariant(cmd) == 'Z' && HasNum()) cmd = 'L';
        }
        return path;
    }

    /// <summary>SVG elliptical arc (endpoint form) as cubic Béziers.</summary>
    static void AddArc(GraphicsPath path, PointF p0, PointF p1, float rx, float ry, float rotDeg, bool large, bool sweep)
    {
        if (rx == 0 || ry == 0) { path.AddLine(p0, p1); return; }
        double phi = rotDeg * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (p0.X - p1.X) / 2.0, dy = (p0.Y - p1.Y) / 2.0;
        double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
        double rxs = Math.Abs(rx), rys = Math.Abs(ry);
        double lambda = x1 * x1 / (rxs * rxs) + y1 * y1 / (rys * rys);
        if (lambda > 1) { rxs *= Math.Sqrt(lambda); rys *= Math.Sqrt(lambda); }
        double num = rxs * rxs * rys * rys - rxs * rxs * y1 * y1 - rys * rys * x1 * x1;
        double den = rxs * rxs * y1 * y1 + rys * rys * x1 * x1;
        double coef = (large == sweep ? -1 : 1) * Math.Sqrt(Math.Max(0, num / den));
        double cx1 = coef * rxs * y1 / rys, cy1 = -coef * rys * x1 / rxs;
        double cx = cos * cx1 - sin * cy1 + (p0.X + p1.X) / 2.0, cy = sin * cx1 + cos * cy1 + (p0.Y + p1.Y) / 2.0;
        double Angle(double ux, double uy, double vx, double vy) =>
            Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        double theta = Angle(1, 0, (x1 - cx1) / rxs, (y1 - cy1) / rys);
        double delta = Angle((x1 - cx1) / rxs, (y1 - cy1) / rys, (-x1 - cx1) / rxs, (-y1 - cy1) / rys);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        if (sweep && delta < 0) delta += 2 * Math.PI;

        int segments = (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2));
        double step = delta / segments, k = 4.0 / 3 * Math.Tan(step / 4);
        PointF At(double a, double ex, double ey) => new(
            (float)(cx + cos * (rxs * Math.Cos(a) + ex) - sin * (rys * Math.Sin(a) + ey)),
            (float)(cy + sin * (rxs * Math.Cos(a) + ex) + cos * (rys * Math.Sin(a) + ey)));
        var from = p0;
        for (int s = 0; s < segments; s++)
        {
            double a0 = theta + s * step, a1 = a0 + step;
            var c1 = At(a0, -k * rxs * Math.Sin(a0), k * rys * Math.Cos(a0));
            var c2 = At(a1, k * rxs * Math.Sin(a1), -k * rys * Math.Cos(a1));
            var to = s == segments - 1 ? p1 : At(a1, 0, 0);
            path.AddBezier(from, c1, c2, to);
            from = to;
        }
    }
}
