using System.Drawing;
using System.Drawing.Imaging;

namespace LivelyPlaylist;

/// <summary>
/// Dark-themed control window shown on tray double-click. Displays playlist
/// status, a live thumbnail preview of the current wallpaper, a wallpaper
/// picker to curate the playlist, and the main controls.
/// </summary>
public sealed class MainForm : Form
{
    private readonly LivelyClient _client;
    private readonly PlaylistEngine _engine;
    private readonly string _configPath;

    private readonly Label _statusValue = new();
    private readonly Label _countdownLabel = new();
    private readonly Label _currentValue = new();
    private readonly Label _poolValue = new();
    private readonly Label _monitorsValue = new();

    // 1s UI timer that refreshes the countdown display from the engine.
    private readonly System.Windows.Forms.Timer _countdownTimer = new() { Interval = 1000 };

    private readonly PictureBox _preview = new();
    private readonly CheckedListBox _picker = new();

    // Check state is driven manually (mouse handlers) so behavior is deterministic;
    // native ItemCheck toggles are cancelled unless this flag is set during a
    // programmatic change we intentionally allow.
    private bool _allowCheckChange;

    private readonly NumericUpDown _intervalInput = new();
    private readonly ComboBox _modeInput = new();

    // Startup behavior.
    private readonly CheckBox _startWithWindowsCheck = new();
    private readonly CheckBox _autoStartCheck = new();

    // Playlist management controls.
    private readonly ComboBox _playlistSelector = new();
    private readonly Button _newPlaylistButton = new();
    private readonly Button _renamePlaylistButton = new();
    private readonly Button _deletePlaylistButton = new();

    private readonly Button _toggleButton = new();
    private readonly Button _prevButton = new();
    private readonly Button _nextButton = new();
    private readonly Button _selectAllButton = new();
    private readonly Button _selectNoneButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _exitButton = new();

    // The library backing the picker; index i in _picker maps to _pickerLibrary[i].
    private List<WallpaperInfo> _pickerLibrary = new();

    // Blocks live-settings pushes while we programmatically seed the controls on load.
    private bool _seedingControls;

