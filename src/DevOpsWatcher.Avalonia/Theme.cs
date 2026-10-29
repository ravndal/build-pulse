using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace DevOpsWatcher;

/// <summary>Same GitHub-dark palette and status mapping as the WinForms app.</summary>
static class Palette
{
    public static readonly Color Green = Color.FromRgb(0x2E, 0xA0, 0x43);
    public static readonly Color Blue = Color.FromRgb(0x2F, 0x81, 0xF7);
    public static readonly Color Orange = Color.FromRgb(0xFF, 0x8C, 0x00);
    public static readonly Color Red = Color.FromRgb(0xF8, 0x51, 0x49);
    public static readonly Color Amber = Color.FromRgb(0xD2, 0x99, 0x22);
    public static readonly Color Gray = Color.FromRgb(0x8B, 0x94, 0x9E);

    public static readonly IBrush Background = Brush(0x16, 0x1B, 0x22);
    public static readonly IBrush Surface = Brush(0x1F, 0x24, 0x2C);
    public static readonly IBrush Hover = Brush(0x2A, 0x31, 0x3C);
    public static readonly IBrush Border = Brush(0x30, 0x36, 0x3D);
    public static readonly IBrush Text = Brush(0xE6, 0xED, 0xF3);
    public static readonly IBrush Muted = Brush(0x8B, 0x94, 0x9E);
    public static readonly IBrush ApprovalTint = Brush(0x3A, 0x2A, 0x12);
    public static readonly IBrush ErrorTint = Brush(0x3B, 0x1D, 0x1D);
    public static readonly IBrush Link = new SolidColorBrush(Blue);

    static IBrush Brush(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

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

/// <summary>Shared animation clock for spinners and the approval flash; ticks only while something listens.</summary>
static class Ticker
{
    static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    static int _listeners;
    public static int Frame { get; private set; }
    public static event Action? Tick;

    static Ticker() => Timer.Tick += (_, _) => { Frame++; Tick?.Invoke(); };

    public static void Subscribe(Action a) { Tick += a; if (++_listeners == 1) Timer.Start(); }
    public static void Unsubscribe(Action a) { Tick -= a; if (--_listeners == 0) Timer.Stop(); }
}

/// <summary>A filled status circle with a white glyph; animates spinners and, with <see cref="Flash"/>, alternates to red.</summary>
sealed class Badge : Control
{
    readonly Color _color;
    readonly Glyph _glyph;
    readonly bool _flash;

    public Badge(Color color, Glyph glyph, double size, bool flash = false)
    {
        _color = color;
        _glyph = glyph;
        _flash = flash;
        Width = Height = size;
    }

    bool Animated => _glyph == Glyph.Spinner || _flash;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Animated) Ticker.Subscribe(InvalidateVisual);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Animated) Ticker.Unsubscribe(InvalidateVisual);
    }

    public override void Render(DrawingContext ctx)
    {
        var color = _flash && Ticker.Frame / 5 % 2 == 1 ? Palette.Red : _color;
        Draw(ctx, new Rect(Bounds.Size), color, _glyph, Ticker.Frame);
    }

    public static void Draw(DrawingContext ctx, Rect r, Color color, Glyph glyph, int frame)
    {
        double w = r.Width;
        var center = r.Center;
        var brush = new SolidColorBrush(color);

        if (glyph == Glyph.Ring)
        {
            double t = Math.Max(1.5, w * 0.14);
            ctx.DrawEllipse(null, new Pen(brush, t), center, w / 2 - w * 0.1, w / 2 - w * 0.1);
            return;
        }

        ctx.DrawEllipse(brush, null, center, w / 2, w / 2);
        var pen = new Pen(Brushes.White, Math.Max(1.5, w * 0.12), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        Point P(double x, double y) => new(r.X + x * w, r.Y + y * r.Height);

        switch (glyph)
        {
            case Glyph.Check:
                ctx.DrawGeometry(null, pen, Polyline(P(.28, .52), P(.44, .68), P(.73, .36)));
                break;
            case Glyph.Cross:
                ctx.DrawLine(pen, P(.34, .34), P(.66, .66));
                ctx.DrawLine(pen, P(.66, .34), P(.34, .66));
                break;
            case Glyph.Bang:
                ctx.DrawLine(pen, P(.5, .24), P(.5, .55));
                double dot = Math.Max(1.6, w * 0.15) / 2;
                ctx.DrawEllipse(Brushes.White, null, P(.5, .75), dot, dot);
                break;
            case Glyph.Dash:
                ctx.DrawLine(pen, P(.32, .5), P(.68, .5));
                break;
            case Glyph.Spinner:
            {
                double radius = w * .23, start = frame * 30 % 360 * Math.PI / 180, end = start + 1.5 * Math.PI;
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(center + new Vector(Math.Cos(start), Math.Sin(start)) * radius, false);
                    g.ArcTo(center + new Vector(Math.Cos(end), Math.Sin(end)) * radius, new Size(radius, radius), 0, true, SweepDirection.Clockwise);
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, geometry);
                break;
            }
            case Glyph.Dots:
                double d = Math.Max(1.5, w * 0.13) / 2;
                foreach (var x in new[] { .28, .5, .72 }) ctx.DrawEllipse(Brushes.White, null, P(x, .5), d, d);
                break;
        }
    }

    static StreamGeometry Polyline(params Point[] points)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(points[0], false);
        foreach (var p in points.Skip(1)) g.LineTo(p);
        g.EndFigure(false);
        return geometry;
    }

    /// <summary>Tray/menu-bar icon for a state, with a red dot when the last finished run failed.</summary>
    public static WindowIcon TrayIcon(TrayState state, int frame, bool failureDot)
    {
        const int size = 32;
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            var (color, glyph) = Palette.ForState(state);
            if (state == TrayState.Approval && frame % 2 == 1) color = Palette.Red;
            Draw(ctx, new Rect(1, 1, size - 2, size - 2), color, glyph, frame);
            if (failureDot && state is TrayState.Idle or TrayState.Running)
            {
                double d = size * 0.42;
                var c = new Point(size - d / 2 - 1, size - d / 2 - 1);
                ctx.DrawEllipse(Palette.Background, null, c, d / 2 + 1.5, d / 2 + 1.5);
                ctx.DrawEllipse(new SolidColorBrush(Palette.Red), null, c, d / 2, d / 2);
            }
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}

