using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grpc.Core;
using GrpcDotNetNamedPipes;
using Lively.Grpc.Common.Proto.Desktop;
using Lively.Grpc.Common.Proto.Display;
using Lively.Grpc.Common.Proto.Settings;

namespace LivelyPlaylist;

/// <summary>
/// Thin wrapper over Lively's gRPC-over-named-pipe API.
/// Mirrors how Lively's own client (WinDesktopCoreClient) connects.
/// </summary>
public sealed class LivelyClient : IDisposable
{
    // Matches Lively.Common.Constants.SingleInstance.GrpcPipeServerName
    private const string AppName = "LIVELY:DESKTOPWALLPAPERSYSTEM";
    private static string PipeName => "Grpc_" + AppName + Environment.UserName;

    // On-disk library subfolders, from Lively.Common.Constants.CommonPartialPaths.
    private const string InstallDir = "wallpapers";
    private const string InstallTempDir = "SaveData/wptmp";

    private readonly DesktopService.DesktopServiceClient _desktop;
    private readonly DisplayService.DisplayServiceClient _display;
    private readonly SettingsService.SettingsServiceClient _settings;
    private readonly NamedPipeChannel _channel;
    private bool _disposed;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WaitNamedPipe(string lpNamedPipeName, uint nTimeOut);

    public LivelyClient()
    {
        _channel = new NamedPipeChannel(".", PipeName);
        _desktop = new DesktopService.DesktopServiceClient(_channel);
        _display = new DisplayService.DisplayServiceClient(_channel);
        _settings = new SettingsService.SettingsServiceClient(_channel);
    }

