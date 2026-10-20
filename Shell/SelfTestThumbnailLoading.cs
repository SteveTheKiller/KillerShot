using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Controls;
using KillerShot.Services;
using Microsoft.Data.Sqlite;

namespace KillerShot.Shell;

internal static partial class SelfTestLibrary
{
    private static async Task RunThumbnailLoading(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int exit = 1;
        Task? held = null;
        try
        {
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
            FolderCaptures.Enabled = false;
            App.InitializeKit();
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            CaptureLibrary.AddNow(image, DateTime.Now, "region", "Thumbnail sample", null);
            var window = new MainWindow { Width = 900, Height = 600, ShowActivated = false };
            var flow = (CoverFlow)((LibraryView)window.FindName("Library")).FindName("BigFlow");
            string database = CaptureLibrary.FilePath;
            string key = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path.ChangeExtension(database, ".key")),
                "KillerShot.Library.v1"u8.ToArray(), DataProtectionScope.CurrentUser));
            CaptureLibrary.Close();
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            var items = CaptureLibrary.Items();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            held = Task.Run(async () =>
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Password = key, Pooling = false }.ConnectionString);
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "BEGIN EXCLUSIVE";
                command.ExecuteNonQuery();
                ready.TrySetResult();
                await Task.Delay(500);
                command.CommandText = "ROLLBACK";
                command.ExecuteNonQuery();
            });
            if (!ready.Task.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Thumbnail lock fixture did not start.");
            var timer = Stopwatch.StartNew();
            flow.Items = items;
            timer.Stop();
            report.Add($"carousel_thumbnail_construction_ms={timer.ElapsedMilliseconds}");
            await held;
            if (timer.ElapsedMilliseconds >= 150)
                throw new InvalidOperationException("Carousel construction waited for uncached database thumbnail I/O.");
            window.Show();
            BitmapSource? loaded = null;
            for (int n = 0; n < 150 && loaded == null; n++)
            {
                loaded = CaptureLibrary.RequestThumb(items.Single());
                await Task.Delay(20);
            }
            if (loaded == null || !loaded.IsFrozen || loaded.PixelWidth != 1)
                throw new InvalidOperationException("The visible carousel did not receive its completed thumbnail.");
            ThumbnailCache.Clear(folders: true);
            var cacheImage = BitmapSource.Create(512, 512, 96, 96, PixelFormats.Bgra32, null, new byte[512 * 512 * 4], 512 * 4);
            cacheImage.Freeze();
            for (int n = 0; n < 70; n++) ThumbnailCache.Put(0, -10000 - n, cacheImage);
            if (Enumerable.Range(0, 6).Any(n => ThumbnailCache.Get(0, -10000 - n) != null)
                || Enumerable.Range(6, 64).Any(n => ThumbnailCache.Get(0, -10000 - n) == null))
                throw new InvalidOperationException("The completed thumbnail cache exceeded 64 MiB or evicted recent images.");
            ThumbnailCache.Clear(folders: true);
            report.Add("visible_thumbnail_completion_and_64MiB_cache_eviction=PASS");
            exit = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            if (held != null) await held;
            CaptureLibrary.Close();
            File.WriteAllLines(Path.Combine(folder, "thumbnail-loading-report.txt"), report);
            App.Instance.Shutdown(exit);
        }
    }
}
