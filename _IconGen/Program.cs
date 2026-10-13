// Generates Assets\app.ico (+ README PNGs) for Visor: a "V" inside a reticle.
// Usage: dotnet run --project _IconGen -- <repoRoot>
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

var frames = sizes.Select(s => (Size: s, Bmp: Render(s))).ToList();
WriteIco(Path.Combine(root, "Assets", "app.ico"), frames);

var shots = Path.Combine(root, "docs", "screenshots");
Directory.CreateDirectory(shots);
using (var big = Render(256)) big.Save(Path.Combine(shots, "app-icon.png"), ImageFormat.Png);
using (var mid = Render(128)) mid.Save(Path.Combine(shots, "app-icon-128.png"), ImageFormat.Png);
using (var sheet = Preview(frames)) sheet.Save(Path.Combine(root, "docs", "branding", "icon-preview.png"), ImageFormat.Png);
foreach (var f in frames) f.Bmp.Dispose();
Console.WriteLine("Icon written: " + Path.Combine(root, "Assets", "app.ico"));

static Bitmap Render(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.CompositingQuality = CompositingQuality.HighQuality;
    g.Clear(Color.Transparent);

    float k = size / 256f;
    bool tiny = size <= 24;   // tray / small taskbar: fewer, bolder shapes
    bool small = size <= 48;

    var bg = Color.FromArgb(0x0B, 0x0F, 0x17);
    var ring = Color.FromArgb(small ? 0x3A : 0x2A, small ? 0x4A : 0x36, small ? 0x60 : 0x46);
    var cyan = Color.FromArgb(0x22, 0xD3, 0xEE);
    var violet = Color.FromArgb(0x8B, 0x5C, 0xF6);

    using (var path = RoundedRect(0, 0, size, size, (tiny ? 48 : 56) * k))
    using (var b = new SolidBrush(bg))
        g.FillPath(b, path);

    // Reticle ring
    float ringW = Math.Max(tiny ? 1.25f : 1f, 10 * k);
    float r = (tiny ? 90 : 84) * k;
    using (var p = new Pen(ring, ringW))
        g.DrawEllipse(p, 128 * k - r, 128 * k - r, 2 * r, 2 * r);

    // Tick marks (cyan top/left, violet bottom/right); skipped at tray size where they turn to mush
    if (!tiny)
    {
        float tw = Math.Max(1.5f, 10 * k), tl = 34 * k;
        FillPill(g, cyan, 128 * k - tw / 2, 26 * k, tw, tl);
        FillPill(g, violet, 128 * k - tw / 2, 196 * k, tw, tl);
        FillPill(g, cyan, 26 * k, 128 * k - tw / 2, tl, tw);
        FillPill(g, violet, 196 * k, 128 * k - tw / 2, tl, tw);
    }

    // The V
    float vw = (tiny ? 34 : small ? 30 : 26) * k;
    var vPts = tiny
        ? new[] { new PointF(80 * k, 84 * k), new PointF(128 * k, 178 * k), new PointF(176 * k, 84 * k) }
        : new[] { new PointF(84 * k, 90 * k), new PointF(128 * k, 172 * k), new PointF(172 * k, 90 * k) };
    using (var grad = new LinearGradientBrush(new PointF(60 * k, 60 * k), new PointF(196 * k, 196 * k), cyan, violet))
    using (var pen = new Pen(grad, vw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        g.DrawLines(pen, vPts);

    return bmp;
}

static void FillPill(Graphics g, Color c, float x, float y, float w, float h)
{
    using var path = RoundedRect(x, y, w, h, Math.Min(w, h) / 2);
    using var b = new SolidBrush(c);
    g.FillPath(b, path);
}

static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
{
    var p = new GraphicsPath();
    float d = Math.Min(2 * r, Math.Min(w, h));
    if (d <= 0) { p.AddRectangle(new RectangleF(x, y, w, h)); return p; }
    p.AddArc(x, y, d, d, 180, 90);
    p.AddArc(x + w - d, y, d, d, 270, 90);
    p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
    p.AddArc(x, y + h - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

// ICO: PNG frames for >= 128 px, 32-bpp DIB frames for smaller sizes (best compatibility with
// System.Drawing.Icon / NotifyIcon on every shell).
static void WriteIco(string file, List<(int Size, Bitmap Bmp)> frames)
{
    var blobs = frames.Select(f => f.Size >= 128 ? Png(f.Bmp) : Dib(f.Bmp)).ToList();
    using var fs = File.Create(file);
    using var w = new BinaryWriter(fs);
    w.Write((short)0); w.Write((short)1); w.Write((short)frames.Count);
    int offset = 6 + 16 * frames.Count;
    for (int i = 0; i < frames.Count; i++)
    {
        int s = frames[i].Size;
        w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((short)1); w.Write((short)32);
        w.Write(blobs[i].Length); w.Write(offset);
        offset += blobs[i].Length;
    }
    foreach (var b in blobs) w.Write(b);
}

static byte[] Png(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static byte[] Dib(Bitmap bmp)
{
    int s = bmp.Width;
    int maskStride = ((s + 31) / 32) * 4;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
    w.Write(0); w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    var data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
        var row = new byte[s * 4];
        for (int y = s - 1; y >= 0; y--)
        {
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
            w.Write(row);
        }
    }
    finally { bmp.UnlockBits(data); }
    w.Write(new byte[maskStride * s]);
    return ms.ToArray();
}

static Bitmap Preview(List<(int Size, Bitmap Bmp)> frames)
{
    int pad = 16, x = pad;
    int width = frames.Sum(f => f.Size + pad) + pad;
    var sheet = new Bitmap(width, 256 + 2 * pad, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(sheet);
    g.Clear(Color.FromArgb(0x30, 0x34, 0x3C));
    foreach (var f in frames)
    {
        g.DrawImageUnscaled(f.Bmp, x, pad + 256 - f.Size);
        x += f.Size + pad;
    }
    return sheet;
}
