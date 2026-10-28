#:package System.Drawing.Common@9.0.0
#:property TargetFramework=net10.0-windows

// Renders the app icon to assets/app.ico (multi-size) and assets/app.png (256px preview).
// Run from the repo root: dotnet run --file assets/make-icon.cs
//
// The design is provider-neutral on purpose: an eye (the "watcher") around a three-stage
// pipeline, so it fits Azure DevOps today and GitHub Actions later.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var background = Color.FromArgb(0x16, 0x1B, 0x22);
var border = Color.FromArgb(0x30, 0x36, 0x3D);
var text = Color.FromArgb(0xE6, 0xED, 0xF3);
var muted = Color.FromArgb(0x8B, 0x94, 0x9E);
var green = Color.FromArgb(0x2E, 0xA0, 0x43);
var blue = Color.FromArgb(0x2F, 0x81, 0xF7);

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var dir = Path.GetDirectoryName(Path.GetFullPath(args.Length > 0 ? args[0] : "assets/app.ico"))!;

// 256px is stored as PNG; smaller sizes as classic DIBs, which every icon API can read.
var images = sizes.Select(s => (Size: s, Data: s >= 256 ? Png(Render(s)) : Dib(Render(s)))).ToList();
File.WriteAllBytes(Path.Combine(dir, "app.png"), images[^1].Data);

using (var ico = new BinaryWriter(File.Create(Path.Combine(dir, "app.ico"))))
{
    ico.Write((short)0); ico.Write((short)1); ico.Write((short)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (s, data) in images)
    {
        ico.Write((byte)(s >= 256 ? 0 : s)); ico.Write((byte)(s >= 256 ? 0 : s));
        ico.Write((byte)0); ico.Write((byte)0);
        ico.Write((short)1); ico.Write((short)32);
        ico.Write(data.Length); ico.Write(offset);
        offset += data.Length;
    }
    foreach (var (_, data) in images) ico.Write(data);
}
Console.WriteLine($"Wrote {Path.Combine(dir, "app.ico")}");

Bitmap Render(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.ScaleTransform(size / 256f, size / 256f);
    bool small = size <= 24;   // drop fine detail where it would turn to mush

    // Tile
    using (var tile = RoundRect(new RectangleF(8, 8, 240, 240), 52))
    {
        using var fill = new LinearGradientBrush(new PointF(0, 8), new PointF(0, 248),
            Color.FromArgb(0x22, 0x29, 0x33), background);
        g.FillPath(fill, tile);
        if (!small) { using var edge = new Pen(border, 4); g.DrawPath(edge, tile); }
    }

    // Eye outline
    using (var eye = new GraphicsPath())
    {
        eye.AddBezier(28, 128, 70, 58, 186, 58, 228, 128);
        eye.AddBezier(228, 128, 186, 198, 70, 198, 28, 128);
        eye.CloseFigure();
        using var pen = new Pen(small ? text : muted, small ? 20 : 12) { LineJoin = LineJoin.Round };
        g.DrawPath(pen, eye);
    }

    // Pipeline: done, done, running
    float y = 128, r = small ? 22 : 17;
    float[] xs = [78, 128, 178];
    using (var line = new Pen(text, small ? 12 : 8)) g.DrawLine(line, xs[0], y, xs[2], y);
    using var white = new Pen(Color.White, small ? 9 : 6) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
    for (int i = 0; i < xs.Length; i++)
    {
        var c = new RectangleF(xs[i] - r, y - r, r * 2, r * 2);
        using var ring = new SolidBrush(background);
        g.FillEllipse(ring, RectangleF.Inflate(c, 6, 6));
        using var fill = new SolidBrush(i < 2 ? green : blue);
        g.FillEllipse(fill, c);
        if (small) continue;
        if (i < 2)
            g.DrawLines(white, [new PointF(xs[i] - 8, y + 1), new PointF(xs[i] - 2, y + 7), new PointF(xs[i] + 8, y - 6)]);
        else
            g.DrawArc(white, RectangleF.Inflate(c, -8, -8), -90, 270);
    }
    return bmp;
}

static GraphicsPath RoundRect(RectangleF r, float radius)
{
    var p = new GraphicsPath();
    float d = radius * 2;
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

// BITMAPINFOHEADER + bottom-up BGRA pixels + an all-zero AND mask (alpha does the masking).
static byte[] Dib(Bitmap bmp)
{
    int s = bmp.Width, maskStride = (s + 31) / 32 * 4;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
    w.Write(0); w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    for (int y = s - 1; y >= 0; y--)
        for (int x = 0; x < s; x++)
        {
            var c = bmp.GetPixel(x, y);
            w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
        }
    w.Write(new byte[maskStride * s]);
    bmp.Dispose();
    return ms.ToArray();
}

static byte[] Png(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    bmp.Dispose();
    return ms.ToArray();
}