    public MainForm(LivelyClient client, PlaylistEngine engine, string configPath)
    {
        _client = client;
        _engine = engine;
        _configPath = configPath;

        Text = "Lively Playlist";
        Icon = AppIcon.Icon;
        // Author this layout at 96 DPI and let WinForms scale everything (fonts +
        // control bounds) up on higher-DPI displays — otherwise fixed-pixel button
        // heights clip their text at >100% scaling.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        // ClientSize (not Size) so the title bar/borders can't push controls off-screen.
        ClientSize = new Size(420, 720);
        MinimumSize = new Size(440, 760);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        BackColor = DarkTheme.Backdrop;

        BuildLayout();

        // Engine events can fire on timer threads; marshal to the UI thread.
        _engine.StatusChanged += status => SafeInvoke(() => _currentValue.Text = status);
        _engine.WallpaperChanged += wp => SafeInvoke(() => ShowPreview(wp));
        _engine.StateChanged += st => SafeInvoke(() => RenderState(st));
        // A wallpaper was added/removed on disk — refresh the picker list.
        _engine.LibraryChanged += () => SafeInvoke(async () => await RefreshLibraryAsync());

        // Drive the countdown label from the engine's remaining-time.
        _countdownTimer.Tick += (_, _) => UpdateCountdown();
        _countdownTimer.Start();

        Load += async (_, _) => await OnFirstLoadAsync();
        // Keep the form hidden (not closed) so the tray icon keeps working.
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private void BuildLayout()
    {
        var title = new Label
        {
            Text = "Lively Playlist",
            Font = new Font(Font.FontFamily, 16f, FontStyle.Bold),
            ForeColor = DarkTheme.TextPrimary,
            AutoSize = true,
            Location = new Point(20, 16),
        };
        Controls.Add(title);

        // ---- Status card (left) + live preview (right) ----
        var card = new Panel
        {
            BackColor = DarkTheme.Surface,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(20, 54),
            Size = new Size(210, 132),
        };
        Controls.Add(card);

        var statusHeading = MakeCaption("STATUS", 14, 12);
        card.Controls.Add(statusHeading);

        // Countdown to the next automatic change (right-aligned on the status row).
        _countdownLabel.Text = "";
        _countdownLabel.Font = new Font(Font.FontFamily, 9f, FontStyle.Bold);
        _countdownLabel.ForeColor = DarkTheme.TextMuted;
        _countdownLabel.TextAlign = ContentAlignment.MiddleRight;
        _countdownLabel.AutoSize = false;
        _countdownLabel.Size = new Size(110, 16);
        _countdownLabel.Location = new Point(88, 13);
        card.Controls.Add(_countdownLabel);

        _statusValue.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        _statusValue.ForeColor = DarkTheme.TextPrimary;
        _statusValue.AutoSize = true;
        _statusValue.Location = new Point(14, 30);
        card.Controls.Add(_statusValue);

        var currentHeading = MakeCaption("CURRENT", 14, 60);
        card.Controls.Add(currentHeading);

        _currentValue.Text = "—";
        _currentValue.Font = new Font(Font.FontFamily, 9.5f, FontStyle.Regular);
        _currentValue.ForeColor = DarkTheme.AccentHover;
        _currentValue.AutoEllipsis = true;
        _currentValue.Size = new Size(182, 18);
        _currentValue.Location = new Point(14, 78);
        card.Controls.Add(_currentValue);

        var poolHeading = MakeCaption("PLAYLIST", 14, 104);
        card.Controls.Add(poolHeading);
        _poolValue.Text = "0";
        _poolValue.Font = new Font(Font.FontFamily, 9.5f, FontStyle.Regular);
        _poolValue.ForeColor = DarkTheme.TextPrimary;
        _poolValue.AutoSize = true;
        _poolValue.Location = new Point(84, 104);
        card.Controls.Add(_poolValue);

        var monHeading = MakeCaption("SCREENS", 120, 104);
        card.Controls.Add(monHeading);
        _monitorsValue.Text = "?";
        _monitorsValue.Font = new Font(Font.FontFamily, 9.5f, FontStyle.Regular);
        _monitorsValue.ForeColor = DarkTheme.TextPrimary;
        _monitorsValue.AutoSize = true;
        _monitorsValue.Location = new Point(180, 104);
        card.Controls.Add(_monitorsValue);

        // Live thumbnail preview
        _preview.Location = new Point(240, 54);
        _preview.Size = new Size(160, 132);
        _preview.BackColor = DarkTheme.Surface;
        _preview.BorderStyle = BorderStyle.FixedSingle;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        Controls.Add(_preview);

        var previewCaption = MakeCaption("PREVIEW", 240, 190);
        Controls.Add(previewCaption);

        // ---- Playlist management: selector + new/rename/delete ----
        var playlistCaption = MakeCaption("PLAYLIST", 20, 212);
        playlistCaption.ForeColor = DarkTheme.TextPrimary;
        Controls.Add(playlistCaption);

        _playlistSelector.Location = new Point(20, 230);
        _playlistSelector.Size = new Size(200, 26);
        _playlistSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _playlistSelector.BackColor = DarkTheme.SurfaceRaised;
        _playlistSelector.ForeColor = DarkTheme.TextPrimary;
        _playlistSelector.FlatStyle = FlatStyle.Flat;
        _playlistSelector.SelectedIndexChanged += async (_, _) => await OnPlaylistSelectedAsync();
        Controls.Add(_playlistSelector);

        _newPlaylistButton.Text = "New";
        _newPlaylistButton.Location = new Point(226, 230);
        _newPlaylistButton.Size = new Size(50, 26);
        _newPlaylistButton.Click += async (_, _) => await OnNewPlaylistAsync();
        DarkTheme.StyleButton(_newPlaylistButton);
        Controls.Add(_newPlaylistButton);

        _renamePlaylistButton.Text = "Rename";
        _renamePlaylistButton.Location = new Point(282, 230);
        _renamePlaylistButton.Size = new Size(62, 26);
        _renamePlaylistButton.Click += async (_, _) => await OnRenamePlaylistAsync();
        DarkTheme.StyleButton(_renamePlaylistButton);
        Controls.Add(_renamePlaylistButton);

        _deletePlaylistButton.Text = "Delete";
        _deletePlaylistButton.Location = new Point(350, 230);
        _deletePlaylistButton.Size = new Size(50, 26);
        _deletePlaylistButton.Click += async (_, _) => await OnDeletePlaylistAsync();
        DarkTheme.StyleButton(_deletePlaylistButton);
        Controls.Add(_deletePlaylistButton);

        // ---- Playlist editor: interval + mode ----
        var editorCaption = MakeCaption("SETTINGS", 20, 268);
        editorCaption.ForeColor = DarkTheme.TextPrimary;
        Controls.Add(editorCaption);

        var intervalCaption = MakeCaption("Interval (min)", 20, 294);
        Controls.Add(intervalCaption);
        _intervalInput.Location = new Point(20, 312);
        _intervalInput.Size = new Size(120, 24);
        _intervalInput.Minimum = 1;
        _intervalInput.Maximum = 1440;
        _intervalInput.BackColor = DarkTheme.SurfaceRaised;
        _intervalInput.ForeColor = DarkTheme.TextPrimary;
        _intervalInput.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_intervalInput);

        var modeCaption = MakeCaption("Mode", 170, 294);
        Controls.Add(modeCaption);
        _modeInput.Location = new Point(170, 312);
        _modeInput.Size = new Size(120, 24);
        _modeInput.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeInput.BackColor = DarkTheme.SurfaceRaised;
        _modeInput.ForeColor = DarkTheme.TextPrimary;
        _modeInput.FlatStyle = FlatStyle.Flat;
        _modeInput.Items.AddRange(new object[] { "Sequential", "Shuffle" });
        Controls.Add(_modeInput);

        // Apply interval/mode changes immediately — no Save click required.
        _intervalInput.ValueChanged += (_, _) => PushLiveSettings();
        _modeInput.SelectedIndexChanged += (_, _) => PushLiveSettings();

        // ---- Startup behavior ----
        _startWithWindowsCheck.Text = "Start with Windows";
        _startWithWindowsCheck.Location = new Point(20, 344);
        _startWithWindowsCheck.Size = new Size(180, 22);
        _startWithWindowsCheck.ForeColor = DarkTheme.TextPrimary;
        _startWithWindowsCheck.BackColor = DarkTheme.Backdrop;
        _startWithWindowsCheck.FlatStyle = FlatStyle.Flat;
        _startWithWindowsCheck.FlatAppearance.BorderColor = DarkTheme.Border;
        _startWithWindowsCheck.CheckedChanged += (_, _) => OnStartWithWindowsChanged();
        Controls.Add(_startWithWindowsCheck);

        _autoStartCheck.Text = "Auto-start when Lively opens";
        _autoStartCheck.Location = new Point(200, 344);
        _autoStartCheck.Size = new Size(200, 22);
        _autoStartCheck.ForeColor = DarkTheme.TextPrimary;
        _autoStartCheck.BackColor = DarkTheme.Backdrop;
        _autoStartCheck.FlatStyle = FlatStyle.Flat;
        _autoStartCheck.FlatAppearance.BorderColor = DarkTheme.Border;
        _autoStartCheck.CheckedChanged += (_, _) => OnAutoStartChanged();
        Controls.Add(_autoStartCheck);

        // ---- Wallpaper picker ----
        var pickCaption = MakeCaption("SELECT WALLPAPERS", 20, 376);
        pickCaption.ForeColor = DarkTheme.TextPrimary;
        Controls.Add(pickCaption);

        _picker.Location = new Point(20, 396);
        _picker.Size = new Size(380, 122);
        _picker.BackColor = DarkTheme.SurfaceRaised;
        _picker.ForeColor = DarkTheme.TextPrimary;
        _picker.BorderStyle = BorderStyle.FixedSingle;
        // false = clicking the TITLE only selects (shows preview); the check state
        // toggles only when clicking the checkbox square itself.
        _picker.CheckOnClick = false;
        _picker.HorizontalScrollbar = true;
        _picker.SelectedIndexChanged += (_, _) => OnPickerSelected();
        _picker.ItemCheck += OnPickerItemCheck;
        _picker.MouseDown += OnPickerMouseDown;
        Controls.Add(_picker);

        _selectAllButton.Text = "All";
        _selectAllButton.Location = new Point(20, 524);
        _selectAllButton.Size = new Size(74, 30);
        _selectAllButton.Click += (_, _) => SetAllChecks(true);
        DarkTheme.StyleButton(_selectAllButton);
        Controls.Add(_selectAllButton);

        _selectNoneButton.Text = "None";
        _selectNoneButton.Location = new Point(100, 524);
        _selectNoneButton.Size = new Size(74, 30);
        _selectNoneButton.Click += (_, _) => SetAllChecks(false);
        DarkTheme.StyleButton(_selectNoneButton);
        Controls.Add(_selectNoneButton);

        // ---- Controls ----
        // Row 1: Previous | Start/Pause | Next
        _prevButton.Text = "Previous";
        _prevButton.Location = new Point(20, 566);
        _prevButton.Size = new Size(120, 38);
        _prevButton.Click += async (_, _) => await GuardAsync(_engine.PreviousAsync);
        DarkTheme.StyleButton(_prevButton);
        Controls.Add(_prevButton);

        _toggleButton.Location = new Point(150, 566);
        _toggleButton.Size = new Size(120, 38);
        _toggleButton.Click += async (_, _) => await OnToggleAsync();
        DarkTheme.StyleAccentButton(_toggleButton);
        Controls.Add(_toggleButton);

        _nextButton.Text = "Next Wallpaper";
        _nextButton.Location = new Point(280, 566);
        _nextButton.Size = new Size(120, 38);
        _nextButton.Click += async (_, _) => await GuardAsync(_engine.NextAsync);
        DarkTheme.StyleButton(_nextButton);
        Controls.Add(_nextButton);

        // Row 2: Save
        _saveButton.Text = "Save Playlist";
        _saveButton.Location = new Point(20, 610);
        _saveButton.Size = new Size(380, 38);
        _saveButton.Click += async (_, _) => await OnSaveAsync();
        DarkTheme.StyleAccentButton(_saveButton);
        Controls.Add(_saveButton);

        // Row 3: Exit (full width — config button removed)
        _exitButton.Text = "Exit";
        _exitButton.Location = new Point(20, 654);
        _exitButton.Size = new Size(380, 36);
        _exitButton.Click += (_, _) => Application.Exit();
        DarkTheme.StyleDangerButton(_exitButton);
        Controls.Add(_exitButton);
    }

