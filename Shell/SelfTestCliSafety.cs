using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using KillerShot.Services;
using Microsoft.Data.Sqlite;

namespace KillerShot.Shell;

/// <summary>Runs real CLI commands with isolated settings and synthetic album data.</summary>
internal static class SelfTestCliSafety
{
    public static bool TryRun(string[] args)
    {
        if (args.Length < 4 || args[0] != "--selftest-cli-safety") return false;
        string dir = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(dir);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(dir, "native"));
        Settings.UseFolder(Path.Combine(dir, "settings"));
        CaptureLibrary.UseFolder(Path.Combine(dir, "library"));
        CaptureLibrary.BackgroundWork = false;
        if (args[2] is "cursor-on" or "cursor-off") CaptureSettings.IncludeCursor = args[2] == "cursor-on";
        if (args[2] is "redact" or "redact-failed")
        {
            // Scan every capture is on. The stand-in reader finds an email at a fixed spot, or
            // fails, so the check does not depend on what is on the screen.
            RedactSettings.Kinds = RedactKinds.Email;
            RedactSettings.Fill = Color.FromRgb(0xFF, 0x00, 0xFF);
            RedactSettings.Feather = 0;
            RedactSettings.ScanEveryCapture = true;
            var white = new byte[200 * 200 * 4];
            Array.Fill(white, (byte)0xFF);
            var area = new Native.Win32.RECT(0, 0, 200, 200);
            Cli.Program.Freeze = _ => new Capture.FrozenDesktop(area, white, [new Capture.MonitorBounds(IntPtr.Zero, area, true)], []);
            Cli.Program.ReadWordsAsync = args[2] == "redact"
                ? _ => Task.FromResult<IReadOnlyList<IReadOnlyList<OcrWord>>?>([[new OcrWord("jane.doe@example.com", new Rect(4, 4, 24, 12))]])
                : _ => throw new InvalidOperationException("Synthetic recognition failure.");
        }
        if (args[2] == "protected")
        {
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            CaptureLibrary.SetPassword("synthetic album password");
            CaptureLibrary.Close();
        }
        else if (args[2] == "two-captures")
        {
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            foreach (string title in new[] { "Older capture", "Newer capture" })
            {
                var pixels = new byte[4 * 4 * 4];
                Array.Fill(pixels, (byte)0x80);
                var picture = System.Windows.Media.Imaging.BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
                CaptureLibrary.AddNow(picture, DateTime.Now, "region", title, null);
            }
            CaptureLibrary.Close();
        }
        else if (args[2] == "recording")
        {
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            string clip = Path.Combine(dir, "clip.mp4");
            File.WriteAllBytes(clip, [1, 3, 5, 7, 9]);
            CaptureLibrary.AddVideo(clip, DateTime.Now, "Synthetic clip");
            File.Delete(clip);
            CaptureLibrary.Close();
        }
        else if (args[2] == "failed-write")
        {
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            string key = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                File.ReadAllBytes(Path.Combine(dir, "library", "Library.key")),
                "KillerShot.Library.v1"u8.ToArray(), DataProtectionScope.CurrentUser));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = CaptureLibrary.FilePath, Password = key, Pooling = false }.ConnectionString);
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_capture BEFORE INSERT ON captures BEGIN SELECT RAISE(ABORT, 'synthetic write failure'); END";
            command.ExecuteNonQuery();
            CaptureLibrary.Close();
        }
        _ = RunAsync(args[3..], args[2] == "recording-audio-failure");
        return true;
    }

    private static async Task RunAsync(string[] command, bool failAudio)
    {
        // The test captures inside this solid synthetic surface.
        var window = new Window
        {
            Title = "KillerShot synthetic CLI check", Left = 0, Top = 0,
            Width = failAudio ? 640 : 96, Height = failAudio ? 480 : 96,
            WindowStartupLocation = WindowStartupLocation.Manual, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false,
            Topmost = true, Background = Brushes.Red,
        };
        window.Show();
        await Task.Delay(100);
        Cli.Program.Start(command, attachConsole: false, Application.Current.Shutdown,
            failAudio ? [new FailingAudioSource()] : null);
    }

    private sealed class FailingAudioSource : KillerShot.Video.IAudioCaptureSource
    {
        public Exception? Error { get; private set; }
        public void Start() { }
        public void Stop() => Error = new IOException("Synthetic recording audio failure.");
        public void Drain(int frames, float[]? mix) { }
        public void Dispose() => Stop();
    }
}
