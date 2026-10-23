using System.Text.Json;

namespace LivelyPlaylist;

/// <summary>
/// Remembers the control window's size and position across sessions. Kept in its own
/// file (not playlist.json) so saving it never trips the config file watcher.
/// </summary>
public sealed class UiState
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static UiState Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var state = JsonSerializer.Deserialize<UiState>(File.ReadAllText(path), Options);
                if (state is not null) return state;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read UI state: {ex.Message}");
        }
        return new UiState();
    }

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not save UI state: {ex.Message}");
        }
    }

    /// <summary>Restores size/position when the saved bounds are still on a connected screen.</summary>
    public void ApplyTo(Form form, Size minimum)
    {
        if (Width >= minimum.Width && Height >= minimum.Height)
        {
            form.ClientSize = new Size(Width, Height);
        }

        if (X == int.MinValue || Y == int.MinValue) return;

        var bounds = new Rectangle(X, Y, form.Width, form.Height);
        var visible = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds));
        if (!visible) return;

        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(X, Y);
    }

    public static UiState From(Form form) => new()
    {
        Width = form.ClientSize.Width,
        Height = form.ClientSize.Height,
        X = form.Location.X,
        Y = form.Location.Y,
    };
}
