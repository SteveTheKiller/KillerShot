using Microsoft.Win32;

namespace KillerShot.Services;

/// <summary>Start with Windows through the per-user Run key, in tray mode so no window opens.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "KillerShot";

    public static bool Enabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(Name) is string;
            }
            catch { return false; }
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) return false;
                key.SetValue(Name, $"\"{exe}\" --tray");
            }
            else
            {
                key.DeleteValue(Name, false);
            }
            return true;
        }
        catch { return false; }
    }
}
