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

    /// <summary>The folder's own pictures, not its subfolders'.</summary>
    internal static List<string> ImagesIn(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception) { return []; }
    }

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
        if (folder == null) return Result.NoImages;
        if (CaptureLibrary.Session != session) { DeleteFolder(folder); return Result.AlbumChanged; }
        return MakePdf(folder);
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
        int width = Math.Max(4, items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        for (int i = 0; i < items.Count; i++)
        {
            if (CaptureLibrary.Session != session) throw new InvalidOperationException();
            File.WriteAllBytes(Path.Combine(folder, (i + 1).ToString("D" + width, System.Globalization.CultureInfo.InvariantCulture) + ".png"),
                CaptureLibrary.LoadOutputPng(items[i].Id));
        }
        return folder;
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
        foreach (string folder in gone) DeleteFolder(folder);
    }

    /// <summary>Deletes every decrypted Make PDF folder, including ones left by an earlier run.</summary>
    internal static void WipeCopies()
    {
        lock (CopiesGate) Copies.Clear();
        try
        {
            if (Directory.Exists(VideoCaptures.PlaybackFolder))
                foreach (string folder in Directory.EnumerateDirectories(VideoCaptures.PlaybackFolder, "PDF *")) DeleteFolder(folder);
        }
        catch (Exception) { }
    }

    private static void DeleteFolder(string folder)
    {
        try { Directory.Delete(folder, true); } catch (Exception) { }
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
