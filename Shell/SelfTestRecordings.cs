using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Controls;
using KillerShot.Services;
using KillerShot.Video;

namespace KillerShot.Shell;

/// <summary>Recordings check: KillerShot.exe --selftest-recordings &lt;new folder&gt;. Encodes real
/// MP4 and GIF clips with the app's own encoders, then checks the Recordings folder collection,
/// moving a clip into a scratch encrypted library and back out byte for byte, the size cap,
/// playback copies and their cleanup, the storage choice, and renders the first-recording
/// dialog to recording-dialog.png. Settings, the library and every clip live in the folder.</summary>
internal static class SelfTestRecordings
{
    public static bool TryRun(string[] args)
    {
        if (args.Length is not (2 or 3 or 4) || args[0] != "--selftest-recordings") return false;
        _ = RunAsync(Path.GetFullPath(args[1]), args.Length >= 3 ? args[2] : null, args.Length == 4 ? args[3] : null);
        return true;
    }

    /// <summary>theme: a theme name such as Light or 98SE; locale: a language such as de-DE.
    /// Both only change how the dialog and library render.</summary>
    private static async Task RunAsync(string folder, string? theme, string? locale)
    {
        int code = 1;
        var report = new List<string>();
        MainWindow? window = null;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new recordings test directory.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            if (theme != null) Settings.Set("Theme", theme);
            if (locale != null) Settings.Set("Locale", locale);
            App.InitializeKit();
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError ?? "Album did not open.");
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            string recordings = Path.Combine(folder, "Screen Recordings");
            Directory.CreateDirectory(recordings);
            FolderCaptures.UseFolder(Path.Combine(folder, "Screenshots"));
            FolderCaptures.RecordingsFolderOverride = recordings;

            string mp4 = Path.Combine(recordings, "clip.mp4");
            await EncodeMp4(mp4, 64, 48);
            string gif = Path.Combine(recordings, "loop.gif");
            await EncodeGif(gif, 32, 24);
            // A recording still being written sits in a hidden staging folder and must never show.
            string staging = Directory.CreateDirectory(Path.Combine(recordings, ".KillerShot-staging-test")).FullName;
            File.SetAttributes(staging, File.GetAttributes(staging) | FileAttributes.Hidden);
            File.Copy(mp4, Path.Combine(staging, "recording.mp4"));

            // 1. The folder collection: both clips as videos at their real size, nothing staged.
            if (!FolderCaptures.LinkedCollections().Any(c => c.Source == FolderCaptures.RecordingsSource))
                throw new InvalidOperationException("The Recordings collection is missing.");
            await FolderCaptures.RefreshFolderAsync(FolderCaptures.RecordingsSource);
            var items = FolderCaptures.LinkedItems(FolderCaptures.RecordingsSource);
            var clip = items.SingleOrDefault(i => i.Window == "clip");
            var loop = items.SingleOrDefault(i => i.Window == "loop");
            if (items.Count != 2 || clip == null || loop == null || !items.All(VideoCaptures.IsVideo))
                throw new InvalidOperationException($"Expected the two clips as videos, got {items.Count}: " + string.Join(", ", items.Select(i => i.Window + "/" + i.Mode)));
            if (clip.Width != 64 || clip.Height != 48 || loop.Width != 32 || loop.Height != 24)
                throw new InvalidOperationException($"Clip sizes were {clip.Width}x{clip.Height} and {loop.Width}x{loop.Height}.");
            report.Add("recordings_folder_lists_mp4_and_gif_as_videos_at_real_size_without_staging=PASS");
            var thumb = FolderCaptures.LoadThumb(clip.Id);
            var gifThumb = FolderCaptures.LoadThumb(loop.Id);
            if (gifThumb == null) throw new InvalidOperationException("The GIF has no poster frame.");
            report.Add(thumb != null ? $"mp4_poster_frame=PASS ({thumb.PixelWidth}x{thumb.PixelHeight})"
                : "mp4_poster_frame=SKIPPED (Windows has no MP4 thumbnail provider here)");
            var (_, _, duration) = VideoCaptures.Probe(mp4);
            if (duration < TimeSpan.FromMilliseconds(500)) throw new InvalidOperationException($"MP4 duration read as {duration}.");
            report.Add($"mp4_duration_read=PASS ({VideoCaptures.FormatDuration(duration)})");

