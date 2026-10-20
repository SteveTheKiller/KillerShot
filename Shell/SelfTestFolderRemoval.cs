using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static partial class SelfTestLibrary
{
    private static async Task RunFolderRemoval(string folder)
    {
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
        FolderCaptures.Enabled = false;
        var report = new List<string>();
        int exit = 1;
        try
        {
            string root = Path.Combine(folder, "photos"), child = Path.Combine(root, "shared");
            Directory.CreateDirectory(child);
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            byte[] png = ImageOutput.EncodePng(image);
            for (int n = 0; n < 256; n++)
            {
                File.WriteAllBytes(Path.Combine(root, $"root-{n}.png"), png);
                File.WriteAllBytes(Path.Combine(child, $"child-{n}.png"), png);
            }
            string parentSource = FolderCaptures.AddFolder(root), childSource = FolderCaptures.AddFolder(child);
            await Task.WhenAll(FolderCaptures.RefreshFolderAsync(parentSource), FolderCaptures.RefreshFolderAsync(childSource));
            var shared = FolderCaptures.LinkedItems(childSource).ToArray();
            if (FolderCaptures.LinkedItems(parentSource).Count != 512 || shared.Length != 256)
                throw new InvalidOperationException("Overlapping folder fixture did not load all images.");
            foreach (string path in Directory.EnumerateFiles(root, "root-*.png")) File.Delete(path);
            var timer = Stopwatch.StartNew();
            await FolderCaptures.RefreshFolderAsync(parentSource);
            timer.Stop();
            report.Add($"remove_256_images_with_256_shared_images_ms={timer.ElapsedMilliseconds}");
            FolderCaptures.RemoveFolder(parentSource);
            if (shared.Any(item => FolderCaptures.PathOf(item.Id) == null))
                throw new InvalidOperationException("Removing an overlapping parent lost the remaining child's paths.");
            FolderCaptures.RemoveFolder(childSource);
            if (shared.Any(item => FolderCaptures.PathOf(item.Id) != null))
                throw new InvalidOperationException("Removed folder metadata remains reachable after its last collection is removed.");
            if (Directory.EnumerateFiles(child, "*.png").Count() != 256)
                throw new InvalidOperationException("Removing a collection changed source files.");
            report.Add("overlapping_folder_removal_preserves_shared_paths_and_reclaims_unreferenced_metadata=PASS");
            exit = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "folder-removal-report.txt"), report);
        App.Instance.Shutdown(exit);
    }
}