    /// <summary>True if a Lively v2 core process is listening on the pipe.</summary>
    public static bool IsCoreRunning() => WaitNamedPipe(@"\\.\pipe\" + PipeName, 0);

    /// <summary>
    /// Poll until Lively's core is reachable (or the token is cancelled). Returns true
    /// if the core came up. Used so the app can idle in the tray and auto-start the
    /// playlist the moment the user launches Lively.
    /// </summary>
    public static async Task<bool> WaitForCoreAsync(TimeSpan pollInterval, CancellationToken token = default)
    {
        while (!token.IsCancellationRequested)
        {
            if (IsCoreRunning())
                return true;
            try
            {
                await Task.Delay(pollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// The two on-disk folders that make up the library, or null if the wallpaper
    /// dir can't be resolved. Used to set up filesystem watchers.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetLibraryRootsAsync()
    {
        var wallpaperDir = await GetWallpaperDirAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(wallpaperDir))
            return Array.Empty<string>();

        return new[]
        {
            Path.Combine(wallpaperDir, InstallDir),
            Path.Combine(wallpaperDir, "SaveData", "wptmp"),
        };
    }

    /// <summary>
    /// The FULL installed library, read from disk. Unlike the GetWallpapers RPC
    /// (which only returns currently-active wallpapers), this scans the wallpaper
    /// folders and parses each LivelyInfo.json for the real title + thumbnail.
    /// </summary>
    public async Task<IReadOnlyList<WallpaperInfo>> GetLibraryAsync()
    {
        var wallpaperDir = await GetWallpaperDirAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(wallpaperDir))
        {
            Logger.Warn("WallpaperDir could not be resolved (settings unreachable and no default).");
            return Array.Empty<WallpaperInfo>();
        }
        Logger.Info($"Scanning library under: {wallpaperDir}");

        var roots = new[]
        {
            Path.Combine(wallpaperDir, InstallDir),
            Path.Combine(wallpaperDir, "SaveData", "wptmp"),
        };

        var result = new List<WallpaperInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                Logger.Info($"Scan root missing (skipped): {root}");
                continue;
            }
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                if (!seen.Add(folder)) continue;
                var info = TryReadLivelyInfo(folder);
                if (info is not null)
                    result.Add(info);
            }
        }

        Logger.Info($"Library scan found {result.Count} wallpapers.");
        return result
            .OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Parse a wallpaper folder's LivelyInfo.json into a WallpaperInfo.</summary>
    private static WallpaperInfo? TryReadLivelyInfo(string folder)
    {
        var infoPath = Path.Combine(folder, "LivelyInfo.json");
        if (!File.Exists(infoPath)) return null;

        try
        {
            var meta = JsonSerializer.Deserialize<LivelyInfoFile>(
                File.ReadAllText(infoPath), JsonOpts);
            if (meta is null) return null;

            // Title falls back to the folder name if missing.
            var title = string.IsNullOrWhiteSpace(meta.Title)
                ? new DirectoryInfo(folder).Name
                : meta.Title.Trim();

            // Thumbnail can be absolute, or a filename relative to the folder.
            var thumb = ResolveThumbnail(folder, meta.Thumbnail);

            return new WallpaperInfo(folder, title, CategoryName(meta.Type), thumb);
        }
        catch (Exception ex)
        {
            // Skip this wallpaper, but make the failure visible in the log.
            Logger.Warn($"Skipping '{folder}' — bad LivelyInfo.json: {ex.Message}");
            return null;
        }
    }

    /// <summary>Map Lively's integer WallpaperCategory to a readable label.</summary>
    private static string CategoryName(int type) => type switch
    {
        0 => "app", 1 => "web", 2 => "webaudio", 3 => "url", 4 => "bizhawk",
        5 => "unity", 6 => "godot", 7 => "video", 8 => "gif", 9 => "unityaudio",
        10 => "videostream", 11 => "picture",
        _ => "unknown",
    };

    /// <summary>Resolve the thumbnail path (handles both absolute and relative metadata).</summary>
    private static string? ResolveThumbnail(string folder, string? thumbnail)
    {
        if (string.IsNullOrWhiteSpace(thumbnail)) return null;
        try
        {
            var full = Path.IsPathRooted(thumbnail)
                ? thumbnail
                : Path.Combine(folder, thumbnail);
            // For absolute-path wallpapers the stored thumb is just a filename in the folder.
            if (!File.Exists(full))
            {
                var byName = Path.Combine(folder, Path.GetFileName(thumbnail));
                full = byName;
            }
            return File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The user's wallpaper directory, from Lively's settings.</summary>
    public async Task<string?> GetWallpaperDirAsync()
    {
        try
        {
            var s = await _settings.GetSettingsAsync(new Google.Protobuf.WellKnownTypes.Empty())
                .ConfigureAwait(false);

            // Lively's own default when wallpaper_dir is unset (see SettingsModel).
            if (string.IsNullOrWhiteSpace(s.WallpaperDir))
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Lively Wallpaper", "Library");

            return s.WallpaperDir;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read WallpaperDir from settings: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Currently-ACTIVE wallpapers (one per screen). Useful for showing what is
    /// running right now, but NOT the full library — see GetLibraryAsync.
    /// </summary>
    public async Task<IReadOnlyList<WallpaperInfo>> GetActiveWallpapersAsync()
    {
        var result = new List<WallpaperInfo>();
        using var call = _desktop.GetWallpapers(new Google.Protobuf.WellKnownTypes.Empty());
        while (await call.ResponseStream.MoveNext().ConfigureAwait(false))
        {
            var r = call.ResponseStream.Current;
            var title = new DirectoryInfo(r.LivelyInfoPath).Name;
            result.Add(new WallpaperInfo(r.LivelyInfoPath, title, r.Category.ToString(), r.ThumbnailPath));
        }
        return result;
    }

    /// <summary>Connected displays (monitors).</summary>
    public async Task<IReadOnlyList<MonitorInfo>> GetMonitorsAsync()
    {
        var resp = await _display.GetScreensAsync(new Google.Protobuf.WellKnownTypes.Empty()).ConfigureAwait(false);
        return resp.Screens.Select(s => new MonitorInfo(s.DeviceId, s.DisplayName, s.Index, s.IsPrimary)).ToList();
    }

    public async Task SetWallpaperAsync(string livelyInfoPath, string monitorDeviceId)
    {
        await _desktop.SetWallpaperAsync(new SetWallpaperRequest
        {
            LivelyInfoPath = livelyInfoPath,
            MonitorId = monitorDeviceId,
        }).ConfigureAwait(false);
    }

    public async Task<CoreStats> GetCoreStatsAsync()
    {
        var r = await _desktop.GetCoreStatsAsync(new Google.Protobuf.WellKnownTypes.Empty()).ConfigureAwait(false);
        return new CoreStats(r.AssemblyVersion, r.IsCoreInitialized);
    }

    // LivelyInfo.json uses PascalCase keys; be lenient about case.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Subset of LivelyInfo.json we care about.</summary>
    private sealed class LivelyInfoFile
    {
        public string? Title { get; set; }
        public string? Thumbnail { get; set; }
        // Type is an integer enum in LivelyInfo.json (not a string) — see WallpaperCategory.
        public int Type { get; set; } = -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // NamedPipeChannel is not IDisposable in GrpcDotNetNamedPipes 3.1.0; the
        // underlying connection closes when the process exits.
    }
}

/// <summary>LivelyInfoPath is the wallpaper folder path — the stable identity used by SetWallpaper.</summary>
public sealed record WallpaperInfo(string LivelyInfoPath, string Title, string Category, string? ThumbnailPath);

public sealed record MonitorInfo(string DeviceId, string DisplayName, int Index, bool IsPrimary);

public sealed record CoreStats(string AssemblyVersion, bool IsCoreInitialized);
