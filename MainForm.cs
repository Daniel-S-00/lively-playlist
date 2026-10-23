using System.Drawing;

namespace LivelyPlaylist;

/// <summary>
/// Dark-themed control window shown on tray double-click: a now-playing card,
/// playlist management, live settings, a thumbnail picker grid and transport controls.
/// </summary>
public sealed class MainForm : Form
{
    private const int Side = 18;

    private readonly LivelyClient _client;
    private readonly PlaylistEngine _engine;
    private readonly string _configPath;
    private readonly string _uiStatePath;

    // Hero card: thumbnail + run state + countdown + progress.
    private readonly NowPlayingCard _nowPlaying = new();

    // 1s UI timer that refreshes the countdown/progress from the engine.
    private readonly System.Windows.Forms.Timer _countdownTimer = new() { Interval = 1000 };

    private readonly NumericUpDown _intervalInput = new();
    private readonly ComboBox _modeInput = new();

    // Startup behavior.
    private readonly CheckBox _startWithWindowsCheck = new();
    private readonly CheckBox _autoStartCheck = new();

    // Playlist management.
    private readonly ComboBox _playlistSelector = new();
    private readonly RoundedButton _newPlaylistButton = new();
    private readonly RoundedButton _renamePlaylistButton = new();
    private readonly RoundedButton _deletePlaylistButton = new();

    private readonly RoundedButton _toggleButton = new();
    private readonly RoundedButton _prevButton = new();
    private readonly RoundedButton _nextButton = new();
    private readonly RoundedButton _selectAllButton = new();
    private readonly RoundedButton _selectNoneButton = new();
    private readonly RoundedButton _saveButton = new();
    private readonly RoundedButton _exitButton = new();

    private readonly WallpaperGrid _grid = new();

    private int _monitorCount;

    // Blocks live-settings pushes while we programmatically seed the controls on load.
    private bool _seedingControls;

