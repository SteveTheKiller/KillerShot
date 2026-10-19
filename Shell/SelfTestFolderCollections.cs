using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static partial class SelfTestLibrary
{
    private static async Task RunFolderCollections(string folder)
    {
        int code = 1;
        var report = new List<string>();
        MainWindow? window = null;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new folder collection test directory.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            FolderCaptures.UseFolder(Path.Combine(folder, "Screenshots"));
            App.InitializeKit();
            string photos = Path.Combine(folder, "Linked photos");
            Directory.CreateDirectory(Path.Combine(photos, "Nested"));
            var pixel = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 }, 8);
            pixel.Freeze();
            string original = Path.Combine(photos, "first.png");
            ImageOutput.SavePng(pixel, original);
            ImageOutput.SavePng(pixel, Path.Combine(photos, "Nested", "second.png"));
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)));
            string source = FolderCaptures.AddFolder(photos);
            await FolderCaptures.RefreshFolderAsync(source);
            var items = FolderCaptures.LinkedItems(source);
            if (items.Count != 2 || !items.Any(item => item.Window == "second")
                || FolderCaptures.AddFolder(photos + Path.DirectorySeparatorChar) != source
                || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original))) != hash)
                throw new InvalidOperationException("A linked collection must include nested images once and preserve original files.");
            report.Add("recursive_linked_folder_duplicate_path_and_original_preservation=PASS");
            string sharePath = @"\\localhost\" + photos[..1] + "$" + photos[2..];
            if (Directory.Exists(sharePath))
            {
                string shared = FolderCaptures.AddFolder(sharePath);
                await FolderCaptures.RefreshFolderAsync(shared).WaitAsync(TimeSpan.FromSeconds(20));
                if (FolderCaptures.LinkedItems(shared).Count != 2)
                    throw new InvalidOperationException("An available SMB folder must include its nested images.");
                FolderCaptures.RemoveFolder(shared);
                report.Add("loopback_smb_recursive_image_read=PASS");
            }
            else report.Add("loopback_smb_recursive_image_read=SKIPPED (no accessible administrative share)");
            string probe = Path.Combine(folder, "restart.txt");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "--selftest-folder-probe", Path.Combine(folder, "settings"), source, probe })
                start.ArgumentList.Add(argument);
            using (var child = Process.Start(start) ?? throw new InvalidOperationException("Restart probe did not launch."))
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                if (child.ExitCode != 0 || File.ReadAllText(probe) != "ok")
                    throw new InvalidOperationException("A fresh process must restore the saved recursive collection.");
            }
            report.Add("saved_folder_restored_in_fresh_process=PASS");
            var first = items.Single(item => item.Window == "first");
            _ = FolderCaptures.LoadThumb(first.Id) ?? throw new InvalidOperationException("Linked thumbnail did not load.");
            var replacement = BitmapSource.Create(1, 2, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 255, 0, 0, 255, 255, 0, 0, 255 }, 4);
            replacement.Freeze();
            ImageOutput.SavePng(replacement, original);
            File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddSeconds(2));
            await FolderCaptures.RefreshFolderAsync(source);
            var updated = FolderCaptures.LinkedItems(source).Single(item => item.Id == first.Id);
            var thumbnail = FolderCaptures.LoadThumb(first.Id)!;
            if (updated.Width != 1 || updated.Height != 2 || thumbnail.PixelHeight <= thumbnail.PixelWidth)
                throw new InvalidOperationException("Changed linked files must invalidate cached dimensions and pixels.");
            File.Delete(original);
            await FolderCaptures.RefreshFolderAsync(source);
            if (FolderCaptures.LinkedItems(source).Count != 1 || FolderCaptures.PathOf(first.Id) != null)
                throw new InvalidOperationException("Removed linked files must disappear from the collection and image lookup.");
            report.Add("changed_and_removed_linked_files_refresh=PASS");
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.Enabled = false;
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("Test album did not open.");
            App.CloseToTray = true;
            window = new MainWindow { Width = 1000, Height = 700, ShowActivated = false };
            window.Show();
            await App.Instance.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            var navigation = window.TestNavigationStrip!;
            var menu = navigation.SourceMenu(window);
            var select = menu.Items.OfType<MenuItem>().SingleOrDefault(item => Equals(item.Header, "Linked photos"));
            if (select == null) throw new InvalidOperationException("Saved folders must be selectable from the collection menu.");
            select.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var library = (LibraryView)window.FindName("Library");
            library.SetView("flow");
            window.UpdateLayout();
            var flow = (KillerShot.Controls.CoverFlow)library.FindName("BigFlow");
            if (navigation.Source != source || flow.Items.Count != 1 || flow.Items[0].Window != "second")
                throw new InvalidOperationException("Selecting a linked folder must filter the main photo view.");
            var root = (FrameworkElement)window.Content;
            var render = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            render.Render(root);
            ImageOutput.SavePng(render, Path.Combine(folder, "linked-folder.png"));
            report.Add("saved_folder_selected_through_collection_menu_and_rendered=PASS");
            long stored = CaptureLibrary.AddNow(pixel, DateTime.Now, "region", "Stored sample", null);
            long storedCollection = CaptureLibrary.CreateCollection("Stored collection", [stored]);
            library.SetGroup("collection");
            navigation.SetSource("all");
            if (flow.Items.Count != 2) throw new InvalidOperationException("All collection groups must include linked folders.");
            navigation.SetSource("c" + storedCollection);
            if (flow.Items.Count != 1 || flow.Items[0].Id != stored)
                throw new InvalidOperationException("Linked folders must not leak into another collection's filter.");
            navigation.SetSource(source);
            App.Instance.OpenLibraryCapture(FolderCaptures.LinkedItems(source)[0]);
            if (window.TestNavigationStrip!.Source != source || window.TestStrip!.Source != source
                || ((KillerShot.Controls.CoverFlow)window.TestStrip.TestCarousel).Items.Count != 1)
                throw new InvalidOperationException("Opening a linked image must preserve its sidebar and editor carousel source.");
            App.Instance.SetDocument(null);
            report.Add("grouped_folder_filter_and_editor_context=PASS");
            string missing = Path.Combine(folder, "Missing folder");
            var clock = Stopwatch.StartNew();
            string missingSource = FolderCaptures.AddFolder(missing);
            string networkSource = FolderCaptures.AddFolder(@"\\127.0.0.1\KillerShot-missing-" + Guid.NewGuid().ToString("N"));
            _ = FolderCaptures.LinkedCollections();
            _ = FolderCaptures.LinkedItems(networkSource);
            if (clock.ElapsedMilliseconds > 1000)
                throw new InvalidOperationException("Registering and reading folder snapshots must not wait for filesystem or network access.");
            await FolderCaptures.RefreshFolderAsync(missingSource);
            navigation.SetSource(missingSource);
            if (FolderCaptures.LinkedCollections().Single(item => item.Source == missingSource).Available
                || navigation.Source != missingSource || FolderCaptures.LinkedItems(missingSource).Count != 0)
                throw new InvalidOperationException("An unavailable folder must retain a selectable, empty saved collection.");
            Directory.CreateDirectory(missing);
            ImageOutput.SavePng(pixel, Path.Combine(missing, "reconnected.png"));
            await FolderCaptures.RefreshFolderAsync(missingSource);
            if (FolderCaptures.LinkedItems(missingSource).Count != 1)
                throw new InvalidOperationException("An unavailable collection must recover when its folder becomes available.");
            FolderCaptures.RemoveFolder(source);
            if (!File.Exists(Path.Combine(photos, "Nested", "second.png"))
                || FolderCaptures.LinkedCollections().Any(item => item.Source == source))
                throw new InvalidOperationException("Removing a folder collection must preserve its original files.");
            report.Add("unavailable_paths_nonblocking_selection_recovery_and_link_removal=PASS");
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            window?.Close();
            CaptureLibrary.Close();
            File.WriteAllLines(Path.Combine(folder, "folder-collections-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }

    private static async Task ProbeFolderCollections(string settings, string source, string report)
    {
        int code = 1;
        try
        {
            Settings.UseFolder(settings);
            App.InitializeKit();
            if (!FolderCaptures.LinkedCollections().Any(folder => folder.Source == source))
                throw new InvalidOperationException("Saved source missing.");
            await FolderCaptures.RefreshFolderAsync(source);
            if (FolderCaptures.LinkedItems(source).Count != 2) throw new InvalidOperationException("Saved images missing.");
            File.WriteAllText(report, "ok");
            code = 0;
        }
        catch (Exception ex) { File.WriteAllText(report, ex.ToString()); }
        finally { App.Instance.Shutdown(code); }
    }
}
