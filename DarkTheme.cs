using System.Drawing;

namespace LivelyPlaylist;

/// <summary>
/// Central dark-theme palette and type scale so every surface (control window,
/// tray menu, custom controls) shares one consistent look.
/// </summary>
public static class DarkTheme
{
    // Surfaces
    public static readonly Color Backdrop      = Color.FromArgb(18, 18, 18);
    public static readonly Color Surface       = Color.FromArgb(32, 32, 32);
    public static readonly Color SurfaceRaised = Color.FromArgb(45, 45, 45);
    public static readonly Color SurfaceHover  = Color.FromArgb(40, 40, 40);
    public static readonly Color SurfaceActive = Color.FromArgb(54, 54, 54);
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

    // Type scale. Created once and reused; they live for the process lifetime.
    private static readonly string Family = PickFamily();
    public static readonly Font TitleFont     = new(Family, 15f, FontStyle.Bold);
    public static readonly Font H2Font        = new(Family, 11f, FontStyle.Bold);
    public static readonly Font BodyFont      = new(Family, 9.5f, FontStyle.Regular);
    public static readonly Font SmallFont     = new(Family, 8.25f, FontStyle.Regular);
    public static readonly Font BoldSmallFont = new(Family, 8.25f, FontStyle.Bold);

    private static string PickFamily()
    {
        try
        {
            var families = new System.Drawing.Text.InstalledFontCollection().Families;
            if (families.Any(f => f.Name == "Segoe UI")) return "Segoe UI";
        }
        catch
        {
            // fall through to the WinForms default family
        }
        return SystemFonts.DefaultFont.FontFamily.Name;
    }
}
