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

    /// <summary>Reserves a path for a recording. The caller removes the empty file on failure.
    /// A name already taken gets " (2)", " (3)" and so on.</summary>
    public static string NextPath(string folder, string fileName)
    {
        Directory.CreateDirectory(folder);
        for (int n = 1; n < int.MaxValue; n++)
        {
            string path = CandidatePath(folder, fileName, n);
            try
            {
                using var reservation = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return path;
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
        throw new IOException("No available output file name was found.");
    }

    /// <summary>Writes to a private file first, then publishes a complete file at the target.</summary>
    public static void WriteAtomic(string path, bool overwrite, Action<Stream> write)
    {
        string full = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(full)!;
        string temp = NewTempPath(folder);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(true);
            }
            File.Move(temp, full, overwrite);
        }
        finally { TryDelete(temp); }
    }

    /// <summary>Publishes a complete file under a unique name across processes.</summary>
    public static string WriteUnique(string folder, string fileName, Action<Stream> write)
    {
        Directory.CreateDirectory(folder);
        string temp = NewTempPath(folder);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(true);
            }
            return PublishUnique(temp, folder, fileName);
        }
        finally { TryDelete(temp); }
    }

    /// <summary>Publishes a completed recording from the same volume without replacing an
    /// existing file. A failed move leaves the staging file for the caller to clean up.</summary>
    public static string PublishUnique(string stagedPath, string folder, string fileName)
    {
        string stagedRoot = Path.GetPathRoot(Path.GetFullPath(stagedPath))!;
        string targetRoot = Path.GetPathRoot(Path.GetFullPath(folder))!;
        if (!string.Equals(stagedRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The recording staging file must be on its output volume.");
        for (int n = 1; n < int.MaxValue; n++)
        {
            string path = CandidatePath(folder, fileName, n);
            try { File.Move(stagedPath, path); return path; }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
        throw new IOException("No available output file name was found.");
    }

    private static string CandidatePath(string folder, string fileName, int n) => n == 1
        ? Path.Combine(folder, fileName)
        : Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(fileName)} ({n}){Path.GetExtension(fileName)}");

    private static string NewTempPath(string folder) => Path.Combine(folder, $".KillerShot-{Guid.NewGuid():N}.tmp");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
