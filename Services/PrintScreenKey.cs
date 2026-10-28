using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// "Use KillerShot for the Print Screen key", in About and the tray menu. While it is on and
// KillerShot runs, Native/PrintScreenHook.cs takes the Print Screen presses KillerShot has
// shortcuts for before Windows hands them to Snipping Tool. Closing KillerShot gives the key back
// at once, and Windows' own setting is never changed.
//
// The first version of the switch turned off Windows' setting
// (HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled) instead, saving the value it
// replaced. Windows did not reliably pick that up until a sign-out, and Print Screen went dead
// whenever KillerShot was not running. RestoreLegacy puts back what that version changed, once, at
// startup and on uninstall.
namespace KillerShot.Services
{
    internal static class PrintScreenKey
    {
        internal const string KeyboardKey = @"Control Panel\Keyboard";
        internal const string ValueName = "PrintScreenKeyForSnippingEnabled";
        internal const string SettingName = "PrintScreenKeyHook";

        /// <summary>Where the first version kept the replaced value: the number, or empty for none.</summary>
        internal const string LegacyPreviousSetting = "PrintScreenKeyPrevious";

        /// <summary>The switch. The tray reads it each time it claims the shortcuts.</summary>
        internal static bool Enabled
        {
            get => Settings.GetBool(SettingName, false);
            set => Settings.SetRequired(SettingName, value ? "1" : "0");
        }

        /// <summary>
        /// Undoes the first version's registry change, if it made one: the saved value goes back
        /// while the key is still the 0 it wrote (a choice made since in Settings stays), the saved
        /// value is forgotten, and the switch stays on in its new form. Nothing recorded means
        /// nothing to undo.
        /// </summary>
        /// <param name="keyPath">Self-tests use a scratch key in place of the real one.</param>
        internal static void RestoreLegacy(string keyPath = KeyboardKey)
        {
            string? previous = Settings.Get(LegacyPreviousSetting);
            if (previous == null) return;
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true))
            {
                if (key.GetValue(ValueName) is int current && current == 0)
                {
                    if (previous == "") key.DeleteValue(ValueName, throwOnMissingValue: false);
                    else key.SetValue(ValueName, int.TryParse(previous, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 1, RegistryValueKind.DWord);
                    if (keyPath == KeyboardKey) Announce();
                }
            }
            Settings.SetRequired(LegacyPreviousSetting, null);
            Enabled = true;
        }

        /// <summary>Tells running programs the keyboard setting changed, as Settings does.</summary>
        private static void Announce() =>
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Keyboard", 0x0002, 2000, out _);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam,
            uint flags, uint timeout, out IntPtr result);
    }
}
