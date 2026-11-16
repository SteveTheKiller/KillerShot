using System.Globalization;
using System.Windows.Media;

namespace KillerShot.Services;

/// <summary>The Redact settings dialog's choices: what auto redact looks for, how every redaction
/// box looks, and whether new captures are scanned when they open.</summary>
internal static class RedactSettings
{
    public const int MaxFeather = 24;

    public static RedactKinds Kinds
    {
        get => int.TryParse(Settings.Get("RedactKinds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? (RedactKinds)v & RedactKinds.All : RedactKinds.All;
        set => Settings.Set("RedactKinds", ((int)(value & RedactKinds.All)).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The box color, always opaque. Black unless one was chosen.</summary>
    public static Color Fill
    {
        get
        {
            string? hex = Settings.Get("RedactColor");
            if (hex is { Length: 7 } && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
                return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return Colors.Black;
        }
        set => Settings.Set("RedactColor", $"#{value.R:X2}{value.G:X2}{value.B:X2}");
    }

    /// <summary>Pixels of soft edge outside each box, 0 for a hard edge.</summary>
    public static int Feather
    {
        get => int.TryParse(Settings.Get("RedactFeather"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? Math.Clamp(v, 0, MaxFeather) : 0;
        set => Settings.Set("RedactFeather", Math.Clamp(value, 0, MaxFeather).ToString(CultureInfo.InvariantCulture));
    }

    public static bool ScanEveryCapture
    {
        get => Settings.GetBool("RedactEveryCapture", false);
        set => Settings.SetBool("RedactEveryCapture", value);
    }
}
