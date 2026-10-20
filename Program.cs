namespace LivelyPlaylist;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var configPath = PlaylistConfig.DefaultPath;
        var config = PlaylistConfig.EnsureExists(configPath);

        // Keep the registry Run key in sync with the saved setting every launch.
        SyncStartupRegistration(config);

        using var client = new LivelyClient();
        using var engine = new PlaylistEngine(client, config, configPath);
        using var mainForm = new MainForm(client, engine, configPath);
        using var tray = new TrayApp(engine, configPath, mainForm);

        using var exitCts = new CancellationTokenSource();
        Application.ApplicationExit += (_, _) =>
        {
            exitCts.Cancel();
            engine.Dispose();
        };

        // Start minimized to tray. Only auto-start once Lively's core is reachable;
        // if it isn't up yet, idle in the tray and start the moment it appears
        // (AutoStartWithLively), otherwise wait for the user to press Start.
        _ = Task.Run(async () =>
        {
            try
            {
                if (LivelyClient.IsCoreRunning())
                {
                    await engine.StartAsync();
                    return;
                }

                Logger.Info("Lively core not running yet — waiting in tray.");
                tray.NotifyLivelyNotRunning(config.AutoStartWithLively);

                // Poll for the pipe. Without AutoStartWithLively we still wait, so a
                // later manual Start works; we just don't auto-start on connect.
                var connected = await LivelyClient.WaitForCoreAsync(TimeSpan.FromSeconds(2), exitCts.Token);
                if (!connected || exitCts.IsCancellationRequested)
                    return;

                Logger.Info("Lively core detected.");
                if (config.AutoStartWithLively)
                    await engine.StartAsync();
                else
                    tray.NotifyLivelyReady();
            }
            catch (OperationCanceledException)
            {
                // App is exiting.
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to start playlist", ex);
            }
        });

        Application.Run();
    }

    /// <summary>Write or remove the HKCU Run key so it matches the config setting.</summary>
    private static void SyncStartupRegistration(PlaylistConfig config)
    {
        try
        {
            if (config.StartWithWindows)
                StartupManager.Enable();
            else
                StartupManager.Disable();
        }
        catch (Exception ex)
        {
            // Non-fatal: the app runs fine, it just won't auto-launch.
            Logger.Error("Could not update start-with-Windows registration", ex);
        }
    }
}
