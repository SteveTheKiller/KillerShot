using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace KillerShot.Services;

/// <summary>
/// Make PDF: hands a folder's pictures to KillerPDF, which opens them as one new document. Only
/// offered when KillerPDF is installed; its installer writes Software\KillerPDF InstallPath.
/// </summary>
internal static class KillerPdfHandoff
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    /// <summary>Windows caps a command line near 32,767 characters; stay clear of it.</summary>
    internal const int MaxCommandLine = 32000;

    /// <summary>Test seam: replaces the registry lookup.</summary>
    internal static Func<string?>? FindOverride;

    /// <summary>Test seam: replaces Process.Start.</summary>
    internal static Action<ProcessStartInfo>? StartOverride;

    /// <summary>Test seam for the decrypting export: called as ("before-load", file) once the
    /// album session is checked and before that picture is read, and as ("after-write", file)
    /// once the plaintext file is on disk.</summary>
    internal static Action<string, string>? ExportHook;

    /// <summary>The KillerPDF exe, per user first, or null when it is not installed.</summary>
    internal static string? FindKillerPdf()
    {
        if (FindOverride != null) return FindOverride();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"Software\KillerPDF");
                if (key?.GetValue("InstallPath") is string path && path.Length > 0 && File.Exists(path)) return path;
            }
            catch (Exception) { }
        }
        return null;
    }

    internal static bool IsInstalled => FindKillerPdf() != null;

    /// <summary>The folder's own pictures, not its subfolders', in Explorer's name order so the PDF's
    /// pages match what the user sees. Enumeration order alone is alphabetical on NTFS ("page 10"
    /// before "page 2") and creation order on FAT and exFAT cards.</summary>
    internal static List<string> ImagesIn(string folder)
    {
        try
        {
            var images = Directory.EnumerateFiles(folder)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();
            images.Sort((a, b) => StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b)));
            return images;
        }
        catch (Exception) { return []; }
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    internal enum Result { Started, NotInstalled, NoImages, TooMany, Failed, AlbumChanged }

    private static readonly object CopiesGate = new();
    private static readonly List<(long Session, string Folder)> Copies = [];

    /// <summary>Make PDF on a library collection. The library is encrypted, so the collection's
    /// pictures, as edited, are decrypted into a folder beside the recordings' playback copies,
    /// numbered oldest first so KillerPDF's name order is capture order. The folder goes when the
    /// album closes, switches or changes password, and with every playback copy at exit.</summary>
    internal static async Task<Result> MakePdfFromCollectionAsync(long collectionId, long session)
    {
        if (FindKillerPdf() == null) return Result.NotInstalled;
        string? folder;
        try { folder = await Task.Run(() => DecryptCollection(collectionId, session)); }
        catch (InvalidOperationException) { return Result.AlbumChanged; }
        // Retirement can delete the folder under a running export, so a write that finds it gone
        // is the album changing, not a crash.
        catch (Exception) { return CaptureLibrary.Session != session ? Result.AlbumChanged : Result.Failed; }
        if (folder == null) return Result.NoImages;
        if (CaptureLibrary.Session != session) { Discard(folder); return Result.AlbumChanged; }
        var result = MakePdf(folder);
        if (result != Result.Started) Discard(folder);
        return result;
    }

    private static string? DecryptCollection(long collectionId, long session)
    {
        if (CaptureLibrary.Session != session) throw new InvalidOperationException();
        var items = CaptureLibrary.Items().Where(i => i.CollectionId == collectionId && !VideoCaptures.IsVideo(i))
            .OrderBy(i => i.Taken).ThenBy(i => i.Id).ToList();
        if (items.Count == 0) return null;
        string folder = Path.Combine(VideoCaptures.PlaybackFolder, "PDF " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        lock (CopiesGate) Copies.Add((session, folder));
        try
        {
            int width = Math.Max(4, items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
            for (int i = 0; i < items.Count; i++)
            {
                if (CaptureLibrary.Session != session) throw new InvalidOperationException();
                string file = Path.Combine(folder, (i + 1).ToString("D" + width, System.Globalization.CultureInfo.InvariantCulture) + ".png");
                ExportHook?.Invoke("before-load", file);
                File.WriteAllBytes(file, CaptureLibrary.LoadOutputPng(items[i]));
                ExportHook?.Invoke("after-write", file);
            }
            return folder;
        }
        catch (Exception)
        {
            // A retired album, a failed read or a failed write leaves no half-made plaintext behind.
            Discard(folder);
            throw;
        }
    }

    /// <summary>Deletes the decrypted Make PDF folders of one album session.</summary>
    internal static void ForgetCopies(long session)
    {
        List<string> gone;
        lock (CopiesGate)
        {
            gone = Copies.Where(c => c.Session == session).Select(c => c.Folder).ToList();
            Copies.RemoveAll(c => c.Session == session);
        }
        foreach (string folder in gone) DeleteOrRetry(folder);
    }

    /// <summary>Drops one export folder from tracking and deletes it, retrying if it is held.</summary>
    private static void Discard(string folder)
    {
        lock (CopiesGate) Copies.RemoveAll(c => string.Equals(c.Folder, folder, StringComparison.OrdinalIgnoreCase));
        DeleteOrRetry(folder);
    }

    /// <summary>Deletes every decrypted Make PDF folder, including ones left by an earlier run. A
    /// folder whose pictures are still held open goes on the retry timer.</summary>
    internal static void WipeCopies()
    {
        lock (CopiesGate)
        {
            Copies.Clear();
            PendingFolders.Clear();
            StopRetry();
        }
        try
        {
            if (Directory.Exists(VideoCaptures.PlaybackFolder))
                foreach (string folder in Directory.EnumerateDirectories(VideoCaptures.PlaybackFolder, "PDF *").ToList()) DeleteOrRetry(folder);
        }
        catch (Exception) { }
    }

    // Folders whose delete failed because a consumer still held a picture. They are no longer
    // handed to anyone; the timer keeps trying until each is gone or the next wipe takes them.
    private static readonly HashSet<string> PendingFolders = new(StringComparer.OrdinalIgnoreCase);
    private static Timer? _retry;

    private static void DeleteOrRetry(string folder)
    {
        if (TryDeleteFolder(folder)) return;
        lock (CopiesGate)
        {
            PendingFolders.Add(folder);
            _retry ??= new Timer(_ => RetryPending(), null, VideoCaptures.PlaybackRetryInterval, VideoCaptures.PlaybackRetryInterval);
        }
    }

    private static void RetryPending()
    {
        lock (CopiesGate)
        {
            PendingFolders.RemoveWhere(TryDeleteFolder);
            if (PendingFolders.Count == 0) StopRetry();
        }
    }

    private static void StopRetry()
    {
        _retry?.Dispose();
        _retry = null;
    }

    /// <summary>Only a Make PDF folder directly inside the playback folder is ours to delete.
    /// True once the folder is gone.</summary>
    private static bool TryDeleteFolder(string folder)
    {
        try
        {
            string parent = Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(folder)) ?? "");
            if (!string.Equals(parent, Path.GetFullPath(VideoCaptures.PlaybackFolder), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(folder).StartsWith("PDF ", StringComparison.Ordinal)) return true;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        catch (Exception) { }
        return !Directory.Exists(folder);
    }

    /// <summary>The string key that explains a failed handoff, or null when it started.</summary>
    internal static string? MessageKey(Result result) => result switch
    {
        Result.Started => null,
        Result.NotInstalled => "Str_MakePdf_NotInstalled",
        Result.NoImages => "Str_MakePdf_NoImages",
        Result.TooMany => "Str_MakePdf_TooMany",
        Result.AlbumChanged => "Str_Album_Changed",
        _ => "Str_MakePdf_Failed",
    };

    /// <summary>Starts KillerPDF on the folder's pictures. The working directory is the folder and
    /// the arguments are bare names, so a large folder still fits the command line.</summary>
    internal static Result MakePdf(string folder)
    {
        string? exe = FindKillerPdf();
        if (exe == null) return Result.NotInstalled;
        var images = ImagesIn(folder);
        if (images.Count == 0) return Result.NoImages;
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = folder };
        int length = exe.Length + 3;
        foreach (var image in images)
        {
            string name = Path.GetFileName(image);
            length += name.Length + 3;
            if (length > MaxCommandLine) return Result.TooMany;
            info.ArgumentList.Add(name);
        }
        try
        {
            if (StartOverride != null) StartOverride(info);
            else Process.Start(info)?.Dispose();
            return Result.Started;
        }
        catch (Exception) { return Result.Failed; }
    }
}
