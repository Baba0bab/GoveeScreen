using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace GoveeScreenV2.UI;

/// <summary>
/// Rendu sombre des menus WinForms (menu de la zone de notification), assorti à la fenêtre WPF.
/// </summary>
internal sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
{
    public static readonly Color Background = Color.FromArgb(0x1E, 0x1E, 0x27);
    public static readonly Color Border = Color.FromArgb(0x2A, 0x2A, 0x35);
    private static readonly Color Hover = Color.FromArgb(0x2E, 0x2E, 0x3A);
    private static readonly Color Text = Color.FromArgb(0xF1, 0xF1, 0xF6);
    private static readonly Color TextDim = Color.FromArgb(0x8A, 0x8A, 0x9C);
    private static readonly Color Accent = Color.FromArgb(0xB9, 0xB0, 0xFF);

    public DarkMenuRenderer() : base(new DarkColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        // Sous Windows 11 la bordure (arrondie) est dessinée par DWM
        using var pen = new Pen(Border);
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
    {
        // Pas de colonne d'icônes en dégradé
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
            return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var path = RoundedRect(rect, 4);
        using var brush = new SolidBrush(Hover);
        g.FillPath(brush, path);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(Border);
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : TextDim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = TextDim;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        // Coche dessinée à la main : l'image système est noire, invisible sur fond sombre
        var r = e.ImageRectangle;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = r.Width / 16f;
        using var pen = new Pen(Accent, 1.8f * s)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        g.DrawLines(pen, new[]
        {
            new PointF(r.X + 3.5f * s, r.Y + 8.5f * s),
            new PointF(r.X + 6.5f * s, r.Y + 11.5f * s),
            new PointF(r.X + 12.5f * s, r.Y + 4.5f * s)
        });
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class DarkColorTable : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Background;
        public override Color CheckBackground => Background;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
