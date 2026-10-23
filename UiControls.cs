using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace LivelyPlaylist;

/// <summary>Shared painting helpers for the custom dark-theme controls.</summary>
internal static class UiDraw
{
    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) return path;

        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Rectangle r, int radius, Color color)
    {
        using var path = Rounded(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void StrokeRounded(Graphics g, Rectangle r, int radius, Color color, float width = 1f)
    {
        using var path = Rounded(r, radius);
        using var pen = new Pen(color, width) { Alignment = PenAlignment.Inset };
        g.DrawPath(pen, path);
    }

    /// <summary>
    /// Loads an image scaled to cover the target box, centre-cropped. Returns null if the
    /// file is missing or unreadable. The caller owns the returned bitmap.
    /// </summary>
    public static Image? LoadCover(string? path, Size target)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || target.Width <= 0 || target.Height <= 0)
            return null;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var src = Image.FromStream(fs);
            return Cover(src, target);
        }
        catch
        {
            return null;
        }
    }

    public static Bitmap Cover(Image src, Size target)
    {
        var bmp = new Bitmap(target.Width, target.Height);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;

        var scale = Math.Max((float)target.Width / src.Width, (float)target.Height / src.Height);
        var w = (int)Math.Ceiling(src.Width * scale);
        var h = (int)Math.Ceiling(src.Height * scale);

        g.DrawImage(src, new Rectangle((target.Width - w) / 2, (target.Height - h) / 2, w, h));
        return bmp;
    }
}

/// <summary>Rounded, hand-painted button with hover/press/focus states.</summary>
public sealed class RoundedButton : Button
{
    public enum Kind { Secondary, Primary, Danger }

    private const int CornerRadius = 8;

    private bool _hover;
    private bool _pressed;
    private Kind _variant = Kind.Secondary;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Kind Variant
    {
        get => _variant;
        set { if (_variant == value) return; _variant = value; Invalidate(); }
    }

    public RoundedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Color.Transparent;
        ForeColor = DarkTheme.TextPrimary;
        Cursor = Cursors.Hand;
        Font = DarkTheme.BodyFont;
        TextAlign = ContentAlignment.MiddleCenter;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs mevent) { _pressed = true; Invalidate(); base.OnMouseDown(mevent); }
    protected override void OnMouseUp(MouseEventArgs mevent) { _pressed = false; Invalidate(); base.OnMouseUp(mevent); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    private (Color fill, Color border, Color text) Palette()
    {
        if (!Enabled)
            return (DarkTheme.Surface, DarkTheme.Border, DarkTheme.TextMuted);

        return _variant switch
        {
            Kind.Primary => (
                _pressed ? DarkTheme.Accent : _hover ? DarkTheme.AccentHover : DarkTheme.Accent,
                DarkTheme.Accent,
                DarkTheme.OnAccent),
            Kind.Danger => (
                _pressed ? Color.FromArgb(70, 35, 35) : _hover ? Color.FromArgb(60, 30, 30) : DarkTheme.SurfaceRaised,
                DarkTheme.Border,
                Color.FromArgb(255, 150, 150)),
            _ => (
                _pressed ? DarkTheme.SurfaceActive : _hover ? DarkTheme.SurfaceHover : DarkTheme.SurfaceRaised,
                DarkTheme.Border,
                DarkTheme.TextPrimary),
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? DarkTheme.Backdrop);

        var (fill, border, text) = Palette();
        var r = new Rectangle(0, 0, Width - 1, Height - 1);

        UiDraw.FillRounded(g, r, CornerRadius, fill);

        if (Focused && ShowFocusCues)
        {
            // Focus ring instead of the normal border so the button doesn't change size.
            using var pen = new Pen(Color.FromArgb(140, DarkTheme.AccentHover), 1f) { Alignment = PenAlignment.Inset };
            using var path = UiDraw.Rounded(r, CornerRadius);
            g.DrawPath(pen, path);
        }
        else
        {
            UiDraw.StrokeRounded(g, r, CornerRadius, border);
        }

        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                      TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, Text, Font, r, text, flags);
    }
}