            // 2. Linking another folder never saves the built-in one with it.
            string other = Directory.CreateDirectory(Path.Combine(folder, "Other")).FullName;
            FolderCaptures.AddFolder(other);
            var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(Settings.Get("LinkedFolderCollections") ?? "{}")!;
            if (saved.Count != 1 || saved.ContainsKey(FolderCaptures.RecordingsSource)
                || saved.Values.Any(path => string.Equals(path, recordings, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The Recordings collection was saved as a linked folder.");
            FolderCaptures.RecordingsEnabled = false;
            if (FolderCaptures.LinkedCollections().Any(c => c.Source == FolderCaptures.RecordingsSource))
                throw new InvalidOperationException("Turning the Recordings collection off left it showing.");
            FolderCaptures.RecordingsEnabled = true;
            if (!FolderCaptures.LinkedCollections().Any(c => c.Source == FolderCaptures.RecordingsSource))
                throw new InvalidOperationException("Turning the Recordings collection back on did not bring it back.");
            report.Add("recordings_collection_not_saved_and_toggles=PASS");

            // 3. The storage choice: unset until answered, and only the two real answers stick.
            if (RecordingStorage.Choice != null) throw new InvalidOperationException("A fresh install already had a storage choice.");
            RecordingStorage.Choice = "nonsense";
            if (RecordingStorage.Choice != null) throw new InvalidOperationException("An unknown storage choice was kept.");
            RecordingStorage.Choice = RecordingStorage.Library;
            if (RecordingStorage.Choice != RecordingStorage.Library) throw new InvalidOperationException("The library choice did not persist.");
            report.Add("storage_choice_unset_validated_and_persisted=PASS");

            // 4. Into the library: the plain file goes, the clip comes back out byte for byte.
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mp4)));
            string moving = Path.Combine(recordings, "moving.mp4");
            File.Copy(mp4, moving);
            long id = await RecordingStorage.MoveToLibraryAsync(moving, DateTime.Now);
            if (File.Exists(moving)) throw new InvalidOperationException("The plain recording stayed after it moved into the library.");
            var stored = CaptureLibrary.Items().SingleOrDefault(i => i.Id == id) ?? throw new InvalidOperationException("The stored recording is not listed.");
            long collection = CaptureLibrary.RecordingsCollection();
            if (!VideoCaptures.IsVideo(stored) || stored.CollectionId != collection || stored.Width != 64 || stored.Height != 48)
                throw new InvalidOperationException($"The stored recording is {stored.Mode} {stored.Width}x{stored.Height} in {stored.CollectionId}, not video 64x48 in {collection}.");
            if (CaptureLibrary.LoadThumb(stored) == null) throw new InvalidOperationException("The stored recording has no thumbnail.");
            string exported = Path.Combine(folder, "exported.mp4");
            CaptureLibrary.ExportVideo(id, exported);
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exported))) != hash || CaptureLibrary.VideoExtension(id) != ".mp4")
                throw new InvalidOperationException("The exported clip differs from the recording.");
            report.Add($"library_move_deletes_plain_file_and_round_trips_bytes=PASS (limit {CaptureLibrary.MaxVideoBytes} bytes)");

            // 5. Playback copies decrypt to the playback folder and are wiped.
            string playable = VideoCaptures.PlayablePath(stored);
            if (!playable.StartsWith(VideoCaptures.PlaybackFolder, StringComparison.OrdinalIgnoreCase)
                || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(playable))) != hash)
                throw new InvalidOperationException("The playback copy is wrong or outside the playback folder.");
            VideoCaptures.WipePlayback();
            if (Directory.EnumerateFiles(VideoCaptures.PlaybackFolder).Any()) throw new InvalidOperationException("Playback copies survived the wipe.");
            report.Add("playback_copy_decrypts_and_wipes=PASS");

            // 6. Over the cap, the recording stays where it is.
            string big = Path.Combine(recordings, "big.mp4");
            File.Copy(mp4, big);
            CaptureLibrary.MaxVideoBytesOverride = 100;
            try
            {
                await RecordingStorage.MoveToLibraryAsync(big, DateTime.Now);
                throw new InvalidOperationException("An over-limit recording was stored.");
            }
            catch (VideoTooLargeException) { }
            finally { CaptureLibrary.MaxVideoBytesOverride = null; }
            if (!File.Exists(big)) throw new InvalidOperationException("An over-limit recording was deleted.");
            report.Add("over_limit_recording_stays_in_folder=PASS");

            // 7. Deleting the capture takes its clip with it.
            CaptureLibrary.Delete([id]);
            try { CaptureLibrary.ExportVideo(id, Path.Combine(folder, "gone.mp4")); throw new InvalidOperationException("A deleted recording could still be exported."); }
            catch (InvalidOperationException ex) when (ex.Message != "A deleted recording could still be exported.") { }
            report.Add("delete_removes_stored_clip=PASS");

            // 8. The library on the Recordings collection, rendered: three clips with play marks.
            await FolderCaptures.RefreshFolderAsync(FolderCaptures.RecordingsSource);
            App.CloseToTray = true;
            window = new MainWindow { Width = 1000, Height = 700, ShowActivated = false, Left = -4000, Top = -4000, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            await App.Instance.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            var navigation = window.TestNavigationStrip!;
            var select = navigation.SourceMenu(window).Items.OfType<System.Windows.Controls.MenuItem>()
                .SingleOrDefault(item => Equals(item.Header, "Screen Recordings"))
                ?? throw new InvalidOperationException("The Recordings collection is not in the collection menu.");
            select.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            var library = (LibraryView)window.FindName("Library");
            library.SetView("flow");
            window.UpdateLayout();
            var flow = (CoverFlow)library.FindName("BigFlow");
            if (navigation.Source != FolderCaptures.RecordingsSource || flow.Items.Count != 3 || !flow.Items.All(VideoCaptures.IsVideo))
                throw new InvalidOperationException($"The Recordings collection showed {flow.Items.Count} items.");
            library.SetView("grid");
            await Task.Delay(800);
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var shot = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            shot.Render(content);
            ImageOutput.SavePng(shot, Path.Combine(folder, "library-recordings.png"));
            report.Add("recordings_collection_selected_and_rendered=PASS");

            // 9. The first-recording dialog, rendered for a look, closing unanswered.
            var dialog = new RecordingStorageDialog(mp4) { ShowInTaskbar = false, Left = -4000, Top = -4000, WindowStartupLocation = WindowStartupLocation.Manual };
            dialog.Show();
            await Task.Delay(600);
            dialog.UpdateLayout();
            var root = (FrameworkElement)dialog.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(folder, "recording-dialog.png"))) png.Save(stream);
            dialog.Close();
            if (dialog.Choice != null || dialog.OpenClip) throw new InvalidOperationException("Closing the dialog answered the question.");
            report.Add("first_recording_dialog_renders_and_close_answers_nothing=PASS");
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            window?.Close();
            CaptureLibrary.Close();
            File.WriteAllLines(Path.Combine(folder, "recordings-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }

    private static async Task EncodeMp4(string path, int width, int height)
    {
        using var sink = new Mp4Encoder(path, .1);
        await sink.StartAsync(width, height, 30);
        for (int i = 0; i < 30; i++)
        {
            sink.AddFrame(Frame(width, height, i), TimeSpan.FromSeconds(i / 30.0));
            await Task.Delay(5);
        }
        await sink.FinishAsync(TimeSpan.FromSeconds(1));
    }

    private static async Task EncodeGif(string path, int width, int height)
    {
        using var sink = new GifEncoder(path, 64, 1.0, true);
        await sink.StartAsync(width, height, 10);
        for (int i = 0; i < 5; i++) sink.AddFrame(Frame(width, height, i * 6), TimeSpan.FromSeconds(i / 10.0));
        await sink.FinishAsync(TimeSpan.FromSeconds(0.5));
    }

    /// <summary>A moving gradient, so the encoder has real motion to compress.</summary>
    private static byte[] Frame(int width, int height, int step)
    {
        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                bgra[o] = (byte)(x * 4 + step * 8);
                bgra[o + 1] = (byte)(y * 5);
                bgra[o + 2] = (byte)(255 - step * 8);
                bgra[o + 3] = 255;
            }
        return bgra;
    }
}
