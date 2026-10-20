namespace LivelyPlaylist;

/// <summary>
/// Loads the app icon from an embedded resource so it survives single-file publish
/// (a plain file path wouldn't resolve next to a bundled exe). Falls back to the
/// system icon if the resource is missing.
/// </summary>
public static class AppIcon
{
    public static Icon Icon { get; } = Load();

    private static Icon Load()
    {
        try
        {
            var asm = typeof(AppIcon).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("LivelyPlaylist.ico", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using var stream = asm.GetManifestResourceStream(name)!;
                return new Icon(stream);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not load app icon resource: {ex.Message}");
        }
        return SystemIcons.Application;
    }
}
