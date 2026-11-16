using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// Make PDF plaintext lifetime (SEC-001) and same-ID album race (SEC-U1). Everything goes through
/// MakePdfFromCollectionAsync and the real close, switch and password-change paths; only the
/// KillerPDF process start is replaced. A consumer is stood in for by a FileStream that denies
/// delete sharing.
/// </summary>
internal static partial class SelfTestMakePdf
{
    private static BitmapSource Solid(byte value)
    {
        var pixels = Enumerable.Repeat(value, 4 * 4 * 4).ToArray();
        var image = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        image.Freeze();
        return image;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> done, int milliseconds = 6000)
    {
        for (var clock = Stopwatch.StartNew(); clock.ElapsedMilliseconds < milliseconds; await Task.Delay(40))
            if (done()) return true;
        return done();
    }

    private static string[] PdfFolders(string playback) =>
        Directory.Exists(playback) ? Directory.GetDirectories(playback, "PDF *") : [];

    private static async Task RetirementAsync(string folder, Action<bool, string> check, List<string> report)
    {
        string playback = Path.Combine(folder, "playback");
        var interval = VideoCaptures.PlaybackRetryInterval;
        VideoCaptures.PlaybackRetryInterval = TimeSpan.FromMilliseconds(200);
        ProcessStartInfo? started = null;
        KillerPdfHandoff.StartOverride = info => started = info;
        int round = 0;
        try
        {
            if (!CaptureLibrary.IsOpen && !CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch library did not reopen.");
            string unowned = Path.Combine(playback, "PDF unowned by this export");
            Directory.CreateDirectory(unowned);

            async Task<string> HandoffAsync()
            {
                round++;
                long a = CaptureLibrary.AddNow(Solid(0x10), DateTime.Now, "region", "h" + round + "a", null);
                long b = CaptureLibrary.AddNow(Solid(0x20), DateTime.Now.AddSeconds(1), "region", "h" + round + "b", null);
                long session = CaptureLibrary.Session;
                long collection = CaptureLibrary.CreateCollection("Round " + round, [a, b], session);
                started = null;
                var made = await KillerPdfHandoff.MakePdfFromCollectionAsync(collection, session);
                check(made == KillerPdfHandoff.Result.Started && started != null, $"round {round}: the handoff starts");
                return started?.WorkingDirectory ?? "";
            }

            async Task HeldThenRetired(string name, Action retire)
            {
                string copies = await HandoffAsync();
                bool had = File.Exists(Path.Combine(copies, "0001.png"));
                using (new FileStream(Path.Combine(copies, "0001.png"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    retire();
                    await Task.Delay(700);
                    check(had && Directory.Exists(copies), name + ": the held copy cannot go while the consumer holds it, so the check means something");
                }
                bool gone = await WaitUntilAsync(() => !Directory.Exists(copies));
                check(gone, name + ": the plaintext folder is deleted soon after the consumer lets go, without a restart");
                report.Add($"{name}: folder gone after release = {gone}");
                check(Directory.Exists(unowned), name + ": a folder this export does not own is left alone");
            }

            await HeldThenRetired("close", () => CaptureLibrary.Close());
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch library did not reopen after close.");

            string other = CaptureLibrary.CreateAlbum("Switch target");
            await HeldThenRetired("switch", () => check(CaptureLibrary.TrySwitchAlbum(other, null, out _), "switch: the other album opens"));

            // Repeated cleanup of a session that is already clean does nothing and does not throw.
            bool repeated = true;
            try { VideoCaptures.ForgetPlayback(CaptureLibrary.Session); VideoCaptures.ForgetPlayback(CaptureLibrary.Session); }
            catch (Exception) { repeated = false; }
            check(repeated && PdfFolders(playback).Length == 1, "repeated cleanup is harmless and keeps to owned folders");

            // An export still writing while the album switches away: no crash, no launch, no folder left.
            {
                string back = CaptureLibrary.CreateAlbum("Switch target two");
                long a = CaptureLibrary.AddNow(Solid(0x30), DateTime.Now, "region", "w1", null);
                long b = CaptureLibrary.AddNow(Solid(0x31), DateTime.Now.AddSeconds(1), "region", "w2", null);
                long session = CaptureLibrary.Session;
                long collection = CaptureLibrary.CreateCollection("Writing", [a, b], session);
                using var paused = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                int loads = 0;
                KillerPdfHandoff.ExportHook = (stage, _) =>
                {
                    if (stage == "before-load" && Interlocked.Increment(ref loads) == 2) { paused.Set(); release.Wait(10000); }
                };
                started = null;
                var before = PdfFolders(playback).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var task = KillerPdfHandoff.MakePdfFromCollectionAsync(collection, session);
                check(await Task.Run(() => paused.Wait(10000)), "writing: the export pauses between two pictures");
                string? writing = PdfFolders(playback).FirstOrDefault(f => !before.Contains(f));
                check(CaptureLibrary.TrySwitchAlbum(back, null, out _), "writing: the album switches away mid-export");
                release.Set();
                KillerPdfHandoff.Result? result = null; Exception? thrown = null;
                try { result = await task; } catch (Exception error) { thrown = error; }
                KillerPdfHandoff.ExportHook = null;
                report.Add($"writing: result={result} thrown={thrown?.GetType().Name} launched={started != null}");
                check(thrown == null && result != KillerPdfHandoff.Result.Started && started == null, "writing: a retired export ends with a message and never reaches KillerPDF");
                check(writing != null && await WaitUntilAsync(() => !Directory.Exists(writing)), "writing: the partial plaintext folder is gone");
            }

            // A handoff that does not start leaves nothing behind either.
            {
                KillerPdfHandoff.StartOverride = _ => throw new InvalidOperationException("launch failed");
                long a = CaptureLibrary.AddNow(Solid(0x40), DateTime.Now, "region", "f1", null);
                long session = CaptureLibrary.Session;
                long collection = CaptureLibrary.CreateCollection("Failing", [a], session);
                var before = PdfFolders(playback).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var failed = await KillerPdfHandoff.MakePdfFromCollectionAsync(collection, session);
                check(failed == KillerPdfHandoff.Result.Failed && PdfFolders(playback).All(before.Contains), "a failed launch deletes its plaintext folder");
                KillerPdfHandoff.StartOverride = info => started = info;
            }

            await HeldThenRetired("password change", () => CaptureLibrary.SetPassword("retirement fixture password"));
        }
        finally
        {
            KillerPdfHandoff.ExportHook = null;
            VideoCaptures.PlaybackRetryInterval = interval;
        }
    }

    /// <summary>
    /// SEC-U1: two albums hold different pictures under the same capture ID. The export pauses
    /// after its album check and before it reads the picture; the album switches; the export
    /// resumes. Records what was loaded, how long plaintext lived, what stayed, and any launch.
    /// </summary>
    private static async Task SameIdRaceAsync(string folder, Action<bool, string> check, List<string> report, bool hold)
    {
        string playback = Path.Combine(folder, "playback");
        string tag = hold ? "held race" : "race";
        ProcessStartInfo? started = null;
        KillerPdfHandoff.StartOverride = info => started = info;
        var interval = VideoCaptures.PlaybackRetryInterval;
        VideoCaptures.PlaybackRetryInterval = TimeSpan.FromMilliseconds(200);
        FileStream? holder = null;
        try
        {
            string pathA = CaptureLibrary.CreateAlbum(tag + " A");
            string pathB = CaptureLibrary.CreateAlbum(tag + " B");
            check(CaptureLibrary.TrySwitchAlbum(pathB, null, out _), "race: album B opens");
            long idB = CaptureLibrary.AddNow(Solid(0x22), DateTime.Now, "region", "B", null);
            byte[] bytesB = CaptureLibrary.LoadOutputPng(idB);
            check(CaptureLibrary.TrySwitchAlbum(pathA, null, out _), "race: album A opens");
            long idA = CaptureLibrary.AddNow(Solid(0x11), DateTime.Now, "region", "A", null);
            byte[] bytesA = CaptureLibrary.LoadOutputPng(idA);
            check(idA == idB && !bytesA.SequenceEqual(bytesB), "race: both albums hold a different picture under the same capture ID");
            long session = CaptureLibrary.Session;
            long collection = CaptureLibrary.CreateCollection("Race", [idA], session);

            using var paused = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var written = new List<string>();
            var lifetime = new Stopwatch();
            KillerPdfHandoff.ExportHook = (stage, file) =>
            {
                if (stage == "before-load")
                {
                    // A consumer that already has the picture's file open without delete sharing,
                    // so retirement cannot remove the folder and what the export writes stays visible.
                    if (hold) { File.Create(file).Dispose(); holder = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
                    paused.Set(); release.Wait(10000);
                }
                else
                {
                    byte[] got = File.ReadAllBytes(file);
                    written.Add(got.SequenceEqual(bytesB) ? "B" : got.SequenceEqual(bytesA) ? "A" : "other");
                    lifetime.Start();
                }
            };
            var before = PdfFolders(playback).ToHashSet(StringComparer.OrdinalIgnoreCase);
            started = null;
            var task = KillerPdfHandoff.MakePdfFromCollectionAsync(collection, session);
            check(await Task.Run(() => paused.Wait(10000)), "race: the export pauses after its album check");
            check(CaptureLibrary.TrySwitchAlbum(pathB, null, out _), "race: the album switches while the export is paused");
            release.Set();
            KillerPdfHandoff.Result? result = null; Exception? thrown = null;
            try { result = await task; } catch (Exception error) { thrown = error; }
            KillerPdfHandoff.ExportHook = null;
            if (hold) await Task.Delay(700);
            int leftWhileHeld = PdfFolders(playback).Count(f => !before.Contains(f));
            holder?.Dispose(); holder = null;
            bool cleaned = await WaitUntilAsync(() => PdfFolders(playback).All(before.Contains));
            if (lifetime.IsRunning) lifetime.Stop();
            report.Add($"{tag}: folders-while-held={leftWhileHeld}; {tag}: loaded=[{string.Join(",", written)}] result={result} thrown={thrown?.GetType().Name} launches={(started != null ? 1 : 0)} " +
                $"folders-left={PdfFolders(playback).Count(f => !before.Contains(f))} cleaned={cleaned} plaintext-ms~{lifetime.ElapsedMilliseconds}");
            check(!written.Contains("B"), tag + ": a switch between the album check and the read never loads the other album's same-ID picture");
            check(started == null && thrown == null, tag + ": the stale export never launches KillerPDF and does not throw");
            check(cleaned, tag + ": no plaintext folder remains");
        }
        finally
        {
            KillerPdfHandoff.ExportHook = null;
            KillerPdfHandoff.StartOverride = null;
            VideoCaptures.PlaybackRetryInterval = interval;
        }
    }
}
