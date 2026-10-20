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
            string another = Path.Combine(folder, "another");
            Directory.CreateDirectory(another);
            await Task.Run(async () =>
            {
                Task<string> adding;
                var snapshot = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (var held = new FileStream(settings + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    adding = Task.Run(() => FolderCaptures.AddFolder(another));
                    await Task.Delay(150).ConfigureAwait(false);
                    using var started = new ManualResetEventSlim();
                    new Thread(() =>
                    {
                        started.Set();
                        try { snapshot.SetResult(FolderCaptures.LinkedCollections().Count == 1 && FolderCaptures.LinkedItems(source).Count == 2); }
                        catch (Exception error) { snapshot.SetException(error); }
                    }) { IsBackground = true }.Start();
                    started.Wait();
                    await Task.WhenAny(snapshot.Task, Task.Delay(350)).ConfigureAwait(false);
                    report.Add($"snapshots_completed_during_settings_lock={snapshot.Task.IsCompleted}");
                }
                string added = await adding;
                if (!await snapshot.Task || !report.Contains("snapshots_completed_during_settings_lock=True")
                    || !FolderCaptures.LinkedCollections().Any(item => item.Source == added))
                    throw new InvalidOperationException("Published folder snapshots must remain readable while a new link waits for settings persistence.");
                report.Add("folder_snapshots_do_not_wait_for_settings_persistence=PASS");
            });
            string retryFolder = Path.Combine(folder, "retry-locked-image");
            Directory.CreateDirectory(retryFolder);
            string retryImage = Path.Combine(retryFolder, "locked.png");
            ImageOutput.SavePng(pixel, retryImage);
            string retrySource;
            using (var held = new FileStream(retryImage, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                retrySource = FolderCaptures.AddFolder(retryFolder);
                await FolderCaptures.RefreshFolderAsync(retrySource);
                if (FolderCaptures.LinkedItems(retrySource).Count != 0)
                    throw new InvalidOperationException("The locked-image fixture unexpectedly loaded.");
            }
            await FolderCaptures.RefreshFolderAsync(retrySource);
            if (FolderCaptures.LinkedItems(retrySource).Count != 1)
                throw new InvalidOperationException("A transient dimension-read failure remains cached after the image is unlocked.");
            report.Add("locked_image_dimension_failure_retries_on_refresh=PASS");
            foreach (var shape in new[] { (64, 8192, 4, 512), (8192, 64, 512, 4), (8, 32, 8, 32), (32, 8, 32, 8) })
            {
                string name = $"shape-{shape.Item1}-{shape.Item2}";
                var shapePixels = new byte[shape.Item1 * shape.Item2 * 4];
                Array.Fill(shapePixels, (byte)255);
                var shapeImage = BitmapSource.Create(shape.Item1, shape.Item2, 96, 96, PixelFormats.Bgra32,
                    null, shapePixels, shape.Item1 * 4);
                ImageOutput.Save(shapeImage, Path.Combine(retryFolder, name + ".png"));
                await FolderCaptures.RefreshFolderAsync(retrySource);
                var shapeItem = FolderCaptures.LinkedItems(retrySource).Single(item => item.Window == name);
                var thumb = FolderCaptures.LoadThumb(shapeItem.Id);
                if (thumb == null || thumb.PixelWidth != shape.Item3 || thumb.PixelHeight != shape.Item4 || !thumb.IsFrozen)
                    throw new InvalidOperationException($"Folder thumbnail {name} decoded to {thumb?.PixelWidth}x{thumb?.PixelHeight}, expected {shape.Item3}x{shape.Item4}.");
            }
            report.Add("folder_thumbnails_bound_longest_edge_without_upscaling=PASS");
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