/// <summary>Small Azure DevOps / GitHub marks so runs can be told apart at a glance.</summary>
static class ProviderLogo
{
    // Azure DevOps mark, 18×18 view box.
    static readonly Geometry Azure = Geometry.Parse("F1 M17 4v9.74l-4 3.28-6.2-2.26V17l-3.51-4.59 10.23.8V4.44zm-3.41.49L7.85 1v2.29L2.58 4.84 1 6.87v4.61l2.26 1V6.57z");

    // GitHub mark (Octicons mark-github), 16×16 view box.
    static readonly Geometry GitHub = Geometry.Parse("F1 M8 0c4.42 0 8 3.58 8 8a8.013 8.013 0 0 1-5.45 7.59c-.4.08-.55-.17-.55-.38 0-.27.01-1.13.01-2.2 "
        + "0-.75-.25-1.23-.54-1.48 1.78-.2 3.65-.88 3.65-3.95 0-.88-.31-1.59-.82-2.15.08-.2.36-1.02-.08-2.12 0 0-.67-.22-2.2.82"
        + "-.64-.18-1.32-.27-2-.27-.68 0-1.36.09-2 .27-1.53-1.03-2.2-.82-2.2-.82-.44 1.1-.16 1.92-.08 2.12-.51.56-.82 1.28-.82 2.15 "
        + "0 3.06 1.86 3.75 3.64 3.95-.23.2-.44.55-.51 1.07-.46.21-1.61.55-2.33-.66-.15-.24-.6-.83-1.23-.82-.67.01-.27.38.01.53"
        + ".34.19.73.9.82 1.13.16.45.68 1.31 2.69.94 0 .67.01 1.3.01 1.49 0 .21-.15.45-.55.38A7.995 7.995 0 0 1 0 8c0-4.42 3.58-8 8-8Z");

    static readonly IBrush AzureBlue = new SolidColorBrush(Color.FromRgb(0x1E, 0x8B, 0xE8));

    public static Control Create(ServerKind kind, double size) => new Path
    {
        Data = kind == ServerKind.GitHub ? GitHub : Azure,
        Fill = kind == ServerKind.GitHub ? Palette.Text : AzureBlue,
        Stretch = Stretch.Uniform,
        Width = size,
        Height = size,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };
}