    private static Label MakeCaption(string text, int x, int y)
    {
        var l = new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
        };
        DarkTheme.StyleCaption(l);
        return l;
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

    /// <summary>Seed the interval/mode/picker controls from the engine's active playlist.</summary>
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
        _poolValue.Text = _engine.PoolCount.ToString();
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

    /// <summary>Simple dark-agnostic text prompt (returns null if cancelled).</summary>
    private string? PromptForText(string title, string label, string initial)
    {
        using var dlg = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(320, 110),
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = DarkTheme.Backdrop,
        };

        var caption = MakeCaption(label, 12, 12);
        dlg.Controls.Add(caption);

        var box = new TextBox
        {
            Location = new Point(12, 32),
            Size = new Size(296, 24),
            Text = initial,
            BackColor = DarkTheme.SurfaceRaised,
            ForeColor = DarkTheme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
        };
        dlg.Controls.Add(box);

        var ok = new Button { Text = "OK", Location = new Point(152, 68), Size = new Size(72, 30) };
        ok.Click += (_, _) => { dlg.DialogResult = DialogResult.OK; };
        DarkTheme.StyleAccentButton(ok);
        dlg.Controls.Add(ok);

        var cancel = new Button { Text = "Cancel", Location = new Point(232, 68), Size = new Size(76, 30) };
        cancel.Click += (_, _) => { dlg.DialogResult = DialogResult.Cancel; };
        DarkTheme.StyleButton(cancel);
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

