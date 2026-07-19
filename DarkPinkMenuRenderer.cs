using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DisplayProfileManager;

/// <summary>Clean dark-pink tray menu — even rows, no crooked padding.</summary>
internal sealed class DarkPinkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color Bg = Color.FromArgb(0x1A, 0x14, 0x18);
    private static readonly Color Border = Color.FromArgb(0x3D, 0x2A, 0x34);
    private static readonly Color Hover = Color.FromArgb(0x2E, 0x1F, 0x27);
    private static readonly Color Accent = Color.FromArgb(0xC4, 0x5C, 0x84);
    private static readonly Color Text = Color.FromArgb(0xF3, 0xE6, 0xEC);
    private static readonly Color TextHot = Color.FromArgb(0xD4, 0x74, 0x9A);

    public DarkPinkMenuRenderer() : base(new DarkPinkColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Bg);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Border);
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // no image margin gutter
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(6, 1, e.Item.Width - 12, e.Item.Height - 2);

        if (!e.Item.Selected && !e.Item.Pressed) return;

        using var path = RoundedRect(bounds, 4);
        using var brush = new SolidBrush(Hover);
        g.FillPath(brush, path);
        using var pen = new Pen(Accent);
        g.DrawPath(pen, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Selected ? TextHot : Text;
        e.TextFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix;
        // Keep left padding consistent
        e.TextRectangle = new Rectangle(14, 0, e.Item.Width - 20, e.Item.Height);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.ContentRectangle.Top + e.Item.ContentRectangle.Height / 2;
        using var pen = new Pen(Border);
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class DarkPinkColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Accent;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
