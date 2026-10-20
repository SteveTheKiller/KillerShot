using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerFamily.Layout;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static partial class SelfTestLibrary
{
    private static async Task RunFolderFixes(string folder)
    {
        int code = 1;
        var report = new List<string>();
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new folder-fix test directory.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            FolderCaptures.UseFolder(Path.Combine(folder, "Screenshots"));
            FolderCaptures.Enabled = false;
            App.InitializeKit();
            string photos = Path.Combine(folder, "photos");
            Directory.CreateDirectory(photos);
            var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            pixel.Freeze();
            string first = Path.Combine(photos, "first.png");
            ImageOutput.SavePng(pixel, first);
            string source = FolderCaptures.AddFolder(photos);
            await FolderCaptures.RefreshFolderAsync(source);
            using var strip = new CollectionStrip(new DockHost(), new Style(typeof(Slider)));
            strip.SetSource(source);
            string? failureMessage = null;
            strip.StatusMessage += message => failureMessage = message;
            string settings = Path.Combine(folder, "settings", "settings.json");
            var attributes = File.GetAttributes(settings);
            try
            {
                File.SetAttributes(settings, attributes | FileAttributes.ReadOnly);
                var remove = strip.SourceMenu(new Border()).Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, LocaleManager.Loc("Str_FolderCollection_Remove")));
                remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                if (string.IsNullOrWhiteSpace(failureMessage) || strip.Source != source
                    || !FolderCaptures.LinkedCollections().Any(item => item.Source == source))
                    throw new InvalidOperationException("Failed removal must retain the selected, registered collection.");
            }
            finally { File.SetAttributes(settings, attributes); }
            report.Add("remove_command_handles_read_only_settings_and_preserves_selection=PASS");
            strip.SourceMenu(new Border()).Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, LocaleManager.Loc("Str_FolderCollection_Remove")))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (strip.Source != "all" || FolderCaptures.LinkedCollections().Any(item => item.Source == source))
                throw new InvalidOperationException("A successful removal must reset selection and remove only the saved link.");
            report.Add("remove_command_succeeds_after_settings_recover=PASS");
            source = FolderCaptures.AddFolder(photos);
            await FolderCaptures.RefreshFolderAsync(source);
            strip.SetSource(source);
            await App.Instance.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            ImageOutput.SavePng(pixel, Path.Combine(photos, "second.png"));
            await FolderCaptures.RefreshFolderAsync(source);
            await App.Instance.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var carousel = (KillerShot.Controls.CoverFlow)strip.TestCarousel;
            if (carousel.Items.Count != 2 || strip.Source != source)
                throw new InvalidOperationException("The same linked editor source must refresh when its snapshot changes.");
            var selected = FolderCaptures.LinkedItems(source).Single(item => item.Window == "second");
            strip.Follow(selected, null);
            File.Delete(first);
            await FolderCaptures.RefreshFolderAsync(source);
            await App.Instance.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (carousel.Items.Count != 1 || carousel.SelectedItem?.Id != selected.Id)
                throw new InvalidOperationException("A linked editor refresh must remove deleted files and preserve the surviving selected photo.");
            report.Add("same_source_editor_refreshes_added_removed_files_and_retains_selection=PASS");
            var pixels = new byte[6000 * 4000 * 4];
            Array.Fill(pixels, (byte)255);
            var large = BitmapSource.Create(6000, 4000, 96, 96, PixelFormats.Bgra32, null, pixels, 6000 * 4);
            var jpeg = new JpegBitmapEncoder { QualityLevel = 80 };
            jpeg.Frames.Add(BitmapFrame.Create(large));
            using (var output = File.Create(Path.Combine(photos, "large.jpg"))) jpeg.Save(output);
            await FolderCaptures.RefreshFolderAsync(source);
            var largeItem = FolderCaptures.LinkedItems(source).Single(item => item.Window == "large");
            long before = GC.GetTotalAllocatedBytes(true);
            var preview = CaptureLibrary.LoadPreview(largeItem);
            long allocated = GC.GetTotalAllocatedBytes(true) - before;
            report.Add($"large_preview_managed_allocation_bytes={allocated}");
            if (preview.PixelWidth != 2048 || preview.PixelHeight != 1365 || !preview.IsFrozen
                || allocated >= 64 * 1024 * 1024)
                throw new InvalidOperationException("The linked JPEG preview must decode at bounded size without full-image conversion.");
            report.Add("large_linked_preview_has_bounded_decode=PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "folder-fixes-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }
}
