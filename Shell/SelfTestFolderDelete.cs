using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static partial class SelfTestLibrary
{
    /// <summary>A drag through a large folder overflows the thumbnail queue; the pictures it lands
    /// on still load, and the ones it dropped load when asked again. Delete on a folder picture
    /// sends the file to the Recycle Bin and the collection follows.</summary>
    private static async Task RunFolderDelete(string folder)
    {
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
        FolderCaptures.Enabled = false;
        var report = new List<string>();
        int exit = 1;
        try
        {
            string root = Path.Combine(folder, "photos");
            Directory.CreateDirectory(root);
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            byte[] png = ImageOutput.EncodePng(image);
            for (int n = 0; n < 600; n++) File.WriteAllBytes(Path.Combine(root, $"photo-{n:D3}.png"), png);
            string source = FolderCaptures.AddFolder(root);
            await FolderCaptures.RefreshFolderAsync(source);
            var items = FolderCaptures.LinkedItems(source).ToArray();
            if (items.Length != 600) throw new InvalidOperationException($"Folder fixture loaded {items.Length} of 600 images.");

            var requests = items.Select(CaptureLibrary.LoadThumbAsync).ToArray();
            var timer = Stopwatch.StartNew();
            var landed = await requests[^1].WaitAsync(TimeSpan.FromSeconds(10));
            report.Add($"landed_thumbnail_after_600_requests_ms={timer.ElapsedMilliseconds}");
            if (landed == null) throw new InvalidOperationException("The newest thumbnail request did not load after the queue overflowed.");
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(30));
            var passed = await CaptureLibrary.LoadThumbAsync(items[0]).WaitAsync(TimeSpan.FromSeconds(10));
            if (passed == null) throw new InvalidOperationException("A dropped thumbnail request did not load when asked again.");
            report.Add("thumbnail_overflow_serves_newest_and_retries_dropped=PASS");

            var victim = items[^1];
            string path = FolderCaptures.PathOf(victim.Id)!;
            FolderCaptures.Recycle(victim.Id);
            if (File.Exists(path)) throw new InvalidOperationException("Delete left the folder file in place.");
            await FolderCaptures.RefreshFolderAsync(source);
            if (FolderCaptures.LinkedItems(source).Any(item => item.Id == victim.Id))
                throw new InvalidOperationException("The collection still shows the deleted file.");
            if (Directory.EnumerateFiles(root, "*.png").Count() != 599)
                throw new InvalidOperationException("Delete removed more than the one file.");
            report.Add("folder_delete_recycles_one_file_and_collection_follows=PASS");

            // A folder with no Recycle Bin (a network share, a removable drive) must never be
            // treated as recyclable: Windows would delete the file for good without a word.
            Check(!FolderCaptures.CanRecycle(@"\\server\share\photo.png"), "A network share must not count as recyclable.");
            Check(FolderCaptures.CanRecycle(Path.Combine(root, "photo-000.png")), "A folder on a local fixed drive must count as recyclable.");
            var doomed = items[^2];
            string doomedPath = FolderCaptures.PathOf(doomed.Id)!;
            await FolderCaptures.DeleteForGoodAsync(doomed.Id);
            if (File.Exists(doomedPath)) throw new InvalidOperationException("Delete for good left the file in place.");
            await FolderCaptures.RefreshFolderAsync(source);
            if (FolderCaptures.LinkedItems(source).Any(item => item.Id == doomed.Id))
                throw new InvalidOperationException("The collection still shows the file deleted for good.");
            report.Add("no_recycle_bin_is_detected_and_delete_for_good_follows=PASS");
            FolderCaptures.RemoveFolder(source);
            exit = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "folder-delete-report.txt"), report);
        App.Instance.Shutdown(exit);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
