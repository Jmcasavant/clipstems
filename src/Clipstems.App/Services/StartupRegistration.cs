using System.IO;
using Microsoft.Win32;

namespace Clipstems.App.Services;

/// <summary>
/// Start with Windows through the per-user Run key, which Windows lists (and
/// lets the user disable) under Settings > Apps > Startup.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Clipstems";

    /// <summary>
    /// True for builds run from a project's <c>bin</c> folder. Those never
    /// register, so trying a development build cannot leave a startup entry
    /// pointing at a folder that will be rebuilt or deleted.
    /// </summary>
    public static bool IsDevelopmentBuild =>
        Environment.ProcessPath is { } path &&
        (path.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase));

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>
    /// Makes the Run entry match the setting, pointing at this executable.
    /// Returns why nothing was changed, or null when the entry now matches.
    /// </summary>
    public static string? Apply(bool enabled)
    {
        if (IsDevelopmentBuild) return "Development builds do not register to start with Windows.";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var command = $"\"{Environment.ProcessPath}\" {Program.BackgroundSwitch}";
                if (key.GetValue(ValueName) as string != command) key.SetValue(ValueName, command);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Some managed PCs block the Run key by policy; recording works either way.
            return $"Start with Windows could not be changed: {ex.Message}";
        }
    }
}
