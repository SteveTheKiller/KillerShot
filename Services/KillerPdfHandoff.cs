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

    internal enum Result { Started, NotInstalled, NoImages, TooMany, Failed }

    /// <summary>The string key that explains a failed handoff, or null when it started.</summary>
    internal static string? MessageKey(Result result) => result switch
    {
        Result.Started => null,
        Result.NotInstalled => "Str_MakePdf_NotInstalled",
        Result.NoImages => "Str_MakePdf_NoImages",
        Result.TooMany => "Str_MakePdf_TooMany",
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