    /// <summary>Rebuild the picker list from the engine's library + current selection.</summary>
    private async Task RefreshLibraryAsync()
    {
        try
        {
            // Engine refreshes its library on start; if not started yet, pull once here.
            if (_engine.Library.Count == 0)
                await _engine.ReloadAsync();

            var library = _engine.Library;
            var selected = _engine.Config.ActivePlaylist?.Wallpapers ?? new List<string>();

            _pickerLibrary = library.ToList();
            _picker.Items.Clear();
            _allowCheckChange = true; // let the initial checked states through ItemCheck guard
            try
            {
                for (int i = 0; i < library.Count; i++)
                {
                    // An empty selection means "all"; check everything in that case.
                    var isChecked = selected.Count == 0 || selected.Contains(library[i].LivelyInfoPath);
                    _picker.Items.Add(library[i].Title, isChecked);
                }
                _poolValue.Text = _engine.PoolCount.ToString();

                if (library.Count == 0)
                    _picker.Items.Add("(no wallpapers found in Lively library)", false);
            }
            finally
            {
                _allowCheckChange = false;
            }
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
        _allowCheckChange = true;
        try
        {
            for (int i = 0; i < _picker.Items.Count; i++)
                _picker.SetItemChecked(i, value);
        }
        finally
        {
            _allowCheckChange = false;
        }
    }

    /// <summary>Selecting a row shows that wallpaper's thumbnail in the preview panel.</summary>
    private void OnPickerSelected()
    {
        var idx = _picker.SelectedIndex;
        if (idx >= 0 && idx < _pickerLibrary.Count)
            ShowPreview(_pickerLibrary[idx]);
    }

    /// <summary>
    /// Cancel native check toggles. All check-state changes go through SetChecked(),
    /// so native mouse/keyboard toggles (which are inconsistent on unfocused items)
    /// never apply — this is what makes a single click on the checkbox reliable.
    /// </summary>
    private void OnPickerItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (!_allowCheckChange)
            e.NewValue = e.CurrentValue;
    }

