using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;
using KillerShot.Editor;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace KillerShot.Shell;

internal static class SelfTestNovemberStorage
{
    public static bool TryRun(string[] args)
    {
        if (args.Length != 3 || args[0] != "--selftest-november-storage") return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        _ = RunAndExitAsync(folder, args[2]);
        return true;
    }

    private static async Task RunAndExitAsync(string folder, string scenario)
    {
        var log = new StringBuilder();
        int exit = 0;
        try
        {
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            if (scenario == "diagnostic-video-read-availability") await VideoImportResponsive(folder, log);
            else if (scenario == "album-video-import-reads") await VideoImportReads(folder, log);
            else if (scenario == "album-export-writes") await ExportAllowsWrites(folder, log);
            else if (scenario == "album-network-refused") NetworkAlbumRefused(folder);
            else if (scenario == "album-rekey-leftover") RekeyLeftover(folder);
            else if (scenario == "album-password-reopen-failure") PasswordReopenFailure(folder);
            else if (scenario == "album-legacy-import-keep") await LegacyImportKeep(folder);
            else if (scenario == "settings-locked-read") LockedSettingsRead(folder);
            else if (scenario == "album-decode-responsive") await DecodeResponsive(folder, log);
            else if (scenario == "folder-continuous-events") await ContinuousFolderEvents(folder);
            else if (scenario == "folder-overflow") await FolderOverflow(folder);
            else if (scenario == "folder-linked-watch") await LinkedFolderWatch(folder);
            else if (scenario == "folder-linked-spacing") await LinkedFolderSpacing(folder);
            else if (scenario == "folder-linked-recreated") await LinkedFolderRecreated(folder);
            else if (scenario == "album-video-drag-responsive") await VideoDragResponsive(folder, log);
            else if (scenario == "folder-linked-initial-arrival") await LinkedInitialArrival(folder);
            else if (scenario == "folder-network-backoff") await NetworkFolderBackoff(folder);
            else if (scenario == "album-drag-missing-image") await MissingImageDrag(folder);
            else if (scenario == "album-video-play-responsive") await VideoPlayResponsive(folder, log);
            else if (scenario == "album-recording-cleanup-warning") await RecordingCleanupWarning(folder);
            else Run(folder, scenario);
            log.AppendLine($"tests=1 scenario={scenario} result=ok");
        }
        catch (Exception error) { log.AppendLine($"tests=1 result=error {error}"); exit = 1; }
        File.WriteAllText(Path.Combine(folder, "report.txt"), log.ToString());
        Application.Current.Shutdown(exit);
    }

