using System.Text;

namespace KillerTools.Qr;

public static class QrPayload
{
    /// <summary>Extracts the Wi-Fi password for copying, without joining a network.</summary>
    public static string? WifiPassword(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase)) return null;
        var field = new StringBuilder();
        string? password = null;
        bool found = false;
        for (int i = 5; i <= text.Length; i++)
        {
            char value = i < text.Length ? text[i] : ';';
            if (value == '\\' && i + 1 < text.Length && "\\;,:\"".Contains(text[i + 1]))
            {
                field.Append(text[++i]);
                continue;
            }
            if (value != ';') { field.Append(value); continue; }
            string part = field.ToString();
            field.Clear();
            if (!part.StartsWith("P:", StringComparison.OrdinalIgnoreCase)) continue;
            if (found) return null;
            password = part[2..];
            found = true;
        }
        return password;
    }
}
