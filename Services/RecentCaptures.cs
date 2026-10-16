using System.IO;
using System.Windows.Media.Imaging;

namespace KillerShot.Services;

/// <summary>The last captures, kept as PNG in %LOCALAPPDATA%\KillerShot\Recent so a capture that
/// was overwritten on the clipboard can still be reopened. Oldest files beyond the limit are
/// removed after each capture.</summary>
internal static class RecentCaptures
{
    public const int Limit = 20;

    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerShot", "Recent");

    /// <summary>Writes the capture in the background and returns the path it will have.</summary>
    public static string Add(BitmapSource bmp, DateTime taken)
    {
        if (!bmp.IsFrozen) bmp.Freeze();
        string path = PathFor(taken);
        Task.Run(() =>
        {
            try { Write(bmp, path); }
            catch { /* history is a convenience; the clipboard already has the capture */ }
        });
        return path;
    }

    /// <summary>Writes the capture before returning, for the command line, whose process can end
    /// before a background write finishes. Throws when the history folder cannot be written.</summary>
    public static string AddNow(BitmapSource bmp, DateTime taken)
    {
        string path = PathFor(taken);
        Write(bmp, path);
        return path;
    }

    private static string PathFor(DateTime taken) => Path.Combine(Folder, $"{taken:yyyyMMdd-HHmmss-fff}.png");

    private static void Write(BitmapSource bmp, string path)
    {
        Directory.CreateDirectory(Folder);
        ImageOutput.SavePng(bmp, path);
        Prune();
    }

    public static IReadOnlyList<FileInfo> List()
    {
        try
        {
            var dir = new DirectoryInfo(Folder);
            if (!dir.Exists) return [];
            return dir.GetFiles("*.png").OrderByDescending(f => f.Name, StringComparer.Ordinal).Take(Limit).ToList();
        }
        catch { return []; }
    }

    private static void Prune()
    {
        var files = new DirectoryInfo(Folder).GetFiles("*.png").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(Limit);
        foreach (var f in files)
        {
            try { f.Delete(); } catch { }
        }
    }

    /// <summary>The capture time encoded in a history file name.</summary>
    public static DateTime? TakenFrom(FileInfo f) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f.Name), "yyyyMMdd-HHmmss-fff",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : null;
}
