using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using KillerShot.Capture;

namespace KillerShot.Services;

/// <summary>Where a capture is saved without a dialog, and what it is called.</summary>
internal static class OutputFiles
{
    private static readonly Guid ScreenshotsFolderId = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>Windows' own Screenshots folder (Pictures\Screenshots, or wherever the user moved
    /// it), the same place Win+Print Screen saves to.</summary>
    public static string DefaultFolder
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(ScreenshotsFolderId, 0, IntPtr.Zero, out IntPtr p) == 0)
                {
                    try { if (Marshal.PtrToStringUni(p) is { Length: > 0 } path) return path; }
                    finally { Marshal.FreeCoTaskMem(p); }
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        }
    }

    public const string Placeholders = "{yyyy} {MM} {dd} {HH} {mm} {ss} {mode} {width} {height}";

    /// <summary>Expands the pattern and strips characters Windows does not allow in a file name.</summary>
    public static string FileName(string pattern, DateTime when, CaptureMode mode, int width, int height, string ext)
    {
        var name = pattern
            .Replace("{yyyy}", when.ToString("yyyy")).Replace("{MM}", when.ToString("MM")).Replace("{dd}", when.ToString("dd"))
            .Replace("{HH}", when.ToString("HH")).Replace("{mm}", when.ToString("mm")).Replace("{ss}", when.ToString("ss"))
            .Replace("{mode}", mode.ToString().ToLowerInvariant())
            .Replace("{width}", width.ToString()).Replace("{height}", height.ToString());
        var sb = new StringBuilder();
        var bad = Path.GetInvalidFileNameChars();
        foreach (char c in name) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
        name = sb.ToString().Trim().TrimEnd('.');
        if (name.Length == 0) name = "KillerShot";
        return name + "." + ext;
    }

    /// <summary>A free path in the folder. A name already taken gets " (2)", " (3)" and so on,
    /// so a capture never overwrites an earlier one.</summary>
    public static string NextPath(string folder, string fileName)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem} ({n}){ext}");
        return path;
    }
}
