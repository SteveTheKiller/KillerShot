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
        if (args[2] == "protected")
        {
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            CaptureLibrary.SetPassword("synthetic album password");
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
