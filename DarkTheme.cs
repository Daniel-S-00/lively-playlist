using System.Drawing;

namespace LivelyPlaylist;

/// <summary>
/// Central dark-theme palette + WinForms styling helpers so every surface
/// (control window, tray menu) shares one consistent look.
/// </summary>
public static class DarkTheme
{
    // Surfaces
    public static readonly Color Backdrop      = Color.FromArgb(18, 18, 18);
    public static readonly Color Surface       = Color.FromArgb(32, 32, 32);
    public static readonly Color SurfaceRaised = Color.FromArgb(45, 45, 45);
    public static readonly Color Border        = Color.FromArgb(60, 60, 60);

    // Text
    public static readonly Color TextPrimary   = Color.FromArgb(235, 235, 235);
    public static readonly Color TextMuted     = Color.FromArgb(150, 150, 150);

    // Accent (kept close to Lively's teal)
    public static readonly Color Accent        = Color.FromArgb(0, 122, 204);
    public static readonly Color AccentHover   = Color.FromArgb(28, 139, 224);
    public static readonly Color OnAccent      = Color.FromArgb(255, 255, 255);

    // States
    public static readonly Color Danger        = Color.FromArgb(200, 60, 60);
    public static readonly Color DangerHover   = Color.FromArgb(220, 80, 80);

    /// <summary>Style a regular clickable button as a dark secondary button.</summary>
    public static void StyleButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.MouseOverBackColor = SurfaceRaised;
        b.FlatAppearance.MouseDownBackColor = Border;
        b.BackColor = SurfaceRaised;
        b.ForeColor = TextPrimary;
        b.Cursor = Cursors.Hand;
        b.UseVisualStyleBackColor = false;
        // No padding — any vertical padding makes small buttons clip their text
        // (it "sinks" below the visible area). Center explicitly instead.
        b.Padding = Padding.Empty;
        b.TextAlign = ContentAlignment.MiddleCenter;
        b.TextImageRelation = TextImageRelation.Overlay;
    }

    /// <summary>Style a button with the accent (primary action) color.</summary>
    public static void StyleAccentButton(Button b)
    {
        StyleButton(b);
        b.BackColor = Accent;
        b.FlatAppearance.BorderColor = Accent;
        b.FlatAppearance.MouseOverBackColor = AccentHover;
        b.FlatAppearance.MouseDownBackColor = Accent;
        b.ForeColor = OnAccent;
    }

    /// <summary>Style a destructive button (Exit).</summary>
    public static void StyleDangerButton(Button b)
    {
        StyleButton(b);
        b.ForeColor = Color.FromArgb(255, 150, 150);
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 35, 35);
    }

    /// <summary>Muted small caption label.</summary>
    public static void StyleCaption(Label l)
    {
        l.ForeColor = TextMuted;
        l.Font = new Font(l.Font.FontFamily, l.Font.Size, FontStyle.Regular);
    }
}
