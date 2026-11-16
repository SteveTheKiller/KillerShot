using System.IO;
using System.Windows;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// KillerShot.exe --selftest-sqlcipher-compat &lt;folder&gt; create|open [expected version]: checks a
/// SQLCipher engine bump against albums an older build made. Run "create" with the old build into
/// a new folder: it makes an account-key album and a password album, each holding one picture with
/// a searchable note, and saves the picture's bytes beside them. Then run "open" with the new build
/// on the same folder: both albums must open, return the same bytes, find the note, refuse a wrong
/// password, and accept a password change. Opens no window.
/// </summary>
internal static class SelfTestSqlCipherCompat
{
    private const string Password = "compat-Password-1";
    private const string Marker = "compatmarker";

    internal static bool TryRun(string[] args)
    {
        if (args.Length < 3 || args[0] != "--selftest-sqlcipher-compat") return false;
        _ = Task.Run(() => RunAsync(Path.GetFullPath(args[1]), args[2], args.Length > 3 ? args[3] : null));
        return true;
    }

    private static Task RunAsync(string folder, string mode, string? expected)
    {
        var report = new List<string>();
        int failures = 0;
        void Check(bool pass, string name) { if (!pass) failures++; report.Add((pass ? "PASS " : "FAIL ") + name); }
        try
        {
            if (mode == "create" && Directory.Exists(folder)) throw new InvalidOperationException("Use a new folder for create.");
            if (mode == "open" && !Directory.Exists(folder)) throw new InvalidOperationException("Run create first.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native-" + mode));
            CaptureLibrary.BackgroundWork = false;
            Application.Current.Dispatcher.Invoke(App.InitializeKit);
            foreach (bool password in new[] { false, true })
            {
                string name = password ? "password" : "account";
                CaptureLibrary.UseFolder(Path.Combine(folder, name));
                string bytesFile = Path.Combine(folder, name + ".png");
                if (mode == "create")
                {
                    if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(name + " album did not open: " + CaptureLibrary.LoadError);
                    var picture = Application.Current.Dispatcher.Invoke(SelfTestAutoRedact.Picture);
                    long id = CaptureLibrary.AddNow(picture, new DateTime(2026, 11, 16), "region", "compat", null);
                    CaptureLibrary.SetNote(id, Marker);
                    File.WriteAllBytes(bytesFile, CaptureLibrary.LoadOutputPng(id));
                    if (password) CaptureLibrary.SetPassword(Password);
                    report.Add($"{name} created with {CaptureLibrary.CipherVersion()}");
                    CaptureLibrary.Close();
                    continue;
                }
                if (password)
                {
                    Check(!CaptureLibrary.OpenAuto() && CaptureLibrary.NeedsPassword, "password album asks for its password");
                    Check(!CaptureLibrary.Unlock("wrong-password"), "password album refuses a wrong password");
                    Check(CaptureLibrary.Unlock(Password), "password album opens with its password");
                }
                else Check(CaptureLibrary.OpenAuto(), "account album opens");
                string version = CaptureLibrary.CipherVersion();
                report.Add($"{name} opened with {version}");
                if (expected != null) Check(version.StartsWith(expected, StringComparison.Ordinal), $"{name} runs on SQLCipher {expected}");
                var items = CaptureLibrary.Items();
                Check(items.Count == 1 && CaptureLibrary.LoadOutputPng(items[0]).SequenceEqual(File.ReadAllBytes(bytesFile)), name + " picture bytes are unchanged");
                Check(CaptureLibrary.Items(Marker).Count == 1, name + " note search still finds the picture");
                if (password)
                {
                    CaptureLibrary.SetPassword(Password + "-new");
                    CaptureLibrary.Close();
                    Check(CaptureLibrary.Unlock(Password + "-new") && CaptureLibrary.Items().Count == 1, "password change rekeys and reopens");
                }
                CaptureLibrary.Close();
            }
        }
        catch (Exception error) { failures++; report.Add(error.ToString()); }
        report.Add(failures == 0 ? "result=ok" : $"result=error failures={failures}");
        File.WriteAllLines(Path.Combine(folder, "report-" + mode + ".txt"), report);
        Application.Current.Dispatcher.Invoke(() => App.Instance.Shutdown(failures == 0 ? 0 : 1));
        return Task.CompletedTask;
    }
}
