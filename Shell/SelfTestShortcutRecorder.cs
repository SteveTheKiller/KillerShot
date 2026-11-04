using System.IO;
using System.Windows.Input;
using KillerShot.Capture;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>The shortcut recorder in Capture settings: chords Windows itself owns are refused, and a
/// recorder left listening never leaves the global shortcuts switched off.</summary>
internal static class SelfTestShortcutRecorder
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-shortcut-recorder") return false;
        string folder = Path.GetFullPath(args[1]);
        var report = new List<string>();
        int code = 1;
        MainWindow? window = null;
        TrayHost? tray = null;
        try
        {
            if (Directory.Exists(folder)) throw new IOException("Use a new shortcut recorder test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            App.InitializeKit();
            tray = new TrayHost();
            window = new MainWindow(tray);

            ReservedChords();
            report.Add("Alt+F4 and Win chords are reserved; ordinary capture chords are not PASS");
            RecorderRefusesReserved(window);
            report.Add("the recorder refuses Alt+F4, keeps listening and stores nothing PASS");
            StoredReservedChordIgnored(tray);
            report.Add("a hand-edited reserved chord is never stored or registered PASS");
            HideToTrayResumes(window, tray);
            report.Add("hiding to the tray with a recorder listening restores every global shortcut PASS");
            DeactivationResumes(window, tray);
            report.Add("leaving the window with a recorder listening restores every global shortcut PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            try { window?.Close(); } catch { }
            tray?.Dispose();
            File.WriteAllLines(Path.Combine(folder, "shortcut-recorder-report.txt"), report);
            App.Instance.Shutdown(code);
        }
        return true;
    }

    private static void ReservedChords()
    {
        bool Reserved(string text) => Shortcut.TryParse(text, out var shortcut) && Shortcut.IsReserved(shortcut);
        if (!Reserved("Alt+F4") || !Reserved("Win+L") || !Reserved("Win+Print Screen") || Reserved("Ctrl+Shift+P")
            || Reserved("Alt+Print Screen") || Reserved("F4") || Reserved("Ctrl+F4"))
            throw new InvalidOperationException("The reserved chord list is wrong.");
    }

    private static void RecorderRefusesReserved(MainWindow window)
    {
        var action = ShortcutAction.Window;
        string before = CaptureSettings.ShortcutText(action);
        if (!window.TestStartRecording(action)) throw new InvalidOperationException("No recorder for " + action + ".");
        window.TestRecorderKey(Key.F4, ModifierKeys.Alt);
        if (CaptureSettings.ShortcutText(action) != before)
            throw new InvalidOperationException("Alt+F4 was stored as the " + action + " shortcut.");
        if (!window.TestIsRecording) throw new InvalidOperationException("The recorder stopped listening after a refused chord.");
        window.TestRecorderKey(Key.Escape, ModifierKeys.None);
    }

    private static void StoredReservedChordIgnored(TrayHost tray)
    {
        var action = ShortcutAction.Interval;
        CaptureSettings.SetShortcutText(action, "Alt+F4");
        if (CaptureSettings.ShortcutText(action) == "Alt+F4") throw new InvalidOperationException("A reserved chord was stored.");
        Settings.Set("Shortcut." + action, "Alt+F4");
        tray.RegisterShortcuts();
        if (tray.TestRegistered(action)) throw new InvalidOperationException("A hand-edited Alt+F4 was registered globally.");
        Settings.Set("Shortcut." + action, CaptureSettings.Defaults[action]);
        tray.RegisterShortcuts();
    }

    private static void HideToTrayResumes(MainWindow window, TrayHost tray)
    {
        // Print Screen chords can be served by the keyboard hook instead of a registration, so one
        // ordinary chord makes the registered count prove the shortcuts really came back.
        CaptureSettings.SetShortcutText(ShortcutAction.Interval, "Ctrl+Alt+Shift+F11");
        tray.RegisterShortcuts();
        int registered = tray.TestRegisteredCount;
        if (registered == 0) throw new InvalidOperationException("No global shortcut registered for the recorder check.");
        if (!window.TestStartRecording(ShortcutAction.Monitor) || !tray.IsSuspended)
            throw new InvalidOperationException("Recording did not suspend the global shortcuts.");
        window.TestHideToTray();
        if (tray.IsSuspended || tray.TestRegisteredCount != registered)
            throw new InvalidOperationException($"Hiding to the tray left the shortcuts suspended: suspended={tray.IsSuspended}, registered={tray.TestRegisteredCount} of {registered}.");
    }

    private static void DeactivationResumes(MainWindow window, TrayHost tray)
    {
        int registered = tray.TestRegisteredCount;
        if (!window.TestStartRecording(ShortcutAction.Monitor) || !tray.IsSuspended)
            throw new InvalidOperationException("Recording did not suspend the global shortcuts.");
        window.TestDeactivate();
        if (tray.IsSuspended || tray.TestRegisteredCount != registered)
            throw new InvalidOperationException($"Leaving the window left the shortcuts suspended: suspended={tray.IsSuspended}, registered={tray.TestRegisteredCount} of {registered}.");
    }
}
