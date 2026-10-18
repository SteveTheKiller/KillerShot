using System.IO;

namespace KillerShot.Services;

/// <summary>Where KillerShot kept its last captures as plain PNG files before the encrypted
/// library. CaptureLibrary imports anything still there and removes the files.</summary>
internal static class RecentCaptures
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerShot", "Recent");

    /// <summary>The capture time encoded in a history file name.</summary>
    public static DateTime? TakenFrom(FileInfo f) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f.Name), "yyyyMMdd-HHmmss-fff",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : null;
}