    /// <summary>Left edge zone occupied by the checkbox glyph.</summary>
    private int GetCheckBoxZoneWidth()
    {
        // The glyph is roughly square and sized to the row font; a few px of slack
        // makes the hit target forgiving without swallowing the row text.
        return _picker.Font.Height + 6;
    }

    /// <summary>
    /// Checkbox zone: the first press toggles. Text zone: the first press only selects
    /// (preview comes from SelectedIndexChanged); the second press of a double-click toggles.
    /// MouseDown is used instead of MouseClick because the first click on an unfocused row
    /// is consumed by focus/selection and never raises MouseClick.
    /// </summary>
    private void OnPickerMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var idx = _picker.IndexFromPoint(e.Location);
        if (idx < 0) return;

        if (e.X <= GetCheckBoxZoneWidth())
        {
            // Ignore the second press of a double-click so it cannot toggle back.
            if (e.Clicks == 1)
                SetChecked(idx, !_picker.GetItemChecked(idx));
            return;
        }

        if (e.Clicks >= 2)
            SetChecked(idx, !_picker.GetItemChecked(idx));
    }

    /// <summary>Toggle a row's check state, allowing the change through OnPickerItemCheck.</summary>
    private void SetChecked(int index, bool value)
    {
        if (index < 0 || index >= _picker.Items.Count) return;
        if (_picker.GetItemChecked(index) == value) return;
        _allowCheckChange = true;
        try
        {
            _picker.SetItemChecked(index, value);
        }
        finally
        {
            _allowCheckChange = false;
        }
    }

    private void ShowPreview(WallpaperInfo wallpaper)
    {
        try
        {
            var thumb = wallpaper.ThumbnailPath;
            if (string.IsNullOrWhiteSpace(thumb) || !File.Exists(thumb))
            {
                _preview.Image?.Dispose();
                _preview.Image = null;
                return;
            }

            // Load a copy so the file isn't locked and we can dispose the old image.
            using var fs = new FileStream(thumb, FileMode.Open, FileAccess.Read);
            using var img = Image.FromStream(fs);
            var clone = new Bitmap(img);

            var old = _preview.Image;
            _preview.Image = clone;
            old?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Preview load failed: {ex.Message}");
        }
    }

    private async Task OnSaveAsync()
    {
        try
        {
            var library = _engine.Library;
            var chosen = new List<string>();
            for (int i = 0; i < _picker.Items.Count && i < library.Count; i++)
            {
                if (_picker.GetItemChecked(i))
                    chosen.Add(library[i].LivelyInfoPath);
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
            _poolValue.Text = _engine.PoolCount.ToString();
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
    /// Single source of truth for the status label + toggle button text.
    /// Called on load and on every StateChanged event.
    /// </summary>
    private void RenderState(PlaylistState state)
    {
        switch (state)
        {
            case PlaylistState.Running:
                _statusValue.Text = "Running";
                _statusValue.ForeColor = DarkTheme.AccentHover;
                _toggleButton.Text = "Pause";
                break;
            case PlaylistState.Paused:
                _statusValue.Text = "Paused";
                // Red = clearly not running, so a paused state is never mistaken for active.
                _statusValue.ForeColor = DarkTheme.DangerHover;
                _toggleButton.Text = "Resume";
                break;
            default:
                _statusValue.Text = "Stopped";
                _statusValue.ForeColor = DarkTheme.DangerHover;
                _toggleButton.Text = "Start";
                break;
        }
    }

    /// <summary>Refresh the countdown label (fires every second from _countdownTimer).</summary>
    private void UpdateCountdown()
    {
        if (_engine.State != PlaylistState.Running)
        {
            _countdownLabel.Text = "";
            return;
        }

        var remaining = _engine.TimeUntilNextChange;
        // Round up so we don't show 0:00 for the tail end of an interval.
        var secs = (int)Math.Ceiling(remaining.TotalSeconds);
        _countdownLabel.Text = secs >= 60
            ? $"{secs / 60}:{secs % 60:00}"
            : $"{secs}s";
    }

    private async Task RefreshStatsAsync()
    {
        try
        {
            var monitors = await _client.GetMonitorsAsync();
            _monitorsValue.Text = monitors.Count.ToString();
        }
        catch
        {
            _monitorsValue.Text = "?";
        }
        _poolValue.Text = _engine.PoolCount.ToString();
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
            _preview.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
