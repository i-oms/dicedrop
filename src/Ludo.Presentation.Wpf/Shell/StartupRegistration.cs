using Microsoft.Win32;

namespace Ludo.Presentation.Wpf.Shell;

/// <summary>
/// "Start with Windows" as a per-user Run entry: no installer, no admin rights, and nothing but this one
/// registry value. The entry starts the game hidden in the tray. On by default from the first run.
/// </summary>
internal static class StartupRegistration
{
    public const string TrayArgument = "--tray";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DiceDrop";
    private const string LegacyValueName = "LudoP2P"; // the game's name before it became DiceDrop

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    private static string Command => $"\"{Environment.ProcessPath}\" {TrayArgument}";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, Command);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>The first-run default. Only for a first run, so turning it off later sticks.</summary>
    public static void EnableOnFirstRun()
    {
        try
        {
            SetEnabled(true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            // Not worth failing startup over; it can still be turned on in settings.
        }
    }

    /// <summary>The exe is portable and may have moved since it was registered: point the entry at this copy.</summary>
    public static void RefreshPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(LegacyValueName) is string)
            {
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                key.SetValue(ValueName, Command);
            }

            if (key?.GetValue(ValueName) is string current && current != Command)
            {
                key.SetValue(ValueName, Command);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            // Not worth failing startup over; the old entry stays.
        }
    }
}
