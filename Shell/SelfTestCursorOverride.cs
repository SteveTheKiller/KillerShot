using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Native;

namespace KillerShot.Shell;

internal static class SelfTestCursorOverride
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-cursor-override") return false;
        string folder = Path.GetFullPath(args[1]); Directory.CreateDirectory(folder);
        _ = RunAsync(folder);
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        try
        {
            Win32.GetCursorPos(out var cursor);
            var window = new Window { Width = 360, Height = 280, Background = Brushes.Lime,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true,
                Left = cursor.X - 180, Top = cursor.Y - 140, ShowActivated = false };
            window.Show();
            await Task.Delay(250);
            var handle = new WindowInteropHelper(window).Handle;
            var results = new Dictionary<string, byte[]>();
            foreach (bool saved in new[] { false, true })
            foreach (bool include in new[] { false, true })
            {
                string name = $"saved-{saved}-flag-{include}";
                string path = Path.Combine(folder, name + ".png");
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in new[] { "--selftest-cli-safety", Path.Combine(folder, name), saved ? "cursor-on" : "cursor-off", "capture", "--mode", "window", "--window", handle.ToInt64().ToString(), include ? "--cursor" : "--no-cursor", "--no-clipboard", "--out", path, "--json" })
                    start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                string json = await output;
                if (process.ExitCode != 0 || !json.Contains("true", StringComparison.Ordinal)) throw new InvalidOperationException("CLI capture failed: " + await errors + json);
                using var stream = File.OpenRead(path);
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var image = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0);
                results.Add(name, bytes);
                report.Add(name + ": " + json.Trim());
            }
            if (!results["saved-False-flag-False"].SequenceEqual(results["saved-True-flag-False"]) ||
                !results["saved-False-flag-True"].SequenceEqual(results["saved-True-flag-True"]) ||
                results["saved-False-flag-False"].SequenceEqual(results["saved-False-flag-True"]))
                throw new InvalidOperationException("Window capture did not follow cursor flags independently of saved preferences.");
            report.Add("Both explicit cursor flags override the opposite saved preference PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "cursor-override-report.txt"), report);
        App.Instance.Shutdown(code);
    }
}
