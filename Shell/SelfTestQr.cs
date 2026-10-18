using System.IO;
using KillerShot.Services;
using KillerTools.Qr;

namespace KillerShot.Shell;

internal static class SelfTestQr
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] != "--selftest-qr") return false;
        int code = 1;
        string? report = null;
        try
        {
            if (args.Length is not (3 or 4)) throw new ArgumentException("Use --selftest-qr <new-scratch-folder> <fixture.png> [old-library-folder].");
            string folder = Path.GetFullPath(args[1]);
            if (Directory.Exists(folder)) throw new InvalidOperationException("The QR self-test needs a new folder.");
            Directory.CreateDirectory(folder);
            report = Path.Combine(folder, "qr-report.txt");
            if (args.Length == 4)
            {
                string libraryFolder = Path.Combine(folder, "library");
                Directory.CreateDirectory(libraryFolder);
                foreach (string file in new[] { "Library.kshot", "Library.key" })
                    File.Copy(Path.Combine(args[3], file), Path.Combine(libraryFolder, file));
            }
            Settings.UseFolder(Path.Combine(folder, "settings"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            if (CaptureLibrary.QrReadingEnabled) throw new InvalidOperationException("QR reading must start off.");
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The isolated album did not open.");
            var image = QrImages.Load(File.ReadAllBytes(args[2]));
            long id = CaptureLibrary.AddNow(image, DateTime.Now, "qr", "Synthetic QR fixture", "qr-check");
            var codes = QrImages.Read(image);
            if (codes.Count != 1) throw new InvalidOperationException("The independent fixture was not decoded.");
            CaptureLibrary.StoreQr(id, codes);
            if (CaptureLibrary.QrCodes(id).Single().Text != "https://killershot.net/qr") throw new InvalidOperationException("The decoded payload was not stored.");
            if (!CaptureLibrary.Items("killershot").Any(item => item.Id == id)) throw new InvalidOperationException("The decoded payload was not indexed for search.");
            if (!CaptureLibrary.Items().Single(item => item.Id == id).HasQr)
                throw new InvalidOperationException("The album did not identify a capture with QR results.");
            long empty = CaptureLibrary.AddNow(image, DateTime.Now, "region", "No results", "qr-check");
            CaptureLibrary.StoreQr(empty, []);
            if (CaptureLibrary.Items().Single(item => item.Id == empty).HasQr)
                throw new InvalidOperationException("The album marked an empty QR result as a code.");
            long backlog = CaptureLibrary.AddNow(image, DateTime.Now, "region", "Backlog fixture", "qr-check");
            CaptureLibrary.BackgroundWork = true;
            Task.Run(CaptureLibrary.ScanQrAsync).GetAwaiter().GetResult();
            if (CaptureLibrary.QrCodes(backlog).Count != 0) throw new InvalidOperationException("The worker read a capture while off.");
            CaptureLibrary.QrReadingEnabled = true;
            Task.Run(CaptureLibrary.ScanQrAsync).GetAwaiter().GetResult();
            if (CaptureLibrary.QrCodes(backlog).Count != 1) throw new InvalidOperationException("Turning QR reading on did not scan the backlog.");
            long fresh = CaptureLibrary.AddNow(image, DateTime.Now, "region", "Fresh fixture", "qr-check");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (CaptureLibrary.QrCodes(fresh).Count == 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
            if (CaptureLibrary.QrCodes(fresh).Count != 1) throw new InvalidOperationException("A new capture was not read.");
            CaptureLibrary.QrReadingEnabled = false;
            CaptureLibrary.BackgroundWork = false;
            CaptureLibrary.Close();
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The isolated album did not reopen.");
            if (CaptureLibrary.QrCodes(id).Single().Text != "https://killershot.net/qr") throw new InvalidOperationException("QR results did not survive reopening.");
            var saved = CaptureLibrary.QrCodes(id).Single();
            if (QrLink.Check(saved.Text).Disposition != QrLinkDisposition.Allowed || saved.Corners.Count != 4)
                throw new InvalidOperationException("The stored result lost its corners or link assessment.");
            CaptureLibrary.Close();
            if (File.ReadAllBytes(CaptureLibrary.FilePath).AsSpan().IndexOf("https://killershot.net/qr"u8) >= 0)
                throw new InvalidOperationException("The decoded payload appears as plaintext in the album.");
            File.WriteAllText(report, "PASS: QR payload, corners, encrypted storage, search, reopen, default off, backlog and new-capture worker.");
            code = 0;
        }
        catch (Exception error)
        {
            if (report != null) File.WriteAllText(report, "FAIL: " + error.Message);
        }
        finally { CaptureLibrary.Close(); }
        System.Windows.Application.Current.Shutdown(code);
        return true;
    }
}
