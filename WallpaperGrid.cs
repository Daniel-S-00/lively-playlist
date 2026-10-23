using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace LivelyPlaylist;

/// <summary>
/// The hero card: current wallpaper thumbnail, title, run state, countdown and
/// a slim progress bar. Painted in one control so the art and text can't drift apart.
/// </summary>
public sealed class NowPlayingCard : Control
{
    private Image? _thumbnail;
    private string _title = "—";
    private string _status = "Stopped";
    private string _countdown = "";
    private string _meta = "";
    private Color _statusColor = DarkTheme.DangerHover;
    private double _progress;

    public NowPlayingCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = DarkTheme.Backdrop;
        Height = 112;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Image? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail?.Dispose();
            _thumbnail = value;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title
    {
        get => _title;
        set { if (_title == value) return; _title = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color StatusColor
    {
        get => _statusColor;
        set { _statusColor = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Countdown
    {
        get => _countdown;
        set { if (_countdown == value) return; _countdown = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Meta
    {
        get => _meta;
        set { if (_meta == value) return; _meta = value; Invalidate(); }
    }

    /// <summary>0..1 progress towards the next change.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Progress
    {
        get => _progress;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(clamped - _progress) < 0.0005) return;
            _progress = clamped;
            Invalidate();
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Parent?.BackColor ?? DarkTheme.Backdrop);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var card = new Rectangle(0, 0, Width - 1, Height - 1);
        UiDraw.FillRounded(g, card, 12, DarkTheme.Surface);
        UiDraw.StrokeRounded(g, card, 12, DarkTheme.Border);

        const int pad = 14;
        const int thumbW = 150;
        const int thumbH = 84;

        var thumbRect = new Rectangle(pad, pad, thumbW, thumbH);
        if (_thumbnail is not null)
        {
            using var clip = UiDraw.Rounded(thumbRect, 8);
            var old = g.Clip;
            g.SetClip(clip);
            g.DrawImage(_thumbnail, thumbRect);
            g.Clip = old;
        }
        else
        {
            UiDraw.FillRounded(g, thumbRect, 8, DarkTheme.SurfaceRaised);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, "no preview", DarkTheme.SmallFont, thumbRect, DarkTheme.TextMuted, flags);
        }

        var x = thumbRect.Right + 14;
        var w = Math.Max(10, Width - x - pad);

        TextRenderer.DrawText(g, "NOW PLAYING", DarkTheme.BoldSmallFont,
            new Rectangle(x, pad, w, 14), DarkTheme.TextMuted,
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        TextRenderer.DrawText(g, _title, DarkTheme.H2Font,
            new Rectangle(x, pad + 16, w, 22), DarkTheme.TextPrimary,
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

        // Status row: coloured dot + state, countdown right-aligned.
        var dot = new Rectangle(x, pad + 44, 8, 8);
        using (var brush = new SolidBrush(_statusColor)) g.FillEllipse(brush, dot);

        TextRenderer.DrawText(g, _status, DarkTheme.BoldSmallFont,
            new Rectangle(x + 14, pad + 40, w - 14, 16), _statusColor,
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        if (_countdown.Length > 0)
        {
            TextRenderer.DrawText(g, _countdown, DarkTheme.SmallFont,
                new Rectangle(x, pad + 40, w, 16), DarkTheme.TextMuted,
                TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.SingleLine);
        }

        var bar = new Rectangle(x, pad + 62, w, 6);
        var radius = bar.Height / 2;
        UiDraw.FillRounded(g, bar, radius, DarkTheme.SurfaceActive);
        var fillW = (int)Math.Round(bar.Width * _progress);
        if (fillW > 2)
            UiDraw.FillRounded(g, new Rectangle(bar.X, bar.Y, fillW, bar.Height), radius, DarkTheme.AccentHover);

        if (_meta.Length > 0)
        {
            TextRenderer.DrawText(g, _meta, DarkTheme.SmallFont,
                new Rectangle(x, pad + 74, w, 14), DarkTheme.TextMuted,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _thumbnail?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>One wallpaper tile: cover thumbnail, title and a check badge.</summary>
public sealed class WallpaperCard : Control
{
    public const int CardWidth = 124;
    public const int CardHeight = 100;
    private const int Pad = 8;
    private const int ThumbW = CardWidth - Pad * 2;
    private const int ThumbH = 61;

    private bool _hover;
    private bool _checked;
    private bool _selected;

    public WallpaperCard(int index, WallpaperInfo wallpaper, Image? thumbnail, bool isChecked)
    {
        Index = index;
        Wallpaper = wallpaper;
        Image = thumbnail;
        _checked = isChecked;

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = DarkTheme.Backdrop;
        Size = new Size(CardWidth, CardHeight);
        Cursor = Cursors.Hand;
    }

    public int Index { get; }
    public WallpaperInfo Wallpaper { get; }
    public Image? Image { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Invalidate(); }
    }

    /// <summary>The badge hit-box, used to distinguish "toggle" from "preview".</summary>
    private Rectangle BadgeRect => new(CardWidth - 30, 6, 22, 22);

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        if (BadgeRect.Contains(e.Location))
        {
            if (e.Clicks == 1) ToggleRequested?.Invoke(this);
            return;
        }

        SelectRequested?.Invoke(this);
        if (e.Clicks >= 2) ToggleRequested?.Invoke(this);
    }

    public event Action<WallpaperCard>? ToggleRequested;
    public event Action<WallpaperCard>? SelectRequested;

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Parent?.BackColor ?? DarkTheme.Backdrop);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        var card = new Rectangle(0, 0, Width - 1, Height - 1);

        var fill = _checked
            ? (_hover ? DarkTheme.SurfaceActive : DarkTheme.SurfaceRaised)
            : (_hover ? DarkTheme.SurfaceHover : DarkTheme.Surface);
        UiDraw.FillRounded(g, card, 10, fill);

        var thumb = new Rectangle(Pad, Pad, ThumbW, ThumbH);
        if (Image is not null)
        {
            using var clip = UiDraw.Rounded(thumb, 6);
            var old = g.Clip;
            g.SetClip(clip);
            g.DrawImage(Image, thumb);
            g.Clip = old;
        }
        else
        {
            UiDraw.FillRounded(g, thumb, 6, DarkTheme.SurfaceActive);
            TextRenderer.DrawText(g, "no image", DarkTheme.SmallFont, thumb, DarkTheme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        // Title
        var titleRect = new Rectangle(Pad, Pad + ThumbH + 4, ThumbW, 18);
        var titleColor = _checked ? DarkTheme.TextPrimary : DarkTheme.TextMuted;
        TextRenderer.DrawText(g, Wallpaper.Title, DarkTheme.SmallFont, titleRect, titleColor,
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

        // Border: accent when checked, lighter when merely previewed.
        if (_checked)
            UiDraw.StrokeRounded(g, card, 10, DarkTheme.Accent, 2f);
        else if (_selected)
            UiDraw.StrokeRounded(g, card, 10, DarkTheme.AccentHover, 1f);
        else
            UiDraw.StrokeRounded(g, card, 10, DarkTheme.Border);

        // Check badge
        var badge = BadgeRect;
        if (_checked)
        {
            using (var brush = new SolidBrush(DarkTheme.Accent)) g.FillEllipse(brush, badge);
            using var pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(badge.X + 6f, badge.Y + 11f),
                new PointF(badge.X + 9.5f, badge.Y + 15f),
                new PointF(badge.X + 16f, badge.Y + 7f),
            });
        }
        else
        {
            using var brush = new SolidBrush(Color.FromArgb(170, 20, 20, 20));
            g.FillEllipse(brush, badge);
            using var pen = new Pen(Color.FromArgb(200, 235, 235, 235), 1.4f);
            g.DrawEllipse(pen, badge);
        }
    }

    protected override void Dispose(bool disposing)
    {
        // The grid owns the shared thumbnail cache, so don't dispose Image here.
        base.Dispose(disposing);
    }
}

/// <summary>
/// Scrollable flow of <see cref="WallpaperCard"/>s. Loads and caches downscaled
/// thumbnails, and reports selection/toggle back to the form.
/// </summary>
public sealed class WallpaperGrid : FlowLayoutPanel
{
    private readonly List<WallpaperCard> _cards = new();
    private readonly Dictionary<string, Image?> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Label _empty;

    public WallpaperGrid()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        AutoScroll = true;
        WrapContents = true;
        FlowDirection = FlowDirection.LeftToRight;
        BackColor = DarkTheme.Backdrop;
        Padding = new Padding(4, 4, 4, 4);

        _empty = new Label
        {
            Text = "No wallpapers found in the Lively library.\nAdd some wallpapers in Lively and they'll appear here.",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = DarkTheme.TextMuted,
            Font = DarkTheme.BodyFont,
            Visible = false,
        };
        Controls.Add(_empty);
    }

    public event Action<WallpaperCard>? PreviewRequested;
    public event Action? CheckChanged;

    public int CheckedCount => _cards.Count(c => c.Checked);

    public void SetWallpapers(IReadOnlyList<WallpaperInfo> library, IReadOnlyList<bool> checkedFlags)
    {
        SuspendLayout();

        foreach (var card in _cards)
        {
            card.ToggleRequested -= OnToggle;
            card.SelectRequested -= OnSelect;
        }
        _cards.Clear();
        Controls.Clear();
        Controls.Add(_empty);

        if (library.Count == 0)
        {
            _empty.Visible = true;
            _empty.SetBounds(0, 0, Math.Max(100, ClientSize.Width - 10), Math.Max(80, ClientSize.Height - 10));
        }
        else
        {
            _empty.Visible = false;
            for (var i = 0; i < library.Count; i++)
            {
                var thumb = GetThumb(library[i].ThumbnailPath);
                var isChecked = i < checkedFlags.Count && checkedFlags[i];
                var card = new WallpaperCard(i, library[i], thumb, isChecked);
                card.ToggleRequested += OnToggle;
                card.SelectRequested += OnSelect;
                _cards.Add(card);
                Controls.Add(card);
            }
        }

        ResumeLayout(true);
        Invalidate();
    }

    public bool[] GetChecked() => _cards.Select(c => c.Checked).ToArray();

    public void SetAll(bool value)
    {
        foreach (var c in _cards) c.Checked = value;
        CheckChanged?.Invoke();
    }

    /// <summary>Show the tile for a wallpaper path (used for the live preview).</summary>
    public void Highlight(WallpaperInfo? wallpaper)
    {
        foreach (var card in _cards)
        {
            card.Selected = wallpaper is not null &&
                string.Equals(card.Wallpaper.LivelyInfoPath, wallpaper.LivelyInfoPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void OnToggle(WallpaperCard card)
    {
        card.Checked = !card.Checked;
        CheckChanged?.Invoke();
    }

    private void OnSelect(WallpaperCard card) => PreviewRequested?.Invoke(card);

    private Image? GetThumb(string? path)
    {
        var key = path ?? "";
        if (_thumbs.TryGetValue(key, out var cached)) return cached;

        var img = UiDraw.LoadCover(path, new Size(WallpaperCard.CardWidth - 16, 61));
        _thumbs[key] = img;
        return img;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_empty.Visible)
            _empty.SetBounds(0, 0, Math.Max(100, ClientSize.Width - 10), Math.Max(80, ClientSize.Height - 10));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var img in _thumbs.Values) img?.Dispose();
            _thumbs.Clear();
        }
        base.Dispose(disposing);
    }
}
