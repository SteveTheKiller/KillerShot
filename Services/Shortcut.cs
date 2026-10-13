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

    /// <summary>The short label used in menus and tooltips, for example "Alt+PrtScn".</summary>
    public string Short => Text.Replace("Print Screen", "PrtScn");
}
