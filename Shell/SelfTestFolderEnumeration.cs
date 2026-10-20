using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestFolderEnumeration
{
    public static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-folder-enumeration", var folder]) return false;
        _ = RunAndExitAsync(Path.GetFullPath(folder));
        return true;
    }

    private static async Task RunAndExitAsync(string folder)
    {
        int code = 0;
        string report;
        try { await RunChecksAsync(folder); report = "PASS: controlled folder enumeration coverage."; }
        catch (Exception error) { code = 1; report = error.ToString(); }
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "folder-enumeration-report.txt"), report);
        App.Instance.Shutdown(code);
    }

    internal static async Task RunChecksAsync(string folder)
    {
        if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new enumeration test folder.");
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        string photos = Path.Combine(folder, "photos");
        string denied = Path.Combine(photos, "denied");
        string sibling = Path.Combine(photos, "accessible-sibling");
        Directory.CreateDirectory(denied);
        Directory.CreateDirectory(sibling);
        var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        pixel.Freeze();
        ImageOutput.SavePng(pixel, Path.Combine(photos, "root.png"));
        ImageOutput.SavePng(pixel, Path.Combine(sibling, "sibling.png"));
        ImageOutput.SavePng(pixel, Path.Combine(denied, "hidden.png"));
        var reader = new DenyingDirectoryReader(denied);
        string link = Path.Combine(photos, "controlled-reparse-directory");
        Directory.CreateDirectory(link);
        ImageOutput.SavePng(pixel, Path.Combine(link, "excluded.png"));
        reader.ReparseDirectories.Add(link);
        FolderCaptures.UseFolder(Path.Combine(folder, "Screenshots"), reader);
        FolderCaptures.Enabled = false;
        string source = FolderCaptures.AddFolder(photos);
        await FolderCaptures.RefreshFolderAsync(source).WaitAsync(TimeSpan.FromSeconds(3));
        var status = FolderCaptures.LinkedCollections().Single(item => item.Source == source);
        var items = FolderCaptures.LinkedItems(source);
        if (status.Available || status.Loading || items.Count != 2 || !items.Any(item => item.Window == "root") || !items.Any(item => item.Window == "sibling"))
            throw new InvalidOperationException("Linked folder denial must publish unavailable coverage and retain accessible siblings.");
        Console.WriteLine("PASS: linked folder denial preserves accessible siblings and reports unavailable coverage.");
        reader.DenyEnabled = false;
        await FolderCaptures.RefreshFolderAsync(source).WaitAsync(TimeSpan.FromSeconds(3));
        if (!FolderCaptures.LinkedCollections().Single(item => item.Source == source).Available || FolderCaptures.LinkedItems(source).Count != 3)
            throw new InvalidOperationException("A linked folder must recover available coverage after retry.");

        string removal = Path.Combine(folder, "removed-during-enumeration");
        Directory.CreateDirectory(removal);
        ImageOutput.SavePng(pixel, Path.Combine(removal, "removed.png"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int paused = 0;
        reader.BeforeRead = directory =>
        {
            if (directory.FullName != removal || Interlocked.Exchange(ref paused, 1) != 0) return;
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(3))) throw new IOException("Controlled enumeration pause exceeded its deadline.");
        };
        string removedSource = FolderCaptures.AddFolder(removal);
        var removedScan = FolderCaptures.RefreshFolderAsync(removedSource);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            FolderCaptures.RemoveFolder(removedSource);
        }
        finally { release.Set(); }
        await removedScan.WaitAsync(TimeSpan.FromSeconds(3));
        reader.BeforeRead = null;
        if (FolderCaptures.LinkedCollections().Any(item => item.Source == removedSource) || FolderCaptures.LinkedItems(removedSource).Count != 0)
            throw new InvalidOperationException("Removing a folder during enumeration must prevent its stale scan from republishing.");

        string screenshots = Path.Combine(folder, "Screenshots");
        Directory.CreateDirectory(screenshots);
        ImageOutput.SavePng(pixel, Path.Combine(screenshots, "screenshot.png"));
        FolderCaptures.UseFolder(screenshots, reader);
        FolderCaptures.Enabled = true;
        long screenshotId = FolderCaptures.Items().Single().Id;
        reader.Denied = screenshots;
        reader.DenyEnabled = true;
        if (FolderCaptures.Items().Count != 0 || FolderCaptures.StatusForSource("folder") is not { Available: false, Loading: false }
            || FolderCaptures.PathOf(screenshotId) == null)
            throw new InvalidOperationException("A Screenshots read failure must report unavailable instead of a normal empty folder and preserve known IDs.");
        reader.DenyEnabled = false;
        if (FolderCaptures.Items().Single().Id != screenshotId || FolderCaptures.StatusForSource("folder") is not { Available: true })
            throw new InvalidOperationException("Screenshots coverage must recover after retry without replacing the surviving image ID.");
        reader.FailAfterFirstFile = true;
        if (FolderCaptures.Items().Count != 1 || FolderCaptures.StatusForSource("folder") is not { Available: false })
            throw new InvalidOperationException("A partial Screenshots enumeration must preserve observed images and report unavailable coverage.");
        reader.FailAfterFirstFile = false;
        FolderCaptures.UseFolder(Path.Combine(folder, "missing-screenshots"), reader);
        if (FolderCaptures.Items().Count != 0 || FolderCaptures.StatusForSource("folder") is not { Available: true })
            throw new InvalidOperationException("A genuinely missing Screenshots folder must remain a normal empty collection.");
        Console.WriteLine("PASS: public folder snapshots retain partial accessible results, unavailable status, retry recovery, reparse exclusions, removal guards and missing Screenshots empty behavior. No OS access rules were changed.");
    }

    private sealed class DenyingDirectoryReader(string denied) : IFolderDirectoryReader
    {
        internal string Denied = denied;
        internal bool DenyEnabled = true, FailAfterFirstFile;
        internal readonly HashSet<string> ReparseDirectories = new(StringComparer.OrdinalIgnoreCase);
        internal Action<DirectoryInfo>? BeforeRead;
        public IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo directory, EnumerationOptions options)
        {
            BeforeRead?.Invoke(directory);
            if (DenyEnabled && string.Equals(directory.FullName, Denied, StringComparison.OrdinalIgnoreCase))
            {
                if (options.IgnoreInaccessible) yield break;
                throw new UnauthorizedAccessException("Controlled inaccessible image directory.");
            }
            foreach (var file in directory.EnumerateFiles("*", TopOnly(options)))
            {
                yield return file;
                if (FailAfterFirstFile) throw new IOException("Controlled enumeration failure after one image.");
            }
            if (options.RecurseSubdirectories)
                foreach (var child in EnumerateDirectories(directory, options))
                    foreach (var file in EnumerateFiles(child, options)) yield return file;
        }

        public IEnumerable<DirectoryInfo> EnumerateDirectories(DirectoryInfo directory, EnumerationOptions options)
        {
            BeforeRead?.Invoke(directory);
            if (DenyEnabled && string.Equals(directory.FullName, Denied, StringComparison.OrdinalIgnoreCase))
            {
                if (options.IgnoreInaccessible) return [];
                throw new UnauthorizedAccessException("Controlled inaccessible image directory.");
            }
            return directory.EnumerateDirectories("*", TopOnly(options)).Where(child =>
                (options.AttributesToSkip & FileAttributes.ReparsePoint) == 0 || !ReparseDirectories.Contains(child.FullName));
        }

        private static EnumerationOptions TopOnly(EnumerationOptions options) => new()
        {
            RecurseSubdirectories = false, IgnoreInaccessible = options.IgnoreInaccessible,
            AttributesToSkip = options.AttributesToSkip, ReturnSpecialDirectories = false
        };
    }
}
