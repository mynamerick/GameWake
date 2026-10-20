using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GameWake.App.Services;

/// <summary>
/// Per-user "launch with Windows" via <c>HKCU\...\Run</c>. No administrator rights, no scheduled
/// tasks, and nothing outside the current user's hive is touched.
/// </summary>
public sealed class StartupRegistration(ILogger<StartupRegistration> logger)
{
    public const string ValueName = "GameWake";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    public bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>True when the user disabled GameWake in Task Manager's Startup apps list.</summary>
    public bool IsDisabledByUser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: false);
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Adds, updates (e.g. after the exe moved) or removes the entry to match the setting.</summary>
    public void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            var current = key.GetValue(ValueName) as string;
            if (enabled)
            {
                if (!string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, Command, RegistryValueKind.String);
                    logger.LogInformation("Registered GameWake to start with Windows");
                }
            }
            else if (current is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                logger.LogInformation("Removed GameWake from Windows startup");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogError(ex, "Could not update the Windows startup entry");
        }
    }
}
