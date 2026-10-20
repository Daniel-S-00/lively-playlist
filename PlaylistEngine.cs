namespace LivelyPlaylist;

/// <summary>User-facing playback state of the playlist.</summary>
public enum PlaylistState
{
    Stopped,
    Running,
    Paused,
}

/// <summary>
/// Drives the playlist: on each tick it picks the next wallpaper and applies it
/// to the configured monitors via the Lively gRPC API.
/// </summary>
public sealed class PlaylistEngine : IDisposable
{
    private readonly LivelyClient _client;
    private PlaylistConfig _config;
    private readonly string _configPath;

    private readonly System.Timers.Timer _timer;
    private readonly object _sync = new();

    private List<WallpaperInfo> _pool = new();
    private IReadOnlyList<WallpaperInfo> _library = Array.Empty<WallpaperInfo>();
    private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();
    private int _sequenceIndex;

    // Shuffle bag: a Fisher-Yates-shuffled list of pool indices drawn WITHOUT
    // replacement, so Shuffle never repeats a wallpaper until all have played.
    private List<int> _shuffleBag = new();
    private int _shufflePos;
    private int _lastShuffleTail = -1; // pool index shown last, to avoid a seam repeat on reshuffle

    // Semaphore (not lock) because AdvanceAsync awaits inside the critical section.
    private readonly SemaphoreSlim _advanceLock = new(1, 1);
    // Guards config reload/save so the file watcher and UI don't race.
    private readonly SemaphoreSlim _configLock = new(1, 1);

    // Auto-reload when the user hand-edits playlist.json.
    private readonly FileSystemWatcher? _watcher;
    private CancellationTokenSource? _watchDebounce;

    // Watch the library folders so newly-added wallpapers are picked up automatically.
    private readonly List<FileSystemWatcher> _libraryWatchers = new();
    private CancellationTokenSource? _libraryDebounce;
    private bool _libraryWatcherReady;

    /// <summary>Raised after each wallpaper change so the tray can refresh its tooltip.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised with the full wallpaper object (for thumbnail preview).</summary>
    public event Action<WallpaperInfo>? WallpaperChanged;

    /// <summary>Raised whenever the playback state changes (Stopped/Running/Paused).</summary>
    public event Action<PlaylistState>? StateChanged;

    /// <summary>Raised when the on-disk library changes (new/removed wallpaper folders).</summary>
    public event Action? LibraryChanged;

    private PlaylistState _state = PlaylistState.Stopped;

    /// <summary>Current playback state. The UI must read this, not cache it.</summary>
    public PlaylistState State => _state;

    /// <summary>Kept for callers that only care about "is the timer active".</summary>
    public bool IsRunning => _state == PlaylistState.Running;

    // Countdown bookkeeping: the engine's System.Timers.Timer doesn't expose time
    // remaining, so we track the interval + when it last (re)started ourselves.
    private TimeSpan _timerInterval = TimeSpan.FromSeconds(300);
    private DateTime _timerAnchorUtc = DateTime.UtcNow;

