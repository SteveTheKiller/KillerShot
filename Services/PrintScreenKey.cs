using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// "Use KillerShot for Print Screen", the Capture settings checkbox.
//
// Windows 11 gives the Print Screen key to its own screen capture while
// HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled is 1 (Settings > Accessibility >
// Keyboard), and then no other app can register the key. Turning this on writes 0 there, as the
// Settings switch does, and tells Windows the setting changed, so KillerShot's own Print Screen
// shortcut can take the key.
//
// It is the user's own choice made in KillerShot, so it never happens on its own and is always
// undone the same way, as KillerShell's default terminal switch is: the value it replaced is saved
// first and put back on the way out (turning it off, or uninstalling), but only while the key is
// still free. A choice made later in Settings is never overwritten. Unlike KillerShell's, it works
// from a portable copy too, whose note asks for it to be turned off before the copy is deleted.
// Win+Shift+S is not part of it:
// Windows keeps that shortcut for itself.
namespace KillerShot.Services
{
    internal enum PrintScreenKeyState
    {
        /// <summary>Windows' screen capture has the Print Screen key.</summary>
        Off,

        /// <summary>The key is free for KillerShot's Print Screen shortcut.</summary>
        On,

        /// <summary>KillerShot freed the key, then something else (Settings, a policy) gave it back to Windows.</summary>
        ChangedElsewhere,
    }

    internal sealed class PrintScreenKey
    {
        internal const string KeyboardKey = @"Control Panel\Keyboard";
        internal const string ValueName = "PrintScreenKeyForSnippingEnabled";

        /// <summary>Where the replaced value waits to be put back: the number, or empty when there was none.</summary>
        internal const string PreviousSetting = "PrintScreenKeyPrevious";

        private readonly string _keyPath;
        private readonly bool _portable;

        /// <param name="portable">A portable copy can turn it on too, unlike KillerShell's default
        /// terminal: nothing here points at the exe, and the worst a deleted copy leaves behind is
        /// Print Screen free of Snipping Tool, which Settings turns back. Its note says to turn the
        /// switch off before deleting the copy.</param>
        /// <param name="keyPath">Self-tests use a scratch key in place of the real one.</param>
        internal PrintScreenKey(bool portable, string keyPath = KeyboardKey)
        {
            _portable = portable;
            _keyPath = keyPath;
        }

        /// <summary>The copy that runs: installed or portable, on the real Windows key.</summary>
        internal static PrintScreenKey Current => new(App.IsPortable());

        internal bool Portable => _portable;

        internal PrintScreenKeyState State
        {
            get
            {
                if (KeyIsFree()) return PrintScreenKeyState.On;
                return Previous() != null ? PrintScreenKeyState.ChangedElsewhere : PrintScreenKeyState.Off;
            }
        }

        /// <summary>Whether the checkbox takes a click. Always: installed and portable copies alike.</summary>
        internal bool CanChange => true;

        /// <summary>The note under the checkbox: what happened, what to do before deleting a portable
        /// copy, or what it does.</summary>
        internal string NoteKey =>
            State == PrintScreenKeyState.ChangedElsewhere ? "Str_CS_PrintScreenKeyChanged"
          : _portable ? "Str_CS_PrintScreenKeyPortable"
          : "Str_CS_PrintScreenKeyHint";

        /// <summary>Frees the Print Screen key from Windows' screen capture.</summary>
        internal bool TurnOn()
        {
            if (State == PrintScreenKeyState.On) return true;
            int? current = Read();
            // The key first, the saved value after: a refused write must leave no saved value, or
            // the switch would report that Windows took the key back (audit BF-007, 2026-10-27).
            Write(0);
            Settings.SetRequired(PreviousSetting, current?.ToString(CultureInfo.InvariantCulture) ?? "");
            return true;
        }

        /// <summary>Puts back the value TurnOn replaced, while the key is still free; a choice made
        /// since in Settings stays. Also what uninstall runs.</summary>
        internal void TurnOff()
        {
            string? previous = Previous();
            if (KeyIsFree())
            {
                // Nothing saved means KillerShot found the key already free; turning the box off is
                // then a request to hand it to Windows, which is what Settings' switch would write.
                if (previous == null || !int.TryParse(previous, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) value = 1;
                if (previous == "") Delete();
                else Write(value);
            }
            if (previous != null) Settings.SetRequired(PreviousSetting, null);
        }

        /// <summary>Uninstall: undoes only what KillerShot did. With nothing recorded the key was
        /// freed some other way, and it stays as the user left it.</summary>
        internal void Undo()
        {
            if (Previous() != null) TurnOff();
        }

        private bool KeyIsFree() => Read() == 0;

        private string? Previous() => Settings.Get(PreviousSetting);

        private int? Read()
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
            return key?.GetValue(ValueName) is int value ? value : null;
        }

        private void Write(int value)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true))
                key.SetValue(ValueName, value, RegistryValueKind.DWord);
            Announce();
        }

        private void Delete()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true))
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            Announce();
        }

        /// <summary>Tells running programs the keyboard setting changed, as Settings does, so
        /// Windows lets go of (or takes back) the key without a sign-out.</summary>
        private void Announce()
        {
            if (_keyPath != KeyboardKey) return;
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Keyboard", 0x0002, 2000, out _);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam,
            uint flags, uint timeout, out IntPtr result);
    }
}
