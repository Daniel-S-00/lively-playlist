using System.Diagnostics;
using System.Drawing;

namespace LivelyPlaylist;

/// <summary>
/// System-tray host. Owns the engine + control window and exposes Pause/Resume,
/// Next, Reload, Edit Config and Exit from a dark-themed context menu.
/// </summary>
public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly PlaylistEngine _engine;
    private readonly string _configPath;
    private readonly MainForm _mainForm;

    private ToolStripMenuItem _pauseItem = null!;
    private ToolStripMenuItem _playlistsItem = null!;

    public TrayApp(PlaylistEngine engine, string configPath, MainForm mainForm)
    {
        _engine = engine;
        _configPath = configPath;
        _mainForm = mainForm;

        _icon = new NotifyIcon
        {
            Icon = AppIcon.Icon,
            Visible = true,
            Text = "Lively Playlist",
            ContextMenuStrip = BuildMenu(),
        };
        _icon.DoubleClick += (_, _) => ShowMainWindow();

        // Keep the tray menu label in sync with the engine's real state.
        _engine.StateChanged += st =>
        {
            try
            {
                var text = st == PlaylistState.Running ? "Pause" : "Resume";
                var menu = _icon.ContextMenuStrip;
                if (menu is null) return;
                if (menu.InvokeRequired)
                    menu.BeginInvoke(() => _pauseItem.Text = text);
                else
                    _pauseItem.Text = text;
            }
            catch { /* icon may be disposed during shutdown */ }
        };

        _engine.StatusChanged += status =>
        {
            // StatusChanged can fire from a timer thread; marshal to the UI thread.
            try
            {
                var text = Truncate($"Lively Playlist — {status}");
                if (_icon.ContextMenuStrip.InvokeRequired)
                    _icon.ContextMenuStrip.BeginInvoke(() => _icon.Text = text);
                else
                    _icon.Text = text;
            }
            catch { /* icon may be disposed during shutdown */ }
        };
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = DarkTheme.Surface,
        };

        _pauseItem = MakeItem("Pause", async (_, _) => await TogglePauseAsync());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(MakeItem("Next Wallpaper", async (_, _) => await SafeAsync(_engine.NextAsync)));

        // Playlist switcher — rebuilt each time the menu opens so it's always current.
        _playlistsItem = MakeItem("Playlists", null);
        _playlistsItem.DropDownOpening += (_, _) => PopulatePlaylistMenu();
        menu.Items.Add(_playlistsItem);

        menu.Items.Add(MakeItem("Reload Config", async (_, _) => await SafeAsync(_engine.ReloadAsync)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MakeItem("Open Control Panel", (_, _) => ShowMainWindow()));
        menu.Items.Add(MakeItem("Edit Config", (_, _) => OpenConfig()));
        menu.Items.Add(MakeItem("Open Log", (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MakeItem("Exit", (_, _) => Application.Exit()));

        return menu;
    }

    private static ToolStripMenuItem MakeItem(string text, EventHandler? onClick)
    {
        var item = new ToolStripMenuItem(text)
        {
            ForeColor = DarkTheme.TextPrimary,
            BackColor = DarkTheme.Surface,
            Padding = new Padding(4, 3, 4, 3),
        };
        if (onClick is not null)
            item.Click += onClick;
        return item;
    }

    /// <summary>Rebuild the Playlists submenu from the current config (runs on menu open).</summary>
    private void PopulatePlaylistMenu()
    {
        _playlistsItem.DropDownItems.Clear();

        var cfg = _engine.Config;
        foreach (var playlist in cfg.Playlists)
        {
            var item = MakeItem(playlist.Name, async (_, _) => await SwitchPlaylistAsync(playlist.Id));
            // Radio-style checkmark marks the active playlist.
            item.Checked = playlist.Id == cfg.ActivePlaylistId;
            _playlistsItem.DropDownItems.Add(item);
        }

        if (cfg.Playlists.Count == 0)
            _playlistsItem.DropDownItems.Add(MakeItem("(no playlists)", null));
    }

    private async Task SwitchPlaylistAsync(string playlistId)
    {
        var cfg = _engine.Config;
        if (cfg.ActivePlaylistId == playlistId) return;

        cfg.ActivePlaylistId = playlistId;
        await SafeAsync(() => _engine.UpdateConfigAsync(cfg));

        // Keep the control window (if open) in sync with the new active playlist.
        _mainForm.SyncFromEngine();
    }

    private void ShowMainWindow()
    {
        _mainForm.Show();
        _mainForm.Activate();
    }

    /// <summary>App started to tray but Lively isn't up yet — tell the user we're waiting.</summary>
    public void NotifyLivelyNotRunning(bool autoStart)
    {
        var message = autoStart
            ? "Lively isn't running yet. The playlist will start automatically when you open it."
            : "Lively isn't running yet. Open Lively, then press Start.";
        ShowBalloon("Waiting for Lively", message);
    }

    /// <summary>Lively came up while we were idle (and we didn't auto-start).</summary>
    public void NotifyLivelyReady()
    {
        ShowBalloon("Lively detected", "Lively is running. Press Start (or Resume) to begin the playlist.");
    }

    private void ShowBalloon(string title, string message)
    {
        try
        {
            _icon.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);
        }
        catch { /* icon may be disposed during shutdown */ }
    }

    private async Task TogglePauseAsync()
    {
        if (_engine.IsRunning)
        {
            _engine.Pause();
        }
        else
        {
            // Paused → resume (cheap); Stopped → full start (scans library).
            await SafeAsync(_engine.State == PlaylistState.Paused
                ? _engine.ResumeAsync
                : _engine.StartAsync);
        }
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Logger.Error("Tray action failed", ex);
            MessageBox.Show(ex.Message, "Lively Playlist", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenConfig()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_configPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("Could not open config", ex);
        }
    }

    private void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Logger.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("Could not open log", ex);
        }
    }

    private static string Truncate(string s) => s.Length <= 63 ? s : s[..60] + "...";

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>Custom renderer that draws the context menu in the dark palette.</summary>
public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? DarkTheme.TextPrimary : DarkTheme.TextMuted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var bg = new SolidBrush(DarkTheme.Surface);
        e.Graphics.FillRectangle(bg, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var rect = new Rectangle(Point.Empty, e.Item.Size);
        var color = e.Item.Selected ? DarkTheme.SurfaceRaised : DarkTheme.Surface;
        using var bg = new SolidBrush(color);
        e.Graphics.FillRectangle(bg, rect);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(DarkTheme.Border);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => DarkTheme.SurfaceRaised;
        public override Color MenuItemSelectedGradientBegin => DarkTheme.SurfaceRaised;
        public override Color MenuItemSelectedGradientEnd => DarkTheme.SurfaceRaised;
        public override Color MenuItemBorder => DarkTheme.Border;
        public override Color MenuBorder => DarkTheme.Border;
        public override Color ToolStripDropDownBackground => DarkTheme.Surface;
        public override Color ImageMarginGradientBegin => DarkTheme.Surface;
        public override Color ImageMarginGradientMiddle => DarkTheme.Surface;
        public override Color ImageMarginGradientEnd => DarkTheme.Surface;
    }
}
