using System.Text.Json;
using System.Text.Json.Serialization;

namespace LivelyPlaylist;

public enum PlayMode
{
    Sequential,
    Shuffle,
}

/// <summary>A single named playlist: its wallpaper set + timing.</summary>
public sealed class PlaylistDefinition
{
    /// <summary>Stable id (guid string). Used to track the active playlist across saves.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Playlist";

    /// <summary>Seconds between wallpaper changes.</summary>
    public int IntervalSeconds { get; set; } = 300;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PlayMode Mode { get; set; } = PlayMode.Sequential;

    /// <summary>
    /// Wallpaper folder paths to include, in order. Matched against the library by
    /// lively_info_path, falling back to a folder-name match. Empty = entire library.
    /// </summary>
    public List<string> Wallpapers { get; set; } = new();

    public PlaylistDefinition Clone() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = Name,
        IntervalSeconds = IntervalSeconds,
        Mode = Mode,
        Wallpapers = new List<string>(Wallpapers),
    };
}

/// <summary>
/// Root config: monitor targeting is SHARED across playlists; the wallpaper set,
/// interval and mode belong to each individual playlist.
/// </summary>
public sealed class PlaylistConfig
{
    /// <summary>
    /// Monitor indices to apply playlists to. Empty means all monitors.
    /// Shared by design (see AGENTS.md scoping notes).
    /// </summary>
    public List<int> Monitors { get; set; } = new();

    /// <summary>Apply a different random wallpaper per monitor. Shared across playlists.</summary>
    public bool PerMonitor { get; set; } = false;

    /// <summary>Launch at Windows sign-in (writes/removes a HKCU Run key). Starts minimized to tray.</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>Auto-start the playlist when Lively's core becomes reachable (waits for the pipe).</summary>
    public bool AutoStartWithLively { get; set; } = false;

    /// <summary>Id of the playlist currently selected/playing.</summary>
    public string ActivePlaylistId { get; set; } = "";

    public List<PlaylistDefinition> Playlists { get; set; } = new();

    [JsonIgnore]
    public PlaylistDefinition ActivePlaylist =>
        Playlists.FirstOrDefault(p => p.Id == ActivePlaylistId) ?? Playlists.FirstOrDefault()!;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "playlist.json");

    public static PlaylistConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return NewDefault();

            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);
            // v2 shape has a "Playlists" array; anything else is the legacy flat shape.
            var isLegacy = !doc.RootElement.TryGetProperty("Playlists", out _);
            doc.Dispose();

            if (isLegacy)
            {
                var migrated = MigrateLegacy(json);
                // Persist the upgraded shape once, so we don't re-migrate every launch.
                try
                {
                    migrated.Save(path);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not persist migrated config (will re-migrate next launch): {ex.Message}");
                }
                return migrated;
            }

            var cfg = JsonSerializer.Deserialize<PlaylistConfig>(json, JsonOpts) ?? NewDefault();
            EnsureValid(cfg);
            return cfg;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to read config '{path}', using defaults: {ex.Message}");
            return NewDefault();
        }
    }

    /// <summary>Convert a pre-multi-playlist config file into the new shape.</summary>
    private static PlaylistConfig MigrateLegacy(string json)
    {
        var legacy = JsonSerializer.Deserialize<LegacyPlaylistConfig>(json, JsonOpts)
                     ?? new LegacyPlaylistConfig();

        var cfg = NewDefault();
        cfg.Monitors = legacy.Monitors;
        cfg.PerMonitor = legacy.PerMonitor;

        var first = cfg.Playlists[0];
        first.IntervalSeconds = legacy.IntervalSeconds;
        first.Mode = legacy.Mode;
        first.Wallpapers = legacy.Wallpapers;
        cfg.ActivePlaylistId = first.Id;

        Logger.Info($"Migrated legacy config to multi-playlist format (playlist '{first.Name}').");
        return cfg;
    }

    /// <summary>Guarantee the config is usable (at least one playlist, valid active id).</summary>
    public static void EnsureValid(PlaylistConfig cfg)
    {
        cfg.Monitors ??= new();
        cfg.Playlists ??= new();
        if (cfg.Playlists.Count == 0)
            cfg.Playlists.Add(new PlaylistDefinition { Name = "Default" });
        foreach (var p in cfg.Playlists)
        {
            p.Wallpapers ??= new();
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Playlist";
        }
        if (cfg.Playlists.All(p => p.Id != cfg.ActivePlaylistId))
            cfg.ActivePlaylistId = cfg.Playlists[0].Id;
    }

    private static PlaylistConfig NewDefault()
    {
        var cfg = new PlaylistConfig();
        cfg.Playlists.Add(new PlaylistDefinition { Name = "Default" });
        cfg.ActivePlaylistId = cfg.Playlists[0].Id;
        return cfg;
    }

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>Write a starter config if none exists, so the user has a template to edit.</summary>
    public static PlaylistConfig EnsureExists(string path)
    {
        if (!File.Exists(path))
            NewDefault().Save(path);
        return Load(path);
    }
}

/// <summary>Old flat schema, used only to migrate pre-multi-playlist files.</summary>
internal sealed class LegacyPlaylistConfig
{
    public int IntervalSeconds { get; set; } = 300;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PlayMode Mode { get; set; } = PlayMode.Sequential;

    public List<string> Wallpapers { get; set; } = new();
    public List<int> Monitors { get; set; } = new();
    public bool PerMonitor { get; set; } = false;
}