    internal static void Run(string folder, string scenario)
    {
        Settings.UseFolder(folder);
        if (scenario.StartsWith("album-", StringComparison.Ordinal))
        {
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
        }
        string path = Path.Combine(folder, "settings.json");
        if (scenario == "settings-pending")
        {
            Settings.SetRequired("A", "old");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try { Settings.Set("A", "new"); }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
            Settings.SetRequired("B", "other");
            Settings.UseFolder(folder);
            if (Settings.Get("A") != "new" || Settings.Get("B") != "other")
                throw new InvalidOperationException("A failed setting save was lost after another setting saved.");
        }
        else if (scenario == "settings-corrupt")
        {
            const string corrupt = "{bad settings: preserve me}";
            File.WriteAllText(path, corrupt);
            Settings.SetRequired("B", "saved");
            string[] backups = Directory.GetFiles(folder, "settings.json.corrupt-*");
            if (backups.Length != 1 || File.ReadAllText(backups[0]) != corrupt)
                throw new InvalidOperationException("Saving replaced corrupt settings without retaining the original bytes.");
            Settings.UseFolder(folder);
            if (Settings.Get("B") != "saved") throw new InvalidOperationException("Replacement settings were not saved.");
        }
        else if (scenario == "duration")
        {
            if (VideoCaptures.FormatDuration(new TimeSpan(2, 3, 4, 5)) != "51:04:05")
                throw new InvalidOperationException("A recording over 24 hours lost its days in the duration label.");
        }
        else if (scenario == "request-acks")
        {
            string stale = Path.Combine(folder, "old.request.ack");
            string recent = Path.Combine(folder, "recent.request.ack");
            File.WriteAllText(stale, "delivered");
            File.WriteAllText(recent, "delivered");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-2));
            new ImageOpenRequests(folder).Drain(_ => { });
            if (File.Exists(stale) || !File.Exists(recent))
                throw new InvalidOperationException("Draining requests did not remove only expired acknowledgments.");
        }
        else if (scenario == "album-staged-key")
        {
            string album = CaptureLibrary.FilePath;
            CaptureLibrary.Close();
            string key = Path.ChangeExtension(album, ".key");
            File.Move(key, key + ".next");
            if (CaptureLibrary.Albums().Single().PasswordProtected)
                throw new InvalidOperationException("An album with a staged account key was reported as password protected.");
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The staged account key did not reopen.");
            CaptureLibrary.Close();
        }
        else if (scenario == "album-export-staging")
        {
            string clip = Path.Combine(folder, "clip.mp4");
            File.WriteAllBytes(clip, [1, 3, 5, 7, 9]);
            long id = CaptureLibrary.AddVideo(clip, DateTime.Now, "Synthetic clip");
            string destination = Path.Combine(folder, "export.mp4");
            File.WriteAllText(destination + ".partial", "user file");
            CaptureLibrary.ExportVideo(id, destination);
            if (!File.Exists(destination + ".partial") || File.ReadAllText(destination + ".partial") != "user file")
                throw new InvalidOperationException("Export destroyed a user-owned partial file.");
            if (!File.ReadAllBytes(destination).SequenceEqual(new byte[] { 1, 3, 5, 7, 9 }))
                throw new InvalidOperationException("Export changed recording bytes.");
            CaptureLibrary.Close();
        }
        else if (scenario == "album-recording-cleanup")
        {
            string clip = Path.Combine(folder, "locked.mp4");
            File.WriteAllBytes(clip, [2, 4, 6]);
            File.SetAttributes(clip, FileAttributes.ReadOnly);
            try
            {
                var result = RecordingStorage.MoveToLibraryAsync(clip, DateTime.Now).GetAwaiter().GetResult();
                if (result.Id <= 0 || result.Session != CaptureLibrary.Session || result.CleanupError == null
                    || !File.Exists(clip) || CaptureLibrary.Items().Count != 1)
                    throw new InvalidOperationException("A committed recording was not reported after source cleanup failed.");
            }
            finally { File.SetAttributes(clip, FileAttributes.Normal); CaptureLibrary.Close(); }
        }
        else if (scenario == "album-qr-failure")
        {
            CaptureLibrary.AddNow(Pixel(1), DateTime.Now.AddSeconds(-1), "region", "Older", null);
            CaptureLibrary.AddNow(Pixel(2), DateTime.Now, "region", "Newest", null);
            int reads = 0;
            CaptureLibrary.QrRecognizer = _ =>
            {
                if (Interlocked.Increment(ref reads) == 1) throw new InvalidDataException("Undecodable capture");
                return [];
            };
            CaptureLibrary.QrReadingEnabled = true;
            CaptureLibrary.BackgroundWork = true;
            try
            {
                CaptureLibrary.ScanQrAsync().GetAwaiter().GetResult();
                CaptureLibrary.ScanQrAsync().GetAwaiter().GetResult();
                if (reads != 2) throw new InvalidOperationException("A bad QR capture blocked later captures or was retried forever.");
            }
            finally { CaptureLibrary.BackgroundWork = false; CaptureLibrary.Close(); }
        }
        else if (scenario == "album-stale-preview")
        {
            CaptureLibrary.AddNow(Pixel(), DateTime.Now, "region", "Old album", null);
            var stale = CaptureLibrary.Items().Single();
            string next = CaptureLibrary.CreateAlbum("Next");
            if (!CaptureLibrary.TrySwitchAlbum(next, null, out _)) throw new InvalidOperationException("Next album did not open.");
            var original = Pixel();
            var changed = Pixel(0);
            long id = CaptureLibrary.AddNow(original, DateTime.Now, "region", "New album", null);
            CaptureLibrary.SaveMarks(id, new EditorDocument(changed).State, original, changed);
            bool rejected = false;
            try { CaptureLibrary.LoadPreview(stale); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("A stale capture loaded an edited preview from another album.");
            CaptureLibrary.Close();
        }
        else if (scenario == "album-thumbnail-edit")
        {
            var original = Pixel();
            long edited = CaptureLibrary.AddNow(original, DateTime.Now, "region", "Edited", null);
            long untouched = CaptureLibrary.AddNow(Pixel(100), DateTime.Now, "region", "Untouched", null);
            var before = CaptureLibrary.LoadThumb(untouched);
            var changed = Pixel(0);
            CaptureLibrary.SaveMarks(edited, new EditorDocument(changed).State, original, changed);
            if (before == null || !ReferenceEquals(before, CaptureLibrary.LoadThumb(untouched)))
                throw new InvalidOperationException("Editing one capture evicted an unrelated capture thumbnail.");
            CaptureLibrary.Close();
        }
        else if (scenario == "album-declined-switch")
        {
            string next = CaptureLibrary.CreateAlbum("Declined");
            if (CaptureLibrary.TrySwitchAlbum(next, null, out _, () => false))
                throw new InvalidOperationException("The switch was not declined.");
            using (File.Open(next, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            CaptureLibrary.Close();
        }
        else if (scenario == "album-recordings-collection")
        {
            using var firstCreated = new ManualResetEventSlim();
            using var secondCreated = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int notifications = 0;
            void OnChanged()
            {
                if (Interlocked.Increment(ref notifications) == 1) { firstCreated.Set(); release.Wait(TimeSpan.FromSeconds(5)); }
                else secondCreated.Set();
            }
            CaptureLibrary.Changed += OnChanged;
            try
            {
                var first = Task.Run(CaptureLibrary.RecordingsCollection);
                if (!firstCreated.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("No collection creation notification.");
                var second = Task.Run(CaptureLibrary.RecordingsCollection);
                secondCreated.Wait(TimeSpan.FromSeconds(1));
                release.Set();
                Task.WaitAll(first, second);
                if (first.Result != second.Result || CaptureLibrary.Collections().Count != 1)
                    throw new InvalidOperationException("Concurrent recording moves created duplicate Recordings collections.");
            }
            finally { release.Set(); CaptureLibrary.Changed -= OnChanged; CaptureLibrary.Close(); }
        }
        else if (scenario == "album-unlock-error")
        {
            CaptureLibrary.SetPassword("fixture password");
            string album = CaptureLibrary.FilePath;
            CaptureLibrary.Close();
            File.WriteAllBytes(album, []);
            if (CaptureLibrary.Unlock("fixture password") || string.IsNullOrEmpty(CaptureLibrary.LoadError))
                throw new InvalidOperationException("Unlock did not report its invalid album through LoadError.");
        }
        else if (scenario == "album-playback-cache")
        {
            string clip = Path.Combine(folder, "clip.mp4");
            File.WriteAllBytes(clip, [11, 22, 33]);
            CaptureLibrary.AddVideo(clip, DateTime.Now, "Cache clip");
            var item = CaptureLibrary.Items().Single();
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            string first = VideoCaptures.PlayablePath(item);
            string second = VideoCaptures.PlayablePath(item);
            if (first != second || Directory.GetFiles(VideoCaptures.PlaybackFolder).Length != 1)
                throw new InvalidOperationException("Repeated playback decrypted duplicate recording files.");
            File.Delete(first);
            string restored = VideoCaptures.PlayablePath(item);
            if (!File.Exists(restored) || !File.ReadAllBytes(restored).SequenceEqual(new byte[] { 11, 22, 33 }))
                throw new InvalidOperationException("An expired playback cache entry did not regenerate.");
            VideoCaptures.WipePlayback();
            CaptureLibrary.Close();
        }
        else if (scenario == "album-count-sources")
        {
            long collection = CaptureLibrary.CreateCollection("One collection");
            long first = CaptureLibrary.AddNow(Pixel(), DateTime.Now, "region", "First", null);
            CaptureLibrary.AddNow(Pixel(), DateTime.Now, "region", "Second", null);
            CaptureLibrary.AddNow(Pixel(), DateTime.Now, "region", "Third", null);
            CaptureLibrary.Move([first], collection);
            if (CaptureLibrary.CountItems("all") != 3 || CaptureLibrary.CountItems("c0") != 2
                || CaptureLibrary.CountItems("c" + collection.ToString(System.Globalization.CultureInfo.InvariantCulture)) != 1
                || CaptureLibrary.CountItems("c999999") != 0)
                throw new InvalidOperationException("Source counts did not match the album's captures and collections.");
            CaptureLibrary.Close();
        }
        else if (scenario == "album-reference-allocation")
        {
            long id = CaptureLibrary.AddNow(Pixel(), DateTime.Now, "region", "Target", null);
            string reference = CaptureLibrary.CaptureReference(id);
            using (var fixture = OpenFixtureDatabase())
            using (var fill = fixture.CreateCommand())
            {
                fill.CommandText = """
                    WITH RECURSIVE seq(n) AS (SELECT 2 UNION ALL SELECT n+1 FROM seq WHERE n<20000)
                    INSERT INTO captures(id,taken_utc,width,height,mode,window_title,thumb,image)
                    SELECT n,638000000000000000,1,1,'region','Unrelated',X'',X'' FROM seq;
                    """;
                fill.ExecuteNonQuery();
            }
            _ = CaptureLibrary.ResolveReference(reference);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var resolved = CaptureLibrary.ResolveReference(reference);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (resolved?.Id != id || allocated > 64 * 1024)
                throw new InvalidOperationException($"Resolving one capture allocated {allocated} bytes in a 20000-capture album.");
            CaptureLibrary.Close();
        }
        else if (scenario == "album-pending-qr-choice")
        {
            string album = CaptureLibrary.FilePath;
            CaptureLibrary.Close();
            CaptureLibrary.QrReadingEnabled = true;
            bool switched = CaptureLibrary.TrySwitchAlbum(album, null, out _, () =>
            {
                CaptureLibrary.QrReadingEnabled = false;
                return true;
            });
            if (!switched || CaptureLibrary.QrReadingEnabled)
                throw new InvalidOperationException("A QR choice made while an album was preparing was lost at activation.");
            CaptureLibrary.Close();
            if (!CaptureLibrary.OpenAuto() || CaptureLibrary.QrReadingEnabled)
                throw new InvalidOperationException("The latest pending QR choice did not persist.");
            CaptureLibrary.Close();
        }
        else if (scenario == "folder-drive-failure")
        {
            string photos = Path.Combine(folder, "photos");
            Directory.CreateDirectory(photos);
            FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
            FolderCaptures.DriveTypeReader = _ => throw new ArgumentException("The drive is unavailable.");
            string source = FolderCaptures.AddFolder(photos);
            FolderCaptures.RefreshFolderAsync(source).GetAwaiter().GetResult();
            if (FolderCaptures.StatusForSource(source) is not { Loading: false, Available: false })
                throw new InvalidOperationException("A rejected drive root left a folder collection loading forever.");
        }
        else if (scenario == "album-video-rekey-guard")
        {
            string clip = Path.Combine(folder, "clip.mp4");
            File.WriteAllBytes(clip, [1, 2, 3]);
            bool rejected = false;
            void OnBusy()
            {
                if (!CaptureLibrary.VideoBusy) return;
                CaptureLibrary.Changed -= OnBusy;
                try { CaptureLibrary.SetPassword(null); }
                catch (InvalidOperationException) { rejected = true; }
            }
            CaptureLibrary.Changed += OnBusy;
            try { CaptureLibrary.AddVideo(clip, DateTime.Now, "Guarded video"); }
            finally { CaptureLibrary.Changed -= OnBusy; }
            if (!rejected || CaptureLibrary.Count != 1)
                throw new InvalidOperationException("Password replacement was allowed during an active video operation.");
            CaptureLibrary.Close();
        }
        else throw new ArgumentException("Unknown storage scenario.");
    }

    private static BitmapSource Pixel(byte red = 255)
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, red, 255 }, 4);
        image.Freeze();
        return image;
    }

    private static SqliteConnection OpenFixtureDatabase()
    {
        string key = Encoding.UTF8.GetString(ProtectedData.Unprotect(
            File.ReadAllBytes(Path.ChangeExtension(CaptureLibrary.FilePath, ".key")),
            "KillerShot.Library.v1"u8.ToArray(), DataProtectionScope.CurrentUser));
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = CaptureLibrary.FilePath, Password = key, Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static async Task VideoImportResponsive(string folder, StringBuilder log)
    {
        Settings.UseFolder(folder);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        CaptureLibrary.BackgroundWork = false;
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
        string clip = Path.Combine(folder, "large.mp4");
        using (var stream = File.Create(clip)) stream.SetLength(200L * 1024 * 1024);
        var import = Task.Run(() => CaptureLibrary.AddVideo(clip, DateTime.Now, "Large recording"));
        double longest = 0;
        int pings = 0;
        while (!import.IsCompleted)
        {
            await Task.Delay(10);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _ = CaptureLibrary.Count;
            longest = Math.Max(longest, clock.Elapsed.TotalMilliseconds);
            pings++;
        }
        long id = await import;
        log.AppendLine($"diagnostic_only=True synchronous_read_queries={pings} longest_ms={longest:F1}");
        if (id <= 0 || pings == 0) throw new InvalidOperationException("The recording read-availability probe did not complete.");
        CaptureLibrary.Close();
    }

    /// <summary>Importing a large recording must not hold the album: listing it from another thread
    /// answers promptly the whole time, and the stored clip is whole afterwards.</summary>
    private static async Task VideoImportReads(string folder, StringBuilder log)
    {
        Settings.UseFolder(folder);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        CaptureLibrary.BackgroundWork = false;
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
        string clip = Path.Combine(folder, "large.mp4");
        using (var stream = File.Create(clip))
        {
            var block = new byte[1 << 20];
            for (int i = 0; i < 200; i++) { block[0] = (byte)i; stream.Write(block); }
        }
        var import = Task.Run(() => CaptureLibrary.AddVideo(clip, DateTime.Now, "Large recording"));
        double longest = 0;
        int reads = 0;
        while (!import.IsCompleted)
        {
            await Task.Delay(20);
            double elapsed = await Task.Run(() =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                _ = CaptureLibrary.Items();
                return clock.Elapsed.TotalMilliseconds;
            });
            longest = Math.Max(longest, elapsed);
            reads++;
        }
        long id = await import;
        log.AppendLine($"listing_reads={reads} longest_ms={longest:F1}");
        if (reads == 0) throw new InvalidOperationException("The import finished before any listing was timed.");
        if (longest >= 200) throw new InvalidOperationException($"Listing the album waited {longest:F0} ms behind a recording import.");
        string exported = Path.Combine(folder, "exported.mp4");
        CaptureLibrary.ExportVideo(id, exported);
        if (!File.ReadAllBytes(exported).AsSpan().SequenceEqual(File.ReadAllBytes(clip)))
            throw new InvalidOperationException("The imported recording is not byte for byte the original.");
        CaptureLibrary.Close();
    }

    /// <summary>Exporting a large recording reads the album for a long time; a note written
    /// meanwhile must not wait for it, and a capture written before a rename of the open album
    /// must be in the renamed album.</summary>
    private static async Task ExportAllowsWrites(string folder, StringBuilder log)
    {
        Settings.UseFolder(folder);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        CaptureLibrary.BackgroundWork = false;
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
        string clip = Path.Combine(folder, "large.mp4");
        using (var stream = File.Create(clip)) stream.SetLength(300L * 1024 * 1024);
        long video = CaptureLibrary.AddVideo(clip, DateTime.Now, "Large recording");
        var still = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[64], 16);
        long picture = CaptureLibrary.AddNow(still, DateTime.Now, "region", "Noted", null);
        var export = Task.Run(() => CaptureLibrary.ExportVideo(video, Path.Combine(folder, "exported.mp4")));
        while (!export.IsCompleted && !File.Exists(Path.Combine(folder, "exported.mp4"))
            && !Directory.EnumerateFiles(folder, "exported.mp4.*.partial").Any()) await Task.Delay(5);
        await Task.Delay(50);
        bool exporting = !export.IsCompleted;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(() => CaptureLibrary.SetNote(picture, "written during export"));
        double noteMs = clock.Elapsed.TotalMilliseconds;
        await export;
        log.AppendLine($"exporting_when_written={exporting} note_ms={noteMs:F0}");
        if (!exporting) throw new InvalidOperationException("The export finished before the write was timed.");
        if (noteMs >= 1000) throw new InvalidOperationException($"A note waited {noteMs:F0} ms for a recording export.");

        string renamed = CaptureLibrary.RenameAlbum(CaptureLibrary.FilePath, "Renamed while open");
        CaptureLibrary.Close();
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        if (!CaptureLibrary.TrySwitchAlbum(renamed, null, out _)) throw new InvalidOperationException(CaptureLibrary.LoadError);
        if (CaptureLibrary.ItemById(picture)?.Note != "written during export")
            throw new InvalidOperationException("The renamed album lost a capture written while it was open.");
        CaptureLibrary.Close();
        if (File.Exists(renamed + "-wal") && new FileInfo(renamed + "-wal").Length > 0)
            throw new InvalidOperationException("Closing the album left uncheckpointed changes beside it.");
    }

    /// <summary>A password change that stopped after writing its rekeyed copy leaves a full album
    /// copy under the abandoned key; the next verified open removes it.</summary>
    private static void RekeyLeftover(string folder)
    {
        Settings.UseFolder(folder);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        CaptureLibrary.BackgroundWork = false;
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
        string leftover = CaptureLibrary.FilePath + ".rekey";
        CaptureLibrary.Close();
        File.WriteAllText(leftover, "abandoned rekeyed copy");
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
        bool left = File.Exists(leftover);
        CaptureLibrary.Close();
        if (left) throw new InvalidOperationException("A verified open kept an abandoned rekeyed album copy.");
    }

    /// <summary>The one-time import of the old Recent folder under a "keep 1 day" limit: a week-old
    /// screenshot must not be imported, pruned and deleted from disk in one go.</summary>
    private static async Task LegacyImportKeep(string folder)
    {
        Settings.UseFolder(folder);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        string library = Path.Combine(folder, "library");
        CaptureLibrary.UseFolder(library);
        CaptureLibrary.BackgroundWork = false;
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
        CaptureLibrary.Keep = "days:1";
        CaptureLibrary.Close();
        string recent = Directory.CreateDirectory(Path.Combine(library, "Recent")).FullName;
        string old = Path.Combine(recent, "old.png");
        ImageOutput.SavePng(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[64], 16), old);
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-7));
        CaptureLibrary.UseFolder(library);
        CaptureLibrary.BackgroundWork = true;
        try
        {
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            await Task.Delay(3000);
            if (!File.Exists(old) && CaptureLibrary.Items().Count == 0)
                throw new InvalidOperationException("The legacy import deleted a week-old screenshot that the keep limit pruned at once.");
        }
        finally { CaptureLibrary.BackgroundWork = false; CaptureLibrary.Close(); }
    }

    /// <summary>Setting a password swaps in the re-encrypted album; when it then cannot be opened,
    /// the album must not be listed as unprotected beside an account-readable key and backup.
    /// A later successful password change leaves no retired key or backup behind.</summary>
    private static void PasswordReopenFailure(string folder)
    {
        Settings.UseFolder(folder);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        string library = Path.Combine(folder, "library");
        CaptureLibrary.UseFolder(library);
        CaptureLibrary.BackgroundWork = false;
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
        string album = CaptureLibrary.FilePath;
        try
        {
            CaptureLibrary.SetPassword("new album password", (source, destination, backup) =>
            {
                File.Replace(source, destination, backup);
                using var damaged = new FileStream(destination, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                damaged.Write(new byte[4096]);
            }, File.Delete);
            throw new InvalidOperationException("A password change into an unreadable album reported success.");
        }
        catch (Exception error) when (error is not InvalidOperationException { Message: "A password change into an unreadable album reported success." }) { }
        CaptureLibrary.Close();
        var listed = CaptureLibrary.Albums().Single(a => string.Equals(a.Path, album, StringComparison.OrdinalIgnoreCase));
        if (!listed.PasswordProtected)
            throw new InvalidOperationException("After a password change failed to reopen, the album was listed as unprotected.");

        string clean = Path.Combine(folder, "clean");
        CaptureLibrary.UseFolder(clean);
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
        string cleanAlbum = CaptureLibrary.FilePath;
        CaptureLibrary.SetPassword("another password");
        CaptureLibrary.Close();
        string[] left = Directory.GetFiles(clean).Where(f => f.EndsWith(".retire", StringComparison.Ordinal)
            || f.EndsWith(".bak", StringComparison.Ordinal) || f.EndsWith(".key", StringComparison.Ordinal)).ToArray();
        if (left.Length > 0) throw new InvalidOperationException("A completed password change left " + string.Join(", ", left.Select(Path.GetFileName)));
    }

    /// <summary>A settings file another program holds for a moment must not leave KillerShot on
    /// empty settings for the rest of the session.</summary>
    private static void LockedSettingsRead(string folder)
    {
        string settings = Path.Combine(folder, "locked-settings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "settings.json"), "{\"ActiveAlbum\":\"Other.kshot\"}");
        Settings.UseFolder(settings);
        using (new FileStream(Path.Combine(settings, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            _ = Settings.Get("ActiveAlbum");
        if (Settings.Get("ActiveAlbum") != "Other.kshot")
            throw new InvalidOperationException("One locked read left the session on empty settings.");
    }

    /// <summary>A shared album needs shared memory the network does not provide.</summary>
    private static void NetworkAlbumRefused(string folder)
    {
        Settings.UseFolder(folder);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        string local = Path.GetFullPath(Path.Combine(folder, "network-library"));
        Directory.CreateDirectory(local);
        string share = @"\\localhost\" + local[0] + "$" + local[2..];
        CaptureLibrary.UseFolder(share);
        CaptureLibrary.BackgroundWork = false;
        if (CaptureLibrary.OpenAuto()) throw new InvalidOperationException("An album opened from a network share.");
        if (CaptureLibrary.LoadError != LocaleManager.Loc("Str_Album_NetworkFolder"))
            throw new InvalidOperationException("A network album was refused with: " + CaptureLibrary.LoadError);
        if (Directory.EnumerateFiles(local).Any()) throw new InvalidOperationException("Refusing a network album still created files there.");
    }

    private sealed class CountingDirectoryReader : IFolderDirectoryReader
    {
        public int Reads;
        public Action? AfterRead;
        public int DelayMs;
        public IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo directory, EnumerationOptions options)
        {
            Interlocked.Increment(ref Reads);
            if (DelayMs > 0) Thread.Sleep(DelayMs);
            foreach (var file in directory.EnumerateFiles("*", options)) yield return file;
            Interlocked.Exchange(ref AfterRead, null)?.Invoke();
        }
        public IEnumerable<DirectoryInfo> EnumerateDirectories(DirectoryInfo directory, EnumerationOptions options) =>
            directory.EnumerateDirectories("*", options);
    }

    private static async Task NetworkFolderBackoff(string folder)
    {
        Settings.UseFolder(folder);
        string photos = Path.Combine(folder, "photos");
        Directory.CreateDirectory(photos);
        var reader = new CountingDirectoryReader();
        FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"), reader);
        FolderCaptures.DriveTypeReader = _ => DriveType.Network;
        string source = FolderCaptures.AddFolder(photos);
        await FolderCaptures.RefreshFolderAsync(source);
        int reads = reader.Reads;
        await Task.Delay(11000);
        if (reader.Reads != reads) throw new InvalidOperationException("A network folder was still recursively polled every ten seconds.");
        FolderCaptures.RemoveFolder(source);
    }

    private static async Task LinkedInitialArrival(string folder)
    {
        Settings.UseFolder(folder);
        string photos = Path.Combine(folder, "photos");
        Directory.CreateDirectory(photos);
        var pixel = Pixel();
        var reader = new CountingDirectoryReader
        { AfterRead = () => ImageOutput.SavePng(pixel, Path.Combine(photos, "arrived.png")) };
        FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"), reader);
        string source = FolderCaptures.AddFolder(photos);
        await FolderCaptures.RefreshFolderAsync(source);
        for (int n = 0; n < 60 && FolderCaptures.LinkedItems(source).Count == 0; n++) await Task.Delay(50);
        if (FolderCaptures.LinkedItems(source).Count != 1)
            throw new InvalidOperationException("A file arriving as the initial linked scan completed was lost before watching began.");
        FolderCaptures.RemoveFolder(source);
    }

    private static async Task LinkedFolderWatch(string folder)
    {
        Settings.UseFolder(folder);
        string photos = Path.Combine(folder, "photos");
        Directory.CreateDirectory(Path.Combine(photos, "nested"));
        var reader = new CountingDirectoryReader();
        FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"), reader);
        string source = FolderCaptures.AddFolder(photos);
        await FolderCaptures.RefreshFolderAsync(source);
        ImageOutput.SavePng(Pixel(), Path.Combine(photos, "nested", "new.png"));
        for (int n = 0; n < 60 && FolderCaptures.LinkedItems(source).Count == 0; n++) await Task.Delay(50);
        if (FolderCaptures.LinkedItems(source).Count != 1)
            throw new InvalidOperationException("A linked local folder waited for polling instead of watching new nested files.");
        await Task.Delay(500);
        int reads = reader.Reads;
        await Task.Delay(11000);
        if (reader.Reads != reads) throw new InvalidOperationException("An idle linked local folder was recursively polled.");
        FolderCaptures.RemoveFolder(source);
    }

    /// <summary>A linked folder that is written to without a pause is rescanned at a steady pace (each scan here takes 300 ms, so the folder is dirty again as soon as one ends),
    /// not back to back: scans start at least about two seconds apart.</summary>
    private static async Task LinkedFolderSpacing(string folder)
    {
        Settings.UseFolder(folder);
        string photos = Path.Combine(folder, "photos");
        Directory.CreateDirectory(photos);
        var reader = new CountingDirectoryReader { DelayMs = 300 };
        FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"), reader);
        string source = FolderCaptures.AddFolder(photos);
        await FolderCaptures.RefreshFolderAsync(source);
        await Task.Delay(500);
        int before = reader.Reads;
        var writes = Task.Run(async () =>
        {
            for (int n = 0; n < 120; n++)
            {
                File.WriteAllText(Path.Combine(photos, "incoming.txt"), n.ToString());
                await Task.Delay(50);
            }
        });
        await writes;
        int scans = reader.Reads - before;
        FolderCaptures.RemoveFolder(source);
        if (scans < 1)
            throw new InvalidOperationException("Continuous writes to a linked folder never caused a rescan.");
        if (scans > 4)
            throw new InvalidOperationException($"A continuously written linked folder was rescanned {scans} times in six seconds, back to back.");
    }

    /// <summary>A watched linked folder that is deleted and made again (a card pulled and put
    /// back, a sync tool replacing the folder) is found again and its new pictures show up.</summary>
    private static async Task LinkedFolderRecreated(string folder)
    {
        Settings.UseFolder(folder);
        string photos = Path.Combine(folder, "photos");
        Directory.CreateDirectory(photos);
        ImageOutput.SavePng(Pixel(), Path.Combine(photos, "first.png"));
        FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"), new CountingDirectoryReader());
        string source = FolderCaptures.AddFolder(photos);
        await FolderCaptures.RefreshFolderAsync(source);
        if (FolderCaptures.LinkedItems(source).Count != 1) throw new InvalidOperationException("The linked folder's first picture was not listed.");
        Directory.Delete(photos, true);
        await Task.Delay(1500);
        Directory.CreateDirectory(photos);
        ImageOutput.SavePng(Pixel(), Path.Combine(photos, "second.png"));
        ImageOutput.SavePng(Pixel(), Path.Combine(photos, "third.png"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 90 && FolderCaptures.LinkedItems(source).Count != 2)
            await Task.Delay(100);
        double seconds = watch.Elapsed.TotalSeconds;
        FolderCaptures.RemoveFolder(source);
        if (seconds > 10)
            throw new InvalidOperationException($"A linked folder that was deleted and made again showed its new picture after {seconds:F0} seconds (90 means never).");
    }

    private sealed class OverflowWatcher(string folder) : FileSystemWatcher(folder)
    {
        public void Overflow() => OnError(new ErrorEventArgs(new InternalBufferOverflowException()));
    }

    private static async Task FolderOverflow(string folder)
    {
        Settings.UseFolder(folder);
        string photos = Path.Combine(folder, "photos");
        Directory.CreateDirectory(photos);
        using var watcher = new OverflowWatcher(photos);
        FolderCaptures.WatcherFactory = _ => watcher;
        FolderCaptures.UseFolder(photos);
        _ = FolderCaptures.Items();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged() => changed.TrySetResult();
        FolderCaptures.Changed += OnChanged;
        try
        {
            watcher.Overflow();
            await Task.WhenAny(changed.Task, Task.Delay(2000));
            if (!changed.Task.IsCompleted) throw new InvalidOperationException("A filesystem watcher overflow did not refresh the collection.");
        }
        finally { FolderCaptures.Changed -= OnChanged; }
    }

    private static async Task ContinuousFolderEvents(string folder)
    {
        Settings.UseFolder(folder);
        string photos = Path.Combine(folder, "photos");
        Directory.CreateDirectory(photos);
        FolderCaptures.UseFolder(photos);
        _ = FolderCaptures.Items();
        await Task.Delay(500);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged() => changed.TrySetResult();
        FolderCaptures.Changed += OnChanged;
        try
        {
            var writes = Task.Run(async () =>
            {
                for (int n = 0; n < 65; n++)
                {
                    File.WriteAllText(Path.Combine(photos, "incoming.txt"), n.ToString());
                    await Task.Delay(50);
                }
            });
            await Task.WhenAny(changed.Task, Task.Delay(2500));
            bool deliveredWhileChanging = changed.Task.IsCompleted;
            await writes;
            if (!deliveredWhileChanging)
                throw new InvalidOperationException("Continuous folder writes starved library change notifications beyond two seconds.");
        }
        finally { FolderCaptures.Changed -= OnChanged; }
    }

    private static async Task VideoDragResponsive(string folder, StringBuilder log)
    {
        Settings.UseFolder(folder);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        CaptureLibrary.BackgroundWork = false;
        VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
        string clip = Path.Combine(folder, "large.mp4");
        using (var stream = File.Create(clip)) stream.SetLength(200L * 1024 * 1024);
        await Task.Run(() => CaptureLibrary.AddVideo(clip, DateTime.Now, "Large recording"));
        var item = CaptureLibrary.Items().Single();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Task<string?> preparation = ImageDrag.PrepareFileAsync(item);
        double longest = clock.Elapsed.TotalMilliseconds;
        int pings = 0;
        while (!preparation.IsCompleted)
        {
            clock.Restart();
            await Task.Delay(10);
            longest = Math.Max(longest, clock.Elapsed.TotalMilliseconds);
            pings++;
        }
        string? file = await preparation;
        log.AppendLine($"drag_dispatcher_pings={pings} longest_ms={longest:F1}");
        if (file == null || new FileInfo(file).Length != 200L * 1024 * 1024 || longest >= 100)
            throw new InvalidOperationException($"Preparing a recording drag blocked the dispatcher for {longest:F1} ms.");
        string destination = Path.Combine(folder, "exported.mp4");
        var export = Task.Run(() => CaptureLibrary.ExportVideo(item.Id, destination, item.Session));
        longest = 0;
        pings = 0;
        while (!export.IsCompleted)
        {
            clock.Restart();
            await Task.Delay(10);
            _ = CaptureLibrary.Count;
            longest = Math.Max(longest, clock.Elapsed.TotalMilliseconds);
            pings++;
        }
        await export;
        log.AppendLine($"export_dispatcher_and_read_pings={pings} longest_ms={longest:F1}");
        if (new FileInfo(destination).Length != 200L * 1024 * 1024 || pings == 0 || longest >= 100)
            throw new InvalidOperationException($"Exporting a recording blocked dispatcher queries for {longest:F1} ms.");
        CaptureLibrary.Close();
    }

    private static async Task MissingImageDrag(string folder)
    {
        Settings.UseFolder(folder);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        CaptureLibrary.BackgroundWork = false;
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
        var missing = new CaptureItem(999999, DateTime.Now, 1, 1, "region", "Deleted image", null, null, null,
            Session: CaptureLibrary.Session);
        await ImageDrag.StartCaptureAsync(missing);
        long corrupt = CaptureLibrary.AddNow(Pixel(), DateTime.Now, "region", "Corrupt image", null);
        using (var fixture = OpenFixtureDatabase())
        using (var damage = fixture.CreateCommand())
        {
            damage.CommandText = "UPDATE captures SET image=X'010203' WHERE id=$id";
            damage.Parameters.AddWithValue("$id", corrupt);
            damage.ExecuteNonQuery();
        }
        await ImageDrag.StartCaptureAsync(CaptureLibrary.Items().Single());
        CaptureLibrary.Close();
    }

    private static async Task RecordingCleanupWarning(string folder)
    {
        Settings.UseFolder(Path.Combine(folder, "settings"));
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        CaptureLibrary.BackgroundWork = false;
        string recordings = Path.Combine(folder, "recordings");
        Directory.CreateDirectory(recordings);
        FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
        FolderCaptures.RecordingsFolderOverride = recordings;
        App.InitializeKit();
        LocaleManager.Apply(Locale.EnUS);
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
        string clip = Path.Combine(recordings, "retained.mp4");
        File.WriteAllBytes(clip, [2, 4, 6]);
        File.SetAttributes(clip, FileAttributes.ReadOnly);
        var anchor = new System.Windows.Controls.Button();
        var window = new Window { Content = anchor, Width = 100, Height = 100, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            await FolderCaptures.RefreshFolderAsync(FolderCaptures.RecordingsSource);
            var item = FolderCaptures.LinkedItems(FolderCaptures.RecordingsSource).Single();
            var view = new LibraryView();
            var message = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.StatusMessage += text => message.TrySetResult(text);
            window.Show();
            view.ShowItemMenu(item, anchor, null, "Del");
            var menu = PresentationSource.CurrentSources.Cast<PresentationSource>().Where(source => source.RootVisual != null)
                .SelectMany(source => Visuals(source.RootVisual)).OfType<System.Windows.Controls.ContextMenu>().Single(candidate => candidate.IsOpen);
            menu.IsOpen = false;
            menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(entry => Equals(entry.Header,
                LocaleManager.Loc("Str_Rec_MoveToLibrary"))).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            string status = await message.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!File.Exists(clip) || CaptureLibrary.Items().Count != 1)
                throw new InvalidOperationException("The cleanup failure did not preserve the committed copy and original file.");
            if (!status.Contains(clip, StringComparison.Ordinal) || !status.Contains("unencrypted", StringComparison.Ordinal))
                throw new InvalidOperationException("Moving a recording reported success without identifying the retained plaintext file: " + status);
        }
        finally
        {
            window.Close();
            File.SetAttributes(clip, FileAttributes.Normal);
            CaptureLibrary.Close();
        }
        static IEnumerable<DependencyObject> Visuals(DependencyObject root)
        {
            yield return root;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
    }

    private static async Task VideoPlayResponsive(string folder, StringBuilder log)
    {
        Settings.UseFolder(folder);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        CaptureLibrary.BackgroundWork = false;
        VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
        string clip = Path.Combine(folder, "large.mp4");
        using (var stream = File.Create(clip)) stream.SetLength(200L * 1024 * 1024);
        await Task.Run(() => CaptureLibrary.AddVideo(clip, DateTime.Now, "First playback"));
        var item = CaptureLibrary.Items().Single();
        string? opened = null;
        bool openedOnDispatcher = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Task playback = VideoCaptures.PlayAsync(item, openPlayer: path =>
        {
            opened = path;
            openedOnDispatcher = Application.Current.Dispatcher.CheckAccess();
        });
        double longest = clock.Elapsed.TotalMilliseconds;
        int pings = 0;
        while (!playback.IsCompleted)
        {
            clock.Restart();
            await Task.Delay(10);
            longest = Math.Max(longest, clock.Elapsed.TotalMilliseconds);
            pings++;
        }
        await playback;
        log.AppendLine($"first_play_dispatcher_pings={pings} longest_ms={longest:F1}");
        if (opened == null || new FileInfo(opened).Length != 200L * 1024 * 1024 || !openedOnDispatcher || longest >= 100)
            throw new InvalidOperationException($"First recording playback blocked the dispatcher for {longest:F1} ms or opened off its dispatcher.");
        string? failure = null;
        bool reportedOnDispatcher = false;
        await VideoCaptures.PlayAsync(item, message =>
        {
            failure = message;
            reportedOnDispatcher = Application.Current.Dispatcher.CheckAccess();
        }, path =>
        {
            if (path != opened) throw new InvalidOperationException("Playback did not reuse its decrypted copy.");
            throw new IOException("Synthetic player failure");
        });
        if (failure != "Synthetic player failure" || !reportedOnDispatcher || Directory.GetFiles(VideoCaptures.PlaybackFolder).Length != 1)
            throw new InvalidOperationException("Playback lost its cached copy or UI-thread error report.");
        CaptureLibrary.Close();
    }

    private static async Task DecodeResponsive(string folder, StringBuilder log)
    {
        Settings.UseFolder(folder);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        CaptureLibrary.BackgroundWork = false;
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
        var pixels = new byte[9000 * 9000 * 4];
        var image = BitmapSource.Create(9000, 9000, 96, 96, PixelFormats.Bgra32, null, pixels, 9000 * 4);
        image.Freeze();
        CaptureLibrary.AddNow(image, DateTime.Now, "region", "Large image", null);
        var item = CaptureLibrary.Items().Single();
        var decode = Task.Run(() => CaptureLibrary.LoadDocument(item));
        double longest = 0;
        int pings = 0;
        while (!decode.IsCompleted)
        {
            await Task.Delay(5);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _ = CaptureLibrary.Count;
            longest = Math.Max(longest, clock.Elapsed.TotalMilliseconds);
            pings++;
        }
        var document = await decode;
        log.AppendLine($"decode_dispatcher_pings={pings} longest_ms={longest:F1}");
        if (document.Original.PixelWidth != 9000 || pings == 0 || longest >= 100)
            throw new InvalidOperationException($"Full image decoding blocked a dispatcher query for {longest:F1} ms.");
        CaptureLibrary.Close();
    }
}
