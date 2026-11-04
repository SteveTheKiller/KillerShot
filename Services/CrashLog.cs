using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace KillerShot.Services;

/// <summary>Last-chance error handling for the running app. KillerShot lives in the tray, so an
/// exception nobody caught would otherwise end it silently, edits and all. A UI-thread exception
/// is logged, the user is told, and the app keeps running unless the failure is one no process
/// survives; task and background-thread failures are logged.</summary>
internal static class CrashLog
{
    private const long MaxLogBytes = 1024 * 1024;
    private static readonly object Gate = new();

    internal static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerShot");

    internal static string LogPath => Path.Combine(Folder, "crash.log");

    /// <summary>Shows a short warning, normally the tray balloon. Null until the tray exists.</summary>
    internal static Action<string>? Warn { get; set; }

    internal static void Install(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Write("UI thread", e.Exception);
            if (IsFatal(e.Exception)) return;
            e.Handled = true;
            try { Warn?.Invoke(e.Exception.Message); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.IsTerminating ? "background thread, terminating" : "background thread", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    private static bool IsFatal(Exception error) =>
        error is OutOfMemoryException or StackOverflowException or AccessViolationException or SEHException;

    private static void Write(string source, Exception? error)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var log = new FileInfo(LogPath);
                if (log.Exists && log.Length > MaxLogBytes) log.Delete();
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {AppInfo.Version} {source}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch { /* Logging must never be the second failure. */ }
    }
}
