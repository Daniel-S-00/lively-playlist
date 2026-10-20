using Microsoft.Win32;

namespace LivelyPlaylist;

/// <summary>
/// Manages the per-user "Run" registry key so the app can launch at Windows sign-in.
/// HKCU (not HKLM) so no admin elevation is required.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LivelyPlaylist";

    /// <summary>Point the Run key at the current executable (quoted, in case of spaces).</summary>
    public static void Enable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        var exePath = Environment.ProcessPath
                      ?? throw new InvalidOperationException("Could not resolve the executable path.");
        key.SetValue(ValueName, $"\"{exePath}\"");
        Logger.Info($"Start-with-Windows enabled: {exePath}");
    }

    /// <summary>Remove the Run key entry.</summary>
    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            Logger.Info("Start-with-Windows disabled.");
        }
    }

    /// <summary>Whether the Run key currently points at this app.</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is not null;
    }
}