    public MainForm(LivelyClient client, PlaylistEngine engine, string configPath)
    {
        _client = client;
        _engine = engine;
        _configPath = configPath;
        _uiStatePath = Path.Combine(Path.GetDirectoryName(configPath) ?? ".", "ui-state.json");

        Text = "Lively Playlist";
        Icon = AppIcon.Icon;
        // Author this layout at 96 DPI and let WinForms scale everything (fonts +
        // control bounds) up on higher-DPI displays — otherwise fixed-pixel button
        // heights clip their text at >100% scaling.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        // ClientSize (not Size) so the title bar/borders can't push controls off-screen.
        ClientSize = new Size(460, 720);
        MinimumSize = new Size(480, 680);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        BackColor = DarkTheme.Backdrop;

        // Restore geometry before laying out so anchor distances are computed correctly.
        UiState.Load(_uiStatePath).ApplyTo(this, new Size(460, 680));

        BuildLayout();

        // Engine events can fire on timer threads; marshal to the UI thread.
        _engine.WallpaperChanged += wp => SafeInvoke(() => ShowPreview(wp));
        _engine.StateChanged += st => SafeInvoke(() => RenderState(st));
        // A wallpaper was added/removed on disk — refresh the grid.
        _engine.LibraryChanged += () => SafeInvoke(async () => await RefreshLibraryAsync());

        _grid.PreviewRequested += card => ShowPreview(card.Wallpaper);
        _grid.CheckChanged += UpdateMeta;

        _countdownTimer.Tick += (_, _) => UpdateCountdown();
        _countdownTimer.Start();

        Load += async (_, _) => await OnFirstLoadAsync();
        // Keep the form hidden (not closed) so the tray icon keeps working.
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                UiState.From(this).Save(_uiStatePath);
                Hide();
            }
        };
    }

    private void BuildLayout()
    {
        var w = ClientSize.Width - Side * 2;   // content width
        var h = ClientSize.Height;

        var title = new Label
        {
            Text = "Lively Playlist",
            Font = DarkTheme.TitleFont,
            ForeColor = DarkTheme.TextPrimary,
            AutoSize = true,
            Location = new Point(Side, 14),
        };
        Controls.Add(title);

        // ---- Hero card ----
        _nowPlaying.Location = new Point(Side, 48);
        _nowPlaying.Size = new Size(w, 112);
        _nowPlaying.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _nowPlaying.Status = "Stopped";
        _nowPlaying.Title = "—";
        Controls.Add(_nowPlaying);

        // ---- Playlist selector row ----
        Controls.Add(SectionLabel("PLAYLIST", Side, 172));

        _playlistSelector.Location = new Point(Side, 190);
        _playlistSelector.Size = new Size(210, 28);
        _playlistSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _playlistSelector.BackColor = DarkTheme.SurfaceRaised;
        _playlistSelector.ForeColor = DarkTheme.TextPrimary;
        _playlistSelector.FlatStyle = FlatStyle.Flat;
        _playlistSelector.Font = DarkTheme.BodyFont;
        _playlistSelector.SelectedIndexChanged += async (_, _) => await OnPlaylistSelectedAsync();
        Controls.Add(_playlistSelector);

        _newPlaylistButton.Text = "New";
        _newPlaylistButton.Location = new Point(236, 190);
        _newPlaylistButton.Size = new Size(54, 28);
        _newPlaylistButton.Click += async (_, _) => await OnNewPlaylistAsync();
        Controls.Add(_newPlaylistButton);

        _renamePlaylistButton.Text = "Rename";
        _renamePlaylistButton.Location = new Point(298, 190);
        _renamePlaylistButton.Size = new Size(70, 28);
        _renamePlaylistButton.Click += async (_, _) => await OnRenamePlaylistAsync();
        Controls.Add(_renamePlaylistButton);

        _deletePlaylistButton.Text = "Delete";
        _deletePlaylistButton.Location = new Point(376, 190);
        _deletePlaylistButton.Size = new Size(66, 28);
        _deletePlaylistButton.Variant = RoundedButton.Kind.Danger;
        _deletePlaylistButton.Click += async (_, _) => await OnDeletePlaylistAsync();
        Controls.Add(_deletePlaylistButton);

        // ---- Settings ----
        Controls.Add(SectionLabel("SETTINGS", Side, 230));

        Controls.Add(Caption("Interval (min)", Side, 248));
        _intervalInput.Location = new Point(Side, 266);
        _intervalInput.Size = new Size(120, 26);
        _intervalInput.Minimum = 1;
        _intervalInput.Maximum = 1440;
        _intervalInput.BackColor = DarkTheme.SurfaceRaised;
        _intervalInput.ForeColor = DarkTheme.TextPrimary;
        _intervalInput.BorderStyle = BorderStyle.FixedSingle;
        _intervalInput.Font = DarkTheme.BodyFont;
        _intervalInput.ValueChanged += (_, _) => PushLiveSettings();
        Controls.Add(_intervalInput);

        Controls.Add(Caption("Mode", 158, 248));
        _modeInput.Location = new Point(158, 266);
        _modeInput.Size = new Size(140, 26);
        _modeInput.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeInput.BackColor = DarkTheme.SurfaceRaised;
        _modeInput.ForeColor = DarkTheme.TextPrimary;
        _modeInput.FlatStyle = FlatStyle.Flat;
        _modeInput.Font = DarkTheme.BodyFont;
        _modeInput.Items.AddRange(new object[] { "Sequential", "Shuffle" });
        _modeInput.SelectedIndexChanged += (_, _) => PushLiveSettings();
        Controls.Add(_modeInput);

        // ---- Startup behavior ----
        _startWithWindowsCheck.Text = "Start with Windows";
        _startWithWindowsCheck.Location = new Point(Side, 302);
        _startWithWindowsCheck.Size = new Size(210, 22);
        StyleCheckBox(_startWithWindowsCheck);
        _startWithWindowsCheck.CheckedChanged += (_, _) => OnStartWithWindowsChanged();
        Controls.Add(_startWithWindowsCheck);

        _autoStartCheck.Text = "Auto-start with Lively";
        _autoStartCheck.Location = new Point(238, 302);
        _autoStartCheck.Size = new Size(204, 22);
        StyleCheckBox(_autoStartCheck);
        _autoStartCheck.CheckedChanged += (_, _) => OnAutoStartChanged();
        Controls.Add(_autoStartCheck);

        // ---- Wallpaper picker grid ----
        Controls.Add(SectionLabel("WALLPAPERS", Side, 334));

        _selectAllButton.Text = "All";
        _selectAllButton.Location = new Point(330, 330);
        _selectAllButton.Size = new Size(52, 26);
        _selectAllButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _selectAllButton.Click += (_, _) => SetAllChecks(true);
        Controls.Add(_selectAllButton);

        _selectNoneButton.Text = "None";
        _selectNoneButton.Location = new Point(388, 330);
        _selectNoneButton.Size = new Size(54, 26);
        _selectNoneButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _selectNoneButton.Click += (_, _) => SetAllChecks(false);
        Controls.Add(_selectNoneButton);

        _grid.Location = new Point(Side, 364);
        _grid.Size = new Size(w, Math.Max(80, h - 508));
        _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_grid);

        // ---- Transport (anchored to the bottom) ----
        var rowY = h - 132;

        _prevButton.Text = "Previous";
        _prevButton.Location = new Point(Side, rowY);
        _prevButton.Size = new Size(136, 36);
        _prevButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _prevButton.Click += async (_, _) => await GuardAsync(_engine.PreviousAsync);
        Controls.Add(_prevButton);

        _toggleButton.Text = "Start";
        _toggleButton.Location = new Point(162, rowY);
        _toggleButton.Size = new Size(136, 36);
        _toggleButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _toggleButton.Variant = RoundedButton.Kind.Primary;
        _toggleButton.Click += async (_, _) => await OnToggleAsync();
        Controls.Add(_toggleButton);

        _nextButton.Text = "Next";
        _nextButton.Location = new Point(306, rowY);
        _nextButton.Size = new Size(136, 36);
        _nextButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _nextButton.Click += async (_, _) => await GuardAsync(_engine.NextAsync);
        Controls.Add(_nextButton);

        _saveButton.Text = "Save Playlist";
        _saveButton.Location = new Point(Side, h - 86);
        _saveButton.Size = new Size(w, 36);
        _saveButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _saveButton.Click += async (_, _) => await OnSaveAsync();
        Controls.Add(_saveButton);

        _exitButton.Text = "Exit";
        _exitButton.Location = new Point(Side, h - 44);
        _exitButton.Size = new Size(w, 30);
        _exitButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _exitButton.Variant = RoundedButton.Kind.Danger;
        _exitButton.Click += (_, _) => Application.Exit();
        Controls.Add(_exitButton);
    }

    private static Label SectionLabel(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        Font = DarkTheme.BoldSmallFont,
        ForeColor = DarkTheme.TextMuted,
    };

    private static Label Caption(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        Font = DarkTheme.SmallFont,
        ForeColor = DarkTheme.TextMuted,
    };

    private static void StyleCheckBox(CheckBox c)
    {
        c.ForeColor = DarkTheme.TextPrimary;
        c.BackColor = DarkTheme.Backdrop;
        c.FlatStyle = FlatStyle.Flat;
        c.FlatAppearance.BorderColor = DarkTheme.Border;
        c.Font = DarkTheme.SmallFont;
    }

    private async Task OnFirstLoadAsync()
    {
        PopulatePlaylistSelector();
        await LoadActivePlaylistIntoUiAsync();

        // Seed the app-level startup checkboxes (guarded so we don't re-save).
        _seedingControls = true;
        try
        {
            _startWithWindowsCheck.Checked = _engine.Config.StartWithWindows;
            _autoStartCheck.Checked = _engine.Config.AutoStartWithLively;
        }
        finally
        {
            _seedingControls = false;
        }

        await RefreshLibraryAsync();
        await RefreshStatsAsync();

        // Reflect the engine's real state — it may already be auto-starting/running
        // by the time the form loads (Program kicks StartAsync off in the background).
        RenderState(_engine.State);

        // If already running, show whatever is on screen now.
        if (_engine.CurrentWallpaper is { } current)
            ShowPreview(current);
    }

    /// <summary>Seed the interval/mode controls from the engine's active playlist.</summary>
    private async Task LoadActivePlaylistIntoUiAsync()
    {
        var active = _engine.Config.ActivePlaylist;
        if (active is null) return;

        _seedingControls = true;
        try
        {
            _intervalInput.Value = (decimal)Math.Clamp(active.IntervalSeconds / 60.0, 1, 1440);
            _modeInput.SelectedIndex = active.Mode == PlayMode.Shuffle ? 1 : 0;
        }
        finally
        {
            _seedingControls = false;
        }

        await RefreshLibraryAsync();
        UpdateMeta();
    }

    /// <summary>Rebuild the playlist dropdown from config, selecting the active one.</summary>
    private void PopulatePlaylistSelector()
    {
        _seedingControls = true; // don't let the reselection trigger a load yet
        try
        {
            _playlistSelector.Items.Clear();
            var cfg = _engine.Config;
            foreach (var p in cfg.Playlists)
                _playlistSelector.Items.Add(new PlaylistItem(p));

            var idx = cfg.Playlists.FindIndex(p => p.Id == cfg.ActivePlaylistId);
            _playlistSelector.SelectedIndex = idx >= 0 ? idx : 0;
        }
        finally
        {
            _seedingControls = false;
        }
    }

    /// <summary>User picked a different playlist → make it active and load it immediately.</summary>
    private async Task OnPlaylistSelectedAsync()
    {
        if (_seedingControls) return;
        if (_playlistSelector.SelectedItem is not PlaylistItem item) return;

        var cfg = _engine.Config;
        if (cfg.ActivePlaylistId == item.Playlist.Id) return;

        cfg.ActivePlaylistId = item.Playlist.Id;
        await GuardAsync(() => _engine.UpdateConfigAsync(cfg));
        await LoadActivePlaylistIntoUiAsync();
        RenderState(_engine.State);
    }

    /// <summary>Create a new empty playlist, switch to it, and let the user name it.</summary>
    private async Task OnNewPlaylistAsync()
    {
        var name = PromptForText("New Playlist", "Playlist name:", "New Playlist");
        if (name is null) return;

        var cfg = _engine.Config;
        var created = new PlaylistDefinition { Name = name };
        cfg.Playlists.Add(created);
        cfg.ActivePlaylistId = created.Id;

        PopulatePlaylistSelector();
        await GuardAsync(() => _engine.UpdateConfigAsync(cfg));
        await LoadActivePlaylistIntoUiAsync();
        RenderState(_engine.State);
    }

    private async Task OnRenamePlaylistAsync()
    {
        var active = _engine.Config.ActivePlaylist;
        if (active is null) return;

        var name = PromptForText("Rename Playlist", "New name:", active.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        active.Name = name;
        PopulatePlaylistSelector();
        await GuardAsync(() => _engine.UpdateConfigAsync(_engine.Config));
    }

    private async Task OnDeletePlaylistAsync()
    {
        var cfg = _engine.Config;
        var active = cfg.ActivePlaylist;
        if (active is null) return;

        var confirm = MessageBox.Show(this,
            $"Delete playlist \"{active.Name}\"?", "Lively Playlist",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        var wasActive = cfg.ActivePlaylistId == active.Id;
        cfg.Playlists.Remove(active);

        // Deleting the last playlist leaves an empty config — recreate a fresh one.
        if (cfg.Playlists.Count == 0)
            cfg.Playlists.Add(new PlaylistDefinition { Name = "Default" });

        if (wasActive)
        {
            // Per agreed design: deleting the playing playlist stops playback.
            _engine.Stop();
            cfg.ActivePlaylistId = cfg.Playlists[0].Id;
        }
        else if (cfg.Playlists.All(p => p.Id != cfg.ActivePlaylistId))
        {
            cfg.ActivePlaylistId = cfg.Playlists[0].Id;
        }

        PopulatePlaylistSelector();
        await GuardAsync(() => _engine.UpdateConfigAsync(cfg));
        await LoadActivePlaylistIntoUiAsync();
        RenderState(_engine.State);
    }

    /// <summary>Simple text prompt styled to match the dark theme.</summary>
    private string? PromptForText(string title, string label, string initial)
    {
        using var dlg = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(320, 112),
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = DarkTheme.Backdrop,
        };

        dlg.Controls.Add(Caption(label, 12, 12));

        var box = new TextBox
        {
            Location = new Point(12, 34),
            Size = new Size(296, 24),
            Text = initial,
            BackColor = DarkTheme.SurfaceRaised,
            ForeColor = DarkTheme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = DarkTheme.BodyFont,
        };
        dlg.Controls.Add(box);

        var ok = new RoundedButton
        {
            Text = "OK",
            Location = new Point(152, 70),
            Size = new Size(72, 30),
            Variant = RoundedButton.Kind.Primary,
        };
        ok.Click += (_, _) => dlg.DialogResult = DialogResult.OK;
        dlg.Controls.Add(ok);

        var cancel = new RoundedButton
        {
            Text = "Cancel",
            Location = new Point(232, 70),
            Size = new Size(76, 30),
        };
        cancel.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;
        dlg.Controls.Add(cancel);

        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        return dlg.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
    }

    /// <summary>Wrapper so the ComboBox can show playlist names.</summary>
    private sealed class PlaylistItem
    {
        public PlaylistDefinition Playlist { get; }
        public PlaylistItem(PlaylistDefinition p) => Playlist = p;
        public override string ToString() => Playlist.Name;
    }

    /// <summary>Apply interval/mode to the running engine as soon as the user edits them.</summary>
    private void PushLiveSettings()
    {
        if (_seedingControls) return;
        if (_modeInput.SelectedIndex < 0) return; // combo not populated yet

        var seconds = (int)_intervalInput.Value * 60;
        var mode = _modeInput.SelectedIndex == 1 ? PlayMode.Shuffle : PlayMode.Sequential;
        _engine.ApplyLiveSettings(seconds, mode);
    }

    /// <summary>Toggle the HKCU Run key and persist the setting.</summary>
    private void OnStartWithWindowsChanged()
    {
        if (_seedingControls) return;

        _engine.Config.StartWithWindows = _startWithWindowsCheck.Checked;
        try
        {
            if (_startWithWindowsCheck.Checked)
                StartupManager.Enable();
            else
                StartupManager.Disable();
        }
        catch (Exception ex)
        {
            Logger.Error("Could not update start-with-Windows registration", ex);
            MessageBox.Show(this,
                "Could not update the startup registration: " + ex.Message,
                "Lively Playlist", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        SaveAppSettings();
    }

    /// <summary>Persist the auto-start-when-Lively-opens setting (takes effect next launch).</summary>
    private void OnAutoStartChanged()
    {
        if (_seedingControls) return;
        _engine.Config.AutoStartWithLively = _autoStartCheck.Checked;
        SaveAppSettings();
    }

    /// <summary>Persist app-level (non-playlist) settings without disturbing playback.</summary>
    private void SaveAppSettings()
    {
        try
        {
            _engine.Config.Save(_configPath);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not save app settings", ex);
        }
    }

    /// <summary>Rebuild the picker grid from the engine's library + current selection.</summary>
    private async Task RefreshLibraryAsync()
    {
        try
        {
            // Engine refreshes its library on start; if not started yet, pull once here.
            if (_engine.Library.Count == 0)
                await _engine.ReloadAsync();

            var library = _engine.Library;
            var selected = _engine.Config.ActivePlaylist?.Wallpapers ?? new List<string>();
            // An empty selection means "all".
            var flags = library.Select(wp => selected.Count == 0 || selected.Contains(wp.LivelyInfoPath)).ToList();

            _grid.SetWallpapers(library, flags);
            UpdateMeta();
        }
        catch (Exception ex)
        {
            Logger.Error("Could not load wallpaper library", ex);
            MessageBox.Show(this, "Could not reach Lively.\n\n" + ex.Message, "Lively Playlist",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetAllChecks(bool value)
    {
        _grid.SetAll(value);
        UpdateMeta();
    }

    /// <summary>Refresh the "N selected · N screens" line on the hero card.</summary>
    private void UpdateMeta()
    {
        _nowPlaying.Meta = $"{_grid.CheckedCount} selected · {_monitorCount} screen(s)";
    }

    private void ShowPreview(WallpaperInfo wallpaper)
    {
        _nowPlaying.Title = wallpaper.Title;
        _nowPlaying.Thumbnail = UiDraw.LoadCover(wallpaper.ThumbnailPath, new Size(150, 84));
        _grid.Highlight(wallpaper);
    }

    private async Task OnSaveAsync()
    {
        try
        {
            var library = _engine.Library;
            var checkedFlags = _grid.GetChecked();
            var chosen = new List<string>();
            for (var i = 0; i < library.Count && i < checkedFlags.Length; i++)
            {
                if (checkedFlags[i]) chosen.Add(library[i].LivelyInfoPath);
            }

            if (chosen.Count == 0)
            {
                var r = MessageBox.Show(this,
                    "No wallpapers are selected. The playlist will be empty and won't change anything.\n\nSave anyway?",
                    "Lively Playlist", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }

            // Mutate the ACTIVE playlist, then persist the whole config.
            var active = _engine.Config.ActivePlaylist;
            if (active is null) return;
            active.Wallpapers = chosen;
            active.IntervalSeconds = (int)_intervalInput.Value * 60;
            active.Mode = _modeInput.SelectedIndex == 1 ? PlayMode.Shuffle : PlayMode.Sequential;

            await GuardAsync(() => _engine.UpdateConfigAsync(_engine.Config));
            UpdateMeta();
            RenderState(_engine.State);

            MessageBox.Show(this, "Playlist saved.", "Lively Playlist",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Logger.Error("Save failed", ex);
            MessageBox.Show(this, ex.Message, "Lively Playlist", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task OnToggleAsync()
    {
        // Drive the engine; StateChanged then updates the labels for us.
        if (_engine.IsRunning)
        {
            _engine.Pause();
        }
        else
        {
            // Stopped → full start (scans library); Paused → resume.
            await GuardAsync(_engine.State == PlaylistState.Paused
                ? _engine.ResumeAsync
                : _engine.StartAsync);
        }
    }

    /// <summary>
    /// Single source of truth for the status text/colour and the toggle button.
    /// Called on load and on every StateChanged event.
    /// </summary>
    private void RenderState(PlaylistState state)
    {
        switch (state)
        {
            case PlaylistState.Running:
                _nowPlaying.Status = "Running";
                _nowPlaying.StatusColor = DarkTheme.AccentHover;
                _toggleButton.Text = "Pause";
                _toggleButton.Variant = RoundedButton.Kind.Secondary;
                break;
            case PlaylistState.Paused:
                _nowPlaying.Status = "Paused";
                // Red = clearly not running, so a paused state is never mistaken for active.
                _nowPlaying.StatusColor = DarkTheme.DangerHover;
                _toggleButton.Text = "Resume";
                _toggleButton.Variant = RoundedButton.Kind.Primary;
                break;
            default:
                _nowPlaying.Status = "Stopped";
                _nowPlaying.StatusColor = DarkTheme.DangerHover;
                _toggleButton.Text = "Start";
                _toggleButton.Variant = RoundedButton.Kind.Primary;
                break;
        }
        _toggleButton.Invalidate();
        UpdateCountdown();
    }

    /// <summary>Refresh the countdown text + progress bar (fires every second).</summary>
    private void UpdateCountdown()
    {
        if (_engine.State != PlaylistState.Running)
        {
            _nowPlaying.Countdown = "";
            _nowPlaying.Progress = 0;
            return;
        }

        var remaining = _engine.TimeUntilNextChange;
        // Round up so we don't show 0:00 for the tail end of an interval.
        var secs = (int)Math.Ceiling(remaining.TotalSeconds);
        _nowPlaying.Countdown = secs >= 60 ? $"next in {secs / 60}:{secs % 60:00}" : $"next in {secs}s";

        var interval = _engine.Config.ActivePlaylist?.IntervalSeconds ?? 0;
        _nowPlaying.Progress = interval > 0 ? 1 - remaining.TotalSeconds / interval : 0;
    }

    private async Task RefreshStatsAsync()
    {
        try
        {
            var monitors = await _client.GetMonitorsAsync();
            _monitorCount = monitors.Count;
        }
        catch
        {
            _monitorCount = 0;
        }
        UpdateMeta();
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Logger.Error("Action failed", ex);
            MessageBox.Show(this, ex.Message, "Lively Playlist", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Refresh the window to match the engine's current config (e.g. after a tray switch).</summary>
    public void SyncFromEngine()
    {
        SafeInvoke(async () =>
        {
            PopulatePlaylistSelector();
            await LoadActivePlaylistIntoUiAsync();
            RenderState(_engine.State);
        });
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
            BeginInvoke(action);
        else
            action();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _countdownTimer.Stop();
            _countdownTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
