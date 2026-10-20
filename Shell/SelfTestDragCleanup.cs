using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestDragCleanup
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-drag-cleanup") return false;
        string folder = Path.GetFullPath(args[1]); Directory.CreateDirectory(folder);
        var report = new List<string>();
        int code = 1;
        try
        {
            string root = Path.Combine(folder, "KillerShot-Drag"); Directory.CreateDirectory(root);
            string[] existing = Directory.GetDirectories(root);
            string Create(bool old, string? name = null)
            {
                string session = Path.Combine(root, name ?? Guid.NewGuid().ToString("N")); Directory.CreateDirectory(session);
                string file = Path.Combine(session, "KillerShot " + Guid.NewGuid().ToString("N") + ".png");
                File.WriteAllBytes(file, [1, 2, 3]);
                if (old) { File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2)); Directory.SetLastWriteTimeUtc(session, DateTime.UtcNow.AddDays(-2)); }
                return session;
            }
            string abandoned = Create(true), recent = Create(false), live = Create(true), locked = Create(true), unrelated = Create(true, "unrelated");
            using var owner = new FileStream(Path.Combine(live, ".session.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            File.SetLastWriteTimeUtc(owner.Name, DateTime.UtcNow.AddDays(-2)); Directory.SetLastWriteTimeUtc(live, DateTime.UtcNow.AddDays(-2));
            using var consumer = File.Open(Directory.GetFiles(locked).Single(), FileMode.Open, FileAccess.Read, FileShare.Read);
            ImageDrag.Initialize(root);
            if (Directory.Exists(abandoned)) throw new InvalidOperationException("Startup retained an abandoned plaintext drag session older than 24 hours.");
            foreach (string keep in new[] { recent, live, locked, unrelated })
                if (!Directory.Exists(keep) || Directory.GetFiles(keep, "*.png").Length != 1) throw new InvalidOperationException("Cleanup removed an active, recent, locked or unrelated session.");
            report.Add("abandoned removed; recent, live, locked and unrelated sessions preserved PASS");
            owner.Dispose(); consumer.Dispose();
            ImageDrag.ReclaimAbandoned();
            if (Directory.Exists(live) || Directory.Exists(locked)) throw new InvalidOperationException("Released sessions were not reclaimed on the periodic sweep.");
            foreach (string keep in existing)
                if (!Directory.Exists(keep)) throw new InvalidOperationException("Cleanup traversed a supplied reparse fixture.");
            report.Add("periodic sweep retries released owners and consumers PASS");
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            using (var transfer = ImageDrag.CreateTransfer(image)) transfer.Completed = true;
            var current = Directory.GetDirectories(root).Except(new[] { recent, live, locked, unrelated }.Concat(existing)).Single();
            if (Directory.GetFiles(current, "*.png").Length != 1) throw new InvalidOperationException("Successful Drop did not retain its PNG for the consumer.");
            foreach (string file in Directory.GetFiles(current)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
            Directory.SetLastWriteTimeUtc(current, DateTime.UtcNow.AddDays(-2));
            ImageDrag.ReclaimAbandoned();
            if (!Directory.Exists(current)) throw new InvalidOperationException("The periodic sweep removed the current session.");
            ImageDrag.Cleanup();
            if (Directory.Exists(current)) throw new InvalidOperationException("Normal exit retained its drag session.");
            report.Add("successful Drop keeps PNG until normal exit PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "drag-cleanup-report.txt"), report);
        App.Instance.Shutdown(code);
        return true;
    }
}