    /// <summary>Time remaining until the next automatic wallpaper change.</summary>
    public TimeSpan TimeUntilNextChange
    {
        get
        {
            if (_state != PlaylistState.Running) return TimeSpan.Zero;
            var remaining = _timerInterval - (DateTime.UtcNow - _timerAnchorUtc);
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
    }

    /// <summary>Restart the countdown clock (called on manual skips and each tick).</summary>
    private void ResetTimerClock() => _timerAnchorUtc = DateTime.UtcNow;

    private void SetState(PlaylistState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>Number of wallpapers currently resolved into the rotation pool.</summary>
    public int PoolCount => _pool.Count;

    /// <summary>Every wallpaper in the Lively library (for the picker list).</summary>
    public IReadOnlyList<WallpaperInfo> Library => _library;

    /// <summary>The wallpaper most recently applied, if any.</summary>
    public WallpaperInfo? CurrentWallpaper { get; private set; }

    /// <summary>The live config object (read its fields to populate the UI).</summary>
    public PlaylistConfig Config => _config;

    public PlaylistEngine(LivelyClient client, PlaylistConfig config, string configPath)
    {
        _client = client;
        _config = config;
        _configPath = configPath;
        _timer = new System.Timers.Timer { AutoReset = true };
        _timer.Elapsed += async (_, _) => await TickSafeAsync().ConfigureAwait(false);

        // Watch the config file so hand-edits apply without restarting.
        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(configPath)!, Path.GetFileName(configPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => OnConfigFileChanged();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Config auto-reload disabled: {ex.Message}");
        }
    }

    private void OnConfigFileChanged()
    {
        // Editors can fire multiple Changed events; debounce into one reload.
        _watchDebounce?.Cancel();
        _watchDebounce = new CancellationTokenSource();
        var token = _watchDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token).ConfigureAwait(false);
                await ReloadAsync().ConfigureAwait(false);
                Logger.Info("Config file change detected — reloaded");
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error("Auto-reload failed", ex);
            }
        }, token);
    }

    /// <summary>Set up filesystem watchers on the library folders (called once, after the first scan).</summary>
    private async Task EnsureLibraryWatchersAsync()
    {
        if (_libraryWatcherReady) return;

        try
        {
            var roots = await _client.GetLibraryRootsAsync().ConfigureAwait(false);
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                var watcher = new FileSystemWatcher(root)
                {
                    NotifyFilter = NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                watcher.Created += (_, _) => OnLibraryChanged();
                watcher.Deleted += (_, _) => OnLibraryChanged();
                watcher.Renamed += (_, _) => OnLibraryChanged();
                _libraryWatchers.Add(watcher);
            }
            _libraryWatcherReady = true;
            Logger.Info($"Library watcher active on {_libraryWatchers.Count} folder(s).");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Library auto-detect disabled: {ex.Message}");
        }
    }

    /// <summary>A wallpaper folder was added/removed — rescan the library (debounced).</summary>
    private void OnLibraryChanged()
    {
        _libraryDebounce?.Cancel();
        _libraryDebounce = new CancellationTokenSource();
        var token = _libraryDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // Imports write files gradually; wait for the dust to settle.
                await Task.Delay(700, token).ConfigureAwait(false);
                await RefreshAsync().ConfigureAwait(false);
                Logger.Info($"Library changed on disk — now {_library.Count} wallpapers, pool {_pool.Count}.");
                LibraryChanged?.Invoke();
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error("Library auto-detect failed", ex);
            }
        }, token);
    }

    /// <summary>Persist a new config from the UI, then refresh the pool and timer.</summary>
    public async Task UpdateConfigAsync(PlaylistConfig newConfig)
    {
        await _configLock.WaitAsync().ConfigureAwait(false);
        try
        {
            newConfig.Save(_configPath);
            _config = newConfig;
            _sequenceIndex = 0;
            await RefreshAsync().ConfigureAwait(false);
            ApplyInterval();
            Logger.Info($"Config updated (playlist='{_config.ActivePlaylist?.Name}', pool={_pool.Count}, interval={_config.ActivePlaylist?.IntervalSeconds}s)");
        }
        finally
        {
            _configLock.Release();
        }
    }

    public async Task StartAsync()
    {
        await RefreshAsync().ConfigureAwait(false);
        ApplyInterval();
        ResetTimer();
        SetState(PlaylistState.Running);
        Logger.Info($"Playlist started (name={_config.ActivePlaylist?.Name}, mode={_config.ActivePlaylist?.Mode}, interval={_config.ActivePlaylist?.IntervalSeconds}s, pool={_pool.Count})");
        // Watch the library folders so newly-added wallpapers are auto-detected.
        await EnsureLibraryWatchersAsync().ConfigureAwait(false);
        // Change one immediately so the user sees it work without waiting a full interval.
        await AdvanceAsync().ConfigureAwait(false);
    }

    public void Pause()
    {
        if (_state != PlaylistState.Running) return;
        lock (_sync) _timer.Stop();
        SetState(PlaylistState.Paused);
        Logger.Info("Playlist paused");
    }

    public async Task ResumeAsync()
    {
        ResetTimer();
        SetState(PlaylistState.Running);
        Logger.Info("Playlist resumed");
        await AdvanceAsync().ConfigureAwait(false);
    }

    /// <summary>Fully stop: timer off and state back to Stopped (used by Exit).</summary>
    public void Stop()
    {
        lock (_sync) _timer.Stop();
        SetState(PlaylistState.Stopped);
        Logger.Info("Playlist stopped");
    }

    /// <summary>(Re)start the timer AND the countdown clock together, so they stay in sync.</summary>
    private void ResetTimer()
    {
        lock (_sync)
        {
            _timer.Stop();
            _timer.Start();
        }
        ResetTimerClock();
    }

    /// <summary>
    /// Restart the countdown after a manual skip, so a pending automatic change can't
    /// immediately override the wallpaper the user just picked.
    /// </summary>
    private void ResetTimerForManualSkip()
    {
        if (_state != PlaylistState.Running) return;
        ResetTimer();
    }

    /// <summary>Re-read config + library and reset the timer. Used after "Edit config".</summary>
    public async Task ReloadAsync()
    {
        _config = PlaylistConfig.Load(_configPath);
        _sequenceIndex = 0;
        await RefreshAsync().ConfigureAwait(false);
        ApplyInterval();
        Logger.Info($"Config reloaded (playlist='{_config.ActivePlaylist?.Name}', interval={_config.ActivePlaylist?.IntervalSeconds}s)");
    }

    /// <summary>
    /// Apply interval/mode changes immediately (and persist) to the ACTIVE playlist,
    /// without a full library re-scan — so editing the spinner or mode dropdown takes
    /// effect on the running timer right away, instead of only after clicking Save.
    /// </summary>
    public void ApplyLiveSettings(int intervalSeconds, PlayMode mode)
    {
        var active = _config.ActivePlaylist;
        if (active is null) return;
        active.IntervalSeconds = intervalSeconds;
        active.Mode = mode;
        ApplyInterval();
        Logger.Info($"Live settings applied (playlist='{active.Name}', interval={intervalSeconds}s, mode={mode})");

        try
        {
            // Persist so it survives restart. The file watcher may also fire a
            // reload for this write — harmless, it reads back the same values.
            _config.Save(_configPath);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not persist live settings: {ex.Message}");
        }
    }

    /// <summary>Skip to the next wallpaper right now (restarts the countdown).</summary>
    public async Task NextAsync()
    {
        await AdvanceAsync(1).ConfigureAwait(false);
        ResetTimerForManualSkip();
    }

    /// <summary>Skip back to the previous wallpaper right now (restarts the countdown).</summary>
    public async Task PreviousAsync()
    {
        await AdvanceAsync(-1).ConfigureAwait(false);
        ResetTimerForManualSkip();
    }

    private void ApplyInterval()
    {
        var seconds = _config.ActivePlaylist?.IntervalSeconds ?? 300;
        var ms = Math.Max(5, seconds) * 1000;
        _timerInterval = TimeSpan.FromMilliseconds(ms);
        lock (_sync) _timer.Interval = ms;
    }

    /// <summary>Resolve the target wallpaper pool and monitors from the current library.</summary>
    private async Task RefreshAsync()
    {
        var library = await _client.GetLibraryAsync().ConfigureAwait(false);
        _library = library;
        _pool = ResolvePool(library, _config.ActivePlaylist?.Wallpapers ?? new List<string>());
        _monitors = await _client.GetMonitorsAsync().ConfigureAwait(false);
        // Pool contents may have changed — the old shuffle bag is now invalid.
        ResetShuffleBag();

        if (_pool.Count == 0)
            Logger.Warn("Playlist pool is empty — no matching wallpapers found in the Lively library.");
    }

    /// <summary>
    /// Map the configured wallpaper entries to actual library items. An empty list means
    /// "everything in the library". Each entry matches by lively_info_path first, then by
    /// folder name (case-insensitive) so the config stays human-readable.
    /// </summary>
    internal static List<WallpaperInfo> ResolvePool(IReadOnlyList<WallpaperInfo> library, List<string> configured)
    {
        if (configured.Count == 0)
            return library.ToList();

        var pool = new List<WallpaperInfo>();
        foreach (var entry in configured)
        {
            var match = library.FirstOrDefault(w =>
                string.Equals(w.LivelyInfoPath, entry, StringComparison.OrdinalIgnoreCase)) ??
                library.FirstOrDefault(w =>
                string.Equals(new DirectoryInfo(w.LivelyInfoPath).Name, entry, StringComparison.OrdinalIgnoreCase));

            if (match is null)
                Logger.Warn($"Configured wallpaper not found in library: '{entry}'");
            else if (!pool.Contains(match))
                pool.Add(match);
        }
        return pool;
    }

    private async Task TickSafeAsync()
    {
        // The timer just fired — restart the countdown clock for the next interval.
        ResetTimerClock();
        try
        {
            await AdvanceAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error("Playlist tick failed", ex);
        }
    }

    /// <summary>Advance every target monitor; <paramref name="step"/> is +1 (next) or -1 (previous).</summary>
    private async Task AdvanceAsync(int step = 1)
    {
        if (_pool.Count == 0)
        {
            StatusChanged?.Invoke("No wallpapers in playlist");
            return;
        }

        var targets = _config.Monitors.Count > 0
            ? _monitors.Where(m => _config.Monitors.Contains(m.Index)).ToList()
            : _monitors.ToList();

        if (targets.Count == 0)
        {
            StatusChanged?.Invoke("No target monitors");
            return;
        }

        await _advanceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Invariant after any advance: the wallpaper on screen is pool[_sequenceIndex - 1].
            // Forward → next displayed is _sequenceIndex.
            // Backward → current displayed is _sequenceIndex - 1, so previous is _sequenceIndex - 2
            // (using -1 would re-pick the wallpaper already showing).
            var baseIndex = step < 0 ? _sequenceIndex - 2 : _sequenceIndex;

            foreach (var monitor in targets)
            {
                WallpaperInfo pick;
                if (_config.PerMonitor)
                {
                    // Independent random pick per monitor.
                    pick = _pool[Random.Shared.Next(_pool.Count)];
                }
                else
                {
                    if (_config.ActivePlaylist?.Mode == PlayMode.Shuffle)
                    {
                        // Draw from the shuffle bag (no repeats until all played).
                        var idx = NextShuffleIndex();
                        _lastShuffleTail = idx; // remember for the refill-seam check
                        pick = _pool[idx];
                    }
                    else
                    {
                        // Sequential: shared sequence, offset per monitor so screens differ.
                        var offset = Mod(baseIndex + monitor.Index, _pool.Count);
                        pick = _pool[offset];
                    }
                }

                try
                {
                    await _client.SetWallpaperAsync(pick.LivelyInfoPath, monitor.DeviceId).ConfigureAwait(false);
                    Logger.Info($"Set '{pick.Title}' on {monitor.DisplayName}");
                    CurrentWallpaper = pick;
                    WallpaperChanged?.Invoke(pick);
                    StatusChanged?.Invoke($"Now: {pick.Title}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to set '{pick.Title}' on {monitor.DisplayName}", ex);
                }
            }

            _sequenceIndex = Mod(_sequenceIndex + step, _pool.Count);
        }
        finally
        {
            _advanceLock.Release();
        }
    }

    /// <summary>Non-negative modulo (C#'s % can return negatives, which would index out of range).</summary>
    private static int Mod(int value, int modulus) =>
        ((value % modulus) + modulus) % modulus;

    /// <summary>
    /// Draw the next pool index from the shuffle bag. The bag is a shuffled list of
    /// every index, consumed without replacement and refilled only once exhausted —
    /// so the same wallpaper cannot repeat until all have played. On refill, if the
    /// pool has 2+ items we rotate so the first pick isn't the one just shown.
    /// </summary>
    private int NextShuffleIndex()
    {
        if (_pool.Count == 1) return 0;

        if (_shufflePos >= _shuffleBag.Count)
            RefillShuffleBag();

        return _shuffleBag[_shufflePos++];
    }

    /// <summary>Rebuild the shuffle bag with a fresh Fisher-Yates shuffle.</summary>
    private void RefillShuffleBag()
    {
        var bag = new List<int>(_pool.Count);
        for (int i = 0; i < _pool.Count; i++)
            bag.Add(i);

        // Fisher-Yates.
        for (int i = bag.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (bag[i], bag[j]) = (bag[j], bag[i]);
        }

        // Avoid an immediate repeat across the refill seam: if the new first item is
        // the same as the last one shown, rotate the bag forward by one.
        if (_lastShuffleTail >= 0 && _pool.Count > 1 && bag[0] == _lastShuffleTail)
        {
            var first = bag[0];
            bag.RemoveAt(0);
            bag.Add(first);
        }

        _shuffleBag = bag;
        _shufflePos = 0;
    }

    /// <summary>Clear the shuffle bag so the next draw reshuffles (call on pool/mode change).</summary>
    private void ResetShuffleBag()
    {
        _shuffleBag = new List<int>();
        _shufflePos = 0;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _watcher?.Dispose();
        _watchDebounce?.Cancel();
        foreach (var watcher in _libraryWatchers)
            watcher.Dispose();
        _libraryWatchers.Clear();
        _libraryDebounce?.Cancel();
    }
}
