using System;
using Microsoft.Win32;
using Nexus.Core.Diagnostics;

namespace Nexus.Platform.Services;

/// <summary>Per-user autostart through HKCU\...\Run. No admin rights, no scheduled tasks.</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NEXUS";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string s && s.Contains(Environment.ProcessPath ?? "\u0000", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled && Environment.ProcessPath is { } exe) key.SetValue(ValueName, $"\"{exe}\" --background");
            else key.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { Log.Error("Startup", "Could not update autostart", ex); }
    }
}
