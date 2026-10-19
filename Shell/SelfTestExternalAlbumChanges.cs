using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using KillerShot.Services;
using Microsoft.Data.Sqlite;

namespace KillerShot.Shell;

internal static class SelfTestExternalAlbumChanges
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length < 3 || args[0] is not ("--selftest-external-album" or "--selftest-external-album-child")) return false;
        _ = RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[0].EndsWith("-child"), args.Contains("--after-export-failure"));
        return true;
    }

    private static async Task RunAsync(string folder, string imagePath, bool child, bool afterExportFailure)
    {
        var report = new List<string>();
        int code = 1;
        try
        {
            if (!child && Directory.Exists(folder)) throw new InvalidOperationException("Use a new external album test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, child ? "child-settings" : "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = !child;
            if (child)
            {
                if (!CaptureLibrary.TryAddNowFromCli(ImageOutput.Load(imagePath), DateTime.Now, "region", "External capture", null, out _))
                    throw new InvalidOperationException(CaptureLibrary.LoadError ?? "Child album write failed.");
                code = 0;
                return;
            }

            CaptureLibrary.QrReadingEnabled = true;
            if (!await CaptureLibrary.BeginOpen()) throw new InvalidOperationException("Album did not open.");
            await CaptureLibrary.ScanQrAsync();
            await Task.Delay(200);
            if (afterExportFailure)
            {
                string key = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(folder, "library", "Library.key")),
                    "KillerShot.Library.v1"u8.ToArray(), DataProtectionScope.CurrentUser));
                using var fault = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = CaptureLibrary.FilePath, Password = key, Pooling = false }.ToString());
                fault.Open();
                fault.CreateCollation("synthetic_external_fault", string.CompareOrdinal);
                using var command = fault.CreateCommand();
                command.CommandText = "CREATE TABLE export_fault(value TEXT COLLATE synthetic_external_fault)";
                command.ExecuteNonQuery();
                bool failed = false;
                try { CaptureLibrary.SetPassword("synthetic password"); }
                catch (SqliteException) { failed = true; }
                if (!failed || !CaptureLibrary.IsOpen) throw new InvalidOperationException("Password export fixture did not recover its original album.");
                command.CommandText = "DROP TABLE export_fault";
                command.ExecuteNonQuery();
            }
            int changes = 0;
            CaptureLibrary.Changed += () => Interlocked.Increment(ref changes);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--selftest-external-album-child");
            start.ArgumentList.Add(folder);
            start.ArgumentList.Add(imagePath);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start synthetic CLI writer.");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (process.ExitCode != 0) throw new InvalidOperationException("Synthetic CLI writer failed.");
            for (int attempt = 0; attempt < 250; attempt++)
            {
                if (CaptureLibrary.Items("killershot.net").Count == 1 && Volatile.Read(ref changes) > 0) break;
                await Task.Delay(20);
            }
            if (CaptureLibrary.Items("killershot.net").Count != 1 || Volatile.Read(ref changes) == 0)
                throw new InvalidOperationException("An external capture did not wake recognition and album refresh after the workers became idle.");
            report.Add("external_commit_refreshes_album_and_recognizes_qr=True");
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, child ? "child-report.txt" : "external-album-report.txt"), report);
            CaptureLibrary.Close();
            Application.Current.Shutdown(code);
        }
    }
}
