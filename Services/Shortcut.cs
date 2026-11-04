using KillerShot.Native;

namespace KillerShot.Services;

/// <summary>A global shortcut parsed from text such as "Ctrl+Print Screen" or "Ctrl+Shift+S".</summary>
internal readonly record struct Shortcut(uint Modifiers, uint Key, string Text)
{
    public static bool TryParse(string? text, out Shortcut shortcut)
    {
        shortcut = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        uint mods = 0;
        uint key = 0;
        foreach (var raw in text.Split('+'))
        {
            string part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Win32.MOD_CONTROL; continue;
                case "alt": mods |= Win32.MOD_ALT; continue;
                case "shift": mods |= Win32.MOD_SHIFT; continue;
                case "win": case "windows": mods |= 0x0008; continue;
            }
            if (key != 0) return false;
            key = KeyFromName(part);
            if (key == 0) return false;
        }
        if (key == 0) return false;
        shortcut = new Shortcut(mods, key, text.Trim());
        return true;
    }

    private static uint KeyFromName(string name)
    {
        string n = name.Replace(" ", "").ToLowerInvariant();
        if (n is "printscreen" or "prtscn" or "prtsc" or "snapshot") return Win32.VK_SNAPSHOT;
        if (n.Length == 1 && n[0] is >= 'a' and <= 'z') return (uint)char.ToUpperInvariant(n[0]);
        if (n.Length == 1 && n[0] is >= '0' and <= '9') return n[0];
        if (n.Length is 2 or 3 && n[0] == 'f' && int.TryParse(n.AsSpan(1), out int f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        return 0;
    }

    /// <summary>Chords Windows owns: Alt+F4, Alt+Esc, Alt+Tab, Ctrl+Esc, Ctrl+Alt+Delete,
    /// Ctrl+Shift+Esc, every Win chord, and a lone key other than an F key or Print Screen.
    /// A global shortcut on one of them would take it from every app on the desktop.</summary>
    public static bool IsReserved(Shortcut shortcut) => IsReserved(shortcut.Key, shortcut.Modifiers);

    public static bool IsReserved(uint key, uint modifiers)
    {
        const uint VkTab = 0x09, VkEscape = 0x1B, VkDelete = 0x2E, VkF4 = 0x73, ModWin = 0x0008;
        uint mods = modifiers & (Win32.MOD_ALT | Win32.MOD_CONTROL | Win32.MOD_SHIFT | ModWin);
        if ((mods & ModWin) != 0) return true;
        if (mods == 0) return !(key == Win32.VK_SNAPSHOT || key is >= 0x70 and <= 0x87);
        if (mods == Win32.MOD_ALT && key is VkF4 or VkEscape or VkTab) return true;
        if (mods == Win32.MOD_CONTROL && key == VkEscape) return true;
        if (mods == (Win32.MOD_CONTROL | Win32.MOD_SHIFT) && key == VkEscape) return true;
        return mods == (Win32.MOD_CONTROL | Win32.MOD_ALT) && key == VkDelete;
    }

    /// <summary>The short label used in menus and tooltips, for example "Alt+PrtScn".</summary>
    public string Short => Text.Replace("Print Screen", "PrtScn");
}
