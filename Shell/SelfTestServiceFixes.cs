using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Capture;
using KillerShot.Editor;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-service-fixes &lt;folder&gt;. Covers the
/// album and service review findings (AL-, SV-, SX-) one check per finding, each on its own
/// scratch album inside the folder. Settings, the album, playback copies and the Screenshots
/// folder all live in the folder, so the user's own album and settings are never touched.
/// report.txt lists each finding as pass or FAIL.</summary>
internal static class SelfTestServiceFixes
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-service-fixes") return false;
        string dir = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(dir);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(dir, "native"));
        Settings.UseFolder(Path.Combine(dir, "settings"));
        VideoCaptures.UsePlaybackFolder(Path.Combine(dir, "playback"));
        CaptureLibrary.BackgroundWork = false;
        _ = RunAsync(dir);
        return true;
    }

    private static async Task RunAsync(string dir)
    {
        var log = new StringBuilder();
        int failures = 0;
        async Task Check(string id, Func<Task> body)
        {
            // Every check starts from the same quiet state, so one failure cannot leak into the next.
            CaptureLibrary.BackgroundWork = false;
            CaptureLibrary.VideoChunkWrittenForSelfTest = null;
            CaptureLibrary.RenameFaultForSelfTest = null;
            CaptureLibrary.ImportCopiedForSelfTest = null;
            Settings.Set("ActiveAlbum", "Library.kshot");
            CaptureLibrary.UseFolder(Path.Combine(dir, "lib-" + id));
            try
            {
                await body();
                log.AppendLine($"{id}=pass");
            }
            catch (Exception error)
            {
                failures++;
                log.AppendLine($"{id}=FAIL {error.GetType().Name}: {error.Message}");
            }
            finally
            {
                CaptureLibrary.BackgroundWork = false;
                try { CaptureLibrary.Close(); } catch { }
            }
        }
        Task Sync(Action body) { body(); return Task.CompletedTask; }

        await Check("SV-004", () => Sync(RegionPresetsSurviveBadEntry));
        await Check("SV-003", () => Sync(UndefinedEnumsFallBack));
        await Check("AL-003", () => Sync(KeepDaysClamped));
        await Check("AL-002", () => Sync(NewerSchemaRefused));
        await Check("AL-005", () => Sync(SaveMarksOnDeletedCapture));
        await Check("AL-008", () => Sync(InterruptedKeyAlbumsAreAccountKeyed));
        await Check("SX-003", () => Task.Run(() => AddVideoPrunes(dir)));
        await Check("SX-001", () => Task.Run(() => PrunedRecordingLosesPlayback(dir)));
        await Check("SV-006", () => Task.Run(() => HeldPlaybackCopyGoesWhenReleased(dir)));
        await Check("AL-001", () => Task.Run(() => VideoImportLeavesAlbumWritable(dir)));
        await Check("AL-007", () => Task.Run(StaleOcrIsDropped));
        await Check("AL-006", () => Sync(RenameRollbackKeepsAlbum));
        await Check("AL-009", () => Task.Run(() => ImportCopiesOutsideGate(dir)));
        await Check("AL-004", () => Task.Run(SwitchAsksWithoutLocks));
        await Check("SV-001", () => Sync(() => WatcherRebuiltAfterError(dir)));

        log.AppendLine(failures == 0 ? "result=ok" : $"result=failed {failures}");
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
        Application.Current.Shutdown(failures == 0 ? 0 : 1);
    }

    // ---- SV-004: one malformed saved region must not erase the others ----

    private static void RegionPresetsSurviveBadEntry()
    {
        Settings.Set("RegionPresets", """[{"n":"A","w":10,"h":10},{"n":"Bad","w":"x","h":5},{"n":"C","w":20,"h":20,"x":1,"y":2}]""");
        RegionMemory.SavePreset(new RegionPreset("D", null, 30, 30));
        string names = string.Join(",", RegionMemory.Presets.Select(p => p.Name));
        Require(names == "A,C,D", $"Saved regions after one bad entry were '{names}', expected A,C,D.");
        RegionMemory.RemovePreset("A");
        names = string.Join(",", RegionMemory.Presets.Select(p => p.Name));
        Require(names == "C,D", $"Saved regions after a remove were '{names}', expected C,D.");

        // A list that does not parse at all is left alone for the user to repair, never overwritten.
        const string broken = "[{\"n\":\"A\",\"w\":10";
        Settings.Set("RegionPresets", broken);
        RegionMemory.SavePreset(new RegionPreset("E", null, 30, 30));
        Require(Settings.Get("RegionPresets") == broken, "An unreadable saved-region list was overwritten.");
        Settings.Set("RegionPresets", "");
    }

    // ---- SV-003: numbers in the settings file never become undefined enum values ----

    private static void UndefinedEnumsFallBack()
    {
        Settings.Set("VideoSound", "99");
        Require(Enum.IsDefined(VideoSettings.Sound), $"VideoSound 99 read as undefined {(int)VideoSettings.Sound}.");
        Settings.Set("LastMode", "42");
        Require(Enum.IsDefined(CaptureSettings.LastMode), $"LastMode 42 read as undefined {(int)CaptureSettings.LastMode}.");
        Settings.Set("LastMode", "Window");
        Require(CaptureSettings.LastMode == CaptureMode.Window, "LastMode Window no longer reads back.");
    }

    // ---- AL-003: an absurd day count cannot reach DateTime arithmetic ----

    private static void KeepDaysClamped()
    {
        var (kind, amount) = CaptureLibrary.ParseKeep("days:2147483647");
        Require(kind == "days" && amount <= 36500, $"days:2147483647 parsed as {kind}:{amount}.");
        Require(CaptureLibrary.ParseKeep("days:30") == ("days", 30), "days:30 no longer parses.");
        _ = DateTime.UtcNow.AddDays(-amount);
    }

    // ---- AL-002: an album from a newer KillerShot is refused and left as it is ----

    private static void NewerSchemaRefused()
    {
        Open();
        string key = CaptureLibrary.CurrentKeyForSelfTest() ?? throw new InvalidOperationException("No key.");
        CaptureLibrary.Close();
        CaptureLibrary.RawMetaForSelfTest(key, "schema", "99");
        bool opened = CaptureLibrary.OpenAuto();
        CaptureLibrary.Close();
        string? stored = CaptureLibrary.RawMetaForSelfTest(key, "schema");
        Require(!opened, "An album with schema 99 opened.");
        Require(CaptureLibrary.LoadError != null, "A refused newer album gave no reason.");
        Require(stored == "99", $"The newer album's schema was rewritten to {stored}.");
        CaptureLibrary.RawMetaForSelfTest(key, "schema", "1");
        Require(CaptureLibrary.OpenAuto(), "An older album no longer opens: " + CaptureLibrary.LoadError);
        CaptureLibrary.Close();
        Require(CaptureLibrary.RawMetaForSelfTest(key, "schema") is "4", "An older album was not upgraded to the current schema.");
    }

    // ---- AL-005: marks on a capture deleted meanwhile give false, not an exception ----

    private static void SaveMarksOnDeletedCapture()
    {
        Open();
        var original = Picture(32, 32, 0x40);
        long id = CaptureLibrary.AddNow(original, DateTime.Now, "region", "marks", null);
        CaptureLibrary.Delete([id]);
        var edited = Picture(32, 32, 0x80);
        var state = new EditorState(edited, ImmutableList<Annotation>.Empty);
        bool saved;
        try { saved = CaptureLibrary.SaveMarks(id, state, original, edited); }
        catch (Exception error) { throw new InvalidOperationException("SaveMarks threw " + error.GetType().Name + ": " + error.Message); }
        Require(!saved, "SaveMarks reported success on a deleted capture.");
    }

    // ---- AL-008: an album mid key change still lists as account-keyed ----

    private static void InterruptedKeyAlbumsAreAccountKeyed()
    {
        Open();
        string folder = Path.GetDirectoryName(CaptureLibrary.FilePath)!;
        File.WriteAllBytes(Path.Combine(folder, "Retiring.kshot"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "Retiring.key.retire"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "Creating.kshot"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "Creating.key.creating"), [1]);
        // A retired key beside a backup means the password swap already happened, so the album
        // holds the new password and the retired key only opens the backup.
        File.WriteAllBytes(Path.Combine(folder, "Swapped.kshot"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "Swapped.key.retire"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "Swapped.kshot.bak"), [1]);
        var albums = CaptureLibrary.Albums();
        foreach (string name in new[] { "Retiring", "Creating" })
            Require(albums.Single(a => a.Name == name) is { PasswordProtected: false }, $"{name} listed as password protected.");
        Require(albums.Single(a => a.Name == "Swapped") is { PasswordProtected: true }, "Swapped listed as unprotected beside a retired key and its backup.");
    }

    // ---- SX-003: a stored recording obeys the history limit ----

    private static void AddVideoPrunes(string dir)
    {
        Open();
        CaptureLibrary.SetAlbumSetting("LibraryKeep", "count:2");
        DateTime start = DateTime.Now.AddHours(-3);
        for (int i = 0; i < 3; i++)
            CaptureLibrary.AddVideo(FakeClip(dir, "keep" + i, 4096), start.AddMinutes(i), "clip" + i);
        int count = CaptureLibrary.Items().Count;
        Require(count == 2, $"count:2 kept {count} recordings.");
    }

    // ---- SX-001: a pruned recording's decrypted playback copy goes with it ----

    private static void PrunedRecordingLosesPlayback(string dir)
    {
        Open();
        long video = CaptureLibrary.AddVideo(FakeClip(dir, "played", 4096), DateTime.Now.AddHours(-1), "played");
        string copy = VideoCaptures.DecryptForPlayback(video);
        Require(File.Exists(copy), "No playback copy was made.");
        CaptureLibrary.AddNow(Picture(16, 16, 0x20), DateTime.Now, "region", "newer", null);
        CaptureLibrary.SetAlbumSetting("LibraryKeep", "count:1");
        CaptureLibrary.AddNow(Picture(16, 16, 0x30), DateTime.Now.AddSeconds(1), "region", "newest", null);
        Require(CaptureLibrary.Items().All(item => item.Id != video), "The recording was not pruned.");
        var wait = Stopwatch.StartNew();
        while (File.Exists(copy) && wait.Elapsed < TimeSpan.FromSeconds(3)) Thread.Sleep(50);
        Require(!File.Exists(copy), "The pruned recording's playback copy is still on disk.");
    }

    // ---- SV-006: a playback copy a player held open is deleted once the player lets go ----

    private static void HeldPlaybackCopyGoesWhenReleased(string dir)
    {
        Open();
        var interval = VideoCaptures.PlaybackRetryInterval;
        VideoCaptures.PlaybackRetryInterval = TimeSpan.FromMilliseconds(200);
        try
        {
            long video = CaptureLibrary.AddVideo(FakeClip(dir, "held", 4096), DateTime.Now, "held");
            string copy = VideoCaptures.DecryptForPlayback(video);
            // A player opens the file without delete sharing, so the first delete fails.
            using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                CaptureLibrary.Delete([video]);
                Thread.Sleep(300);
                Require(File.Exists(copy), "The held copy vanished while the player held it, so this check proves nothing.");
            }
            var wait = Stopwatch.StartNew();
            while (File.Exists(copy) && wait.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(50);
            Require(!File.Exists(copy), "The decrypted copy stayed on disk after the player let go of it.");
        }
        finally { VideoCaptures.PlaybackRetryInterval = interval; }
    }

    // ---- AL-001: a long recording import never holds the album's write lock ----

    private static void VideoImportLeavesAlbumWritable(string dir)
    {
        Open();
        long picture = CaptureLibrary.AddNow(Picture(16, 16, 0x50), DateTime.Now, "region", "note target", null);
        TimeSpan? noteTime = null;
        Task? note = null;
        CaptureLibrary.VideoChunkWrittenForSelfTest = written =>
        {
            if (note != null) return;
            var timer = Stopwatch.StartNew();
            note = Task.Run(() => CaptureLibrary.SetNote(picture, "during import"));
            if (note.Wait(TimeSpan.FromSeconds(3))) noteTime = timer.Elapsed;
        };
        long video = CaptureLibrary.AddVideo(FakeClip(dir, "long", 12 << 20), DateTime.Now.AddMinutes(-5), "long");
        CaptureLibrary.VideoChunkWrittenForSelfTest = null;
        note?.Wait(TimeSpan.FromSeconds(40));
        Require(note != null, "The import never reported a written piece.");
        Require(noteTime != null, "A note edit waited more than 3 s behind a recording import.");
        Require(CaptureLibrary.Items().Any(item => item.Id == video), "The imported recording is missing.");
        Require(CaptureLibrary.Items("during").Any(item => item.Id == picture), "The note written during the import was lost.");
        string back = Path.Combine(dir, "long-back.mp4");
        CaptureLibrary.ExportVideo(video, back);
        Require(File.ReadAllBytes(back).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "clips", "long.mp4"))),
            "The imported recording's bytes differ from the source.");
        File.Delete(back);
    }

    // ---- AL-007: text read before a reindex is not stored after it ----

    private static async Task StaleOcrIsDropped()
    {
        Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int engines = 0;
        var previous = CaptureLibrary.OcrEngineFactory;
        CaptureLibrary.OcrEngineFactory = () =>
        {
            int engine = Interlocked.Increment(ref engines);
            return async _ =>
            {
                if (engine > 1) return "freshwordq";
                entered.TrySetResult();
                await release.Task;
                return "stalewordq";
            };
        };
        try
        {
            CaptureLibrary.BackgroundWork = true;
            CaptureLibrary.AddNow(Picture(16, 16, 0x60), DateTime.Now, "region", "ocr", null);
            if (await Task.WhenAny(entered.Task, Task.Delay(5000)) != entered.Task) throw new TimeoutException("OCR never started.");
            CaptureLibrary.ReindexOcr();
            release.TrySetResult();
            var wait = Stopwatch.StartNew();
            while (CaptureLibrary.Items("freshwordq").Count == 0 && CaptureLibrary.Items("stalewordq").Count == 0
                && wait.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
            await Task.Delay(300);
            Require(CaptureLibrary.Items("stalewordq").Count == 0, "Text read before the reindex was stored after it.");
            Require(CaptureLibrary.Items("freshwordq").Count == 1, "The capture was not read again after the reindex.");
        }
        finally
        {
            release.TrySetResult();
            CaptureLibrary.BackgroundWork = false;
            CaptureLibrary.OcrEngineFactory = previous;
        }
    }

    // ---- AL-006: a rename that fails at its last step leaves the album where it was, open ----

    private static void RenameRollbackKeepsAlbum()
    {
        Open();
        CaptureLibrary.AddNow(Picture(16, 16, 0x70), DateTime.Now, "region", "kept", null);
        string original = CaptureLibrary.FilePath;
        string renamed = Path.Combine(Path.GetDirectoryName(original)!, "Renamed.kshot");
        CaptureLibrary.RenameFaultForSelfTest = () => throw new IOException("injected rename fault");
        try { CaptureLibrary.RenameAlbum(original, "Renamed"); throw new InvalidOperationException("The faulted rename succeeded."); }
        catch (IOException error) when (error.Message == "injected rename fault") { }
        finally { CaptureLibrary.RenameFaultForSelfTest = null; }
        Require(string.Equals(CaptureLibrary.FilePath, original, StringComparison.OrdinalIgnoreCase), $"The active album is now {CaptureLibrary.FilePath}.");
        Require(File.Exists(original) && !File.Exists(renamed), "The album file did not move back.");
        Require(CaptureLibrary.IsOpen, "The album is closed after the rollback.");
        Require(Settings.Get("ActiveAlbum") == "Library.kshot", $"ActiveAlbum is {Settings.Get("ActiveAlbum")}.");
        CaptureLibrary.AddNow(Picture(16, 16, 0x71), DateTime.Now, "region", "after", null);
        Require(CaptureLibrary.Items().Count == 2, "The album does not take captures after the rollback.");
    }

    // ---- AL-009: the album stays usable while an import copies another album ----

    private static void ImportCopiesOutsideGate(string dir)
    {
        string source = Path.Combine(dir, "lib-AL-009-source");
        CaptureLibrary.UseFolder(source);
        Open();
        CaptureLibrary.AddNow(Picture(16, 16, 0x11), DateTime.Now, "region", "source", null);
        CaptureLibrary.Close();
        CaptureLibrary.UseFolder(Path.Combine(dir, "lib-AL-009"));
        Open();
        bool? listed = null;
        CaptureLibrary.ImportCopiedForSelfTest = () =>
        {
            var items = Task.Run(() => CaptureLibrary.Items());
            listed = items.Wait(TimeSpan.FromSeconds(2));
        };
        Settings.Set("ActiveAlbum", "Library.kshot");
        string? imported = CaptureLibrary.ImportAlbum(Path.Combine(source, "Library.kshot"), null, out bool needsPassword);
        CaptureLibrary.ImportCopiedForSelfTest = null;
        Require(imported != null && !needsPassword && File.Exists(imported), "The album import did not complete.");
        Require(listed == true, listed == null ? "The import never reported its copy." : "Listing the open album waited behind an album import.");
        Require(CaptureLibrary.Albums().Count == 2, "The imported album is not listed.");
    }

    // ---- AL-004: the switch confirmation runs without the cross-process album locks ----

    private static void SwitchAsksWithoutLocks()
    {
        Open();
        string second = CaptureLibrary.CreateAlbum("Second");
        string folder = Path.GetDirectoryName(second)!;
        string? held = null;
        bool asked = false;
        bool switched = CaptureLibrary.TrySwitchAlbum(second, null, out bool needsPassword, () =>
        {
            asked = true;
            foreach (string lockFile in new[] { Path.Combine(folder, "albums.lock"), Path.ChangeExtension(second, ".open.lock") })
            {
                try { using var probe = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { held = Path.GetFileName(lockFile); }
            }
            return true;
        });
        Require(asked, "The switch never asked.");
        Require(held == null, $"The switch asked while holding {held}.");
        Require(switched && !needsPassword, "The switch did not complete.");
        Require(string.Equals(CaptureLibrary.FilePath, second, StringComparison.OrdinalIgnoreCase), "The second album is not active.");
    }

    // ---- SV-001: a watcher that reported an error is replaced on the next scan ----

    private sealed class FaultableWatcher(string path) : FileSystemWatcher(path)
    {
        public void Fail() => OnError(new ErrorEventArgs(new InternalBufferOverflowException()));
    }

    private static void WatcherRebuiltAfterError(string dir)
    {
        string shots = Path.Combine(dir, "shots");
        Directory.CreateDirectory(shots);
        var previous = FolderCaptures.WatcherFactory;
        var made = new List<FaultableWatcher>();
        try
        {
            FolderCaptures.UseFolder(shots);
            FolderCaptures.WatcherFactory = path => { var watcher = new FaultableWatcher(path); made.Add(watcher); return watcher; };
            FolderCaptures.Items();
            Require(made.Count == 1, $"The first scan made {made.Count} watchers.");
            made[0].Fail();
            FolderCaptures.Items();
            Require(made.Count == 2, "The watcher was not rebuilt after its error.");
        }
        finally
        {
            FolderCaptures.WatcherFactory = previous;
            FolderCaptures.UseFolder(shots);
        }
    }

    // ---- helpers ----

    private static void Open()
    {
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch album did not open: " + CaptureLibrary.LoadError);
    }

    private static string FakeClip(string dir, string name, int bytes)
    {
        string folder = Path.Combine(dir, "clips");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".mp4");
        var data = new byte[bytes];
        new Random(bytes ^ name.Length).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static BitmapSource Picture(int width, int height, byte shade)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = shade; pixels[i + 1] = (byte)(255 - shade); pixels[i + 2] = shade; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
