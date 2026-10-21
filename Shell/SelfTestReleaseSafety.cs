using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Controls;
using KillerShot.Capture;
using KillerShot.Editor;
using KillerShot.Services;
using Microsoft.Data.Sqlite;

namespace KillerShot.Shell;

internal static class SelfTestReleaseSafety
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    internal static bool TryRun(string[] args)
    {
        if (SelfTestAlbumLayout.TryRun(args)) return true;
        if (SelfTestViewCards.TryRun(args)) return true;
        if (SelfTestWindowLifetime.TryRun(args)) return true;
        if (args.Length < 2 || args[0] != "--selftest-release-safety") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new release safety test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Path.Combine(folder, "Screenshots"));
            FolderCaptures.Enabled = false;
            App.InitializeKit();
            App.CloseToTray = true;
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("Synthetic album did not open.");
            var image = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null,
                Enumerable.Repeat((byte)255, 64).ToArray(), 16);
            image.Freeze();
            var original = new EditorDocument(image);
            App.Instance.SetDocument(original);
            App.Instance.ShowEditor();
            await Task.Delay(150);
            original.Crop(new Int32Rect(0, 0, 2, 2));
            Task<bool>? exitAttempt = null;
            bool exitPrompted = RespondToConfirmation(false, () => exitAttempt = App.Instance.CanExitAsync(),
                renderPath: Path.Combine(folder, "unsaved-exit.png"));
            Check(exitPrompted && !await exitAttempt! && ReferenceEquals(App.Instance.Document, original) && original.Dirty,
                "Canceling exit preserves the open editor and unsaved crop.");
            report.Add("exit_cancel_preserves_unsaved_crop=True");
            exitPrompted = RespondToConfirmation(false, () => exitAttempt = App.Instance.CanExitAsync(), alternative: true);
            Check(exitPrompted && await exitAttempt! && ReferenceEquals(App.Instance.Document, original),
                "Discard permits exit without replacing the document.");
            string exitSave = Path.Combine(folder, "exit-save.png");
            ImageOutput.Save(image, exitSave);
            original.FilePath = exitSave;
            exitPrompted = RespondToConfirmation(true, () => exitAttempt = App.Instance.CanExitAsync());
            Check(exitPrompted && await exitAttempt! && !original.Dirty && ImageOutput.Load(exitSave).PixelWidth == 2,
                "Save before exit writes the cropped image and clears dirty state.");
            report.Add("exit_save_and_discard_choices=True");
            original.Add(new ShapeAnnotation(ShapeKind.Rectangle, new Point(0, 0), new Point(1, 1), Colors.Red, 1));
            bool prompted = RespondToConfirmation(false, () => App.Instance.SetDocument(new EditorDocument(image)));
            Check(prompted && ReferenceEquals(App.Instance.Document, original) && original.Dirty,
                "Cancel preserves the unsaved document and its edits.");
            report.Add("unsaved_cancel_preserves_document=True");

            var replacement = new EditorDocument(image);
            prompted = RespondToConfirmation(true, () => App.Instance.SetDocument(replacement));
            Check(prompted && ReferenceEquals(App.Instance.Document, replacement), "Discard opens the selected document.");
            report.Add("unsaved_discard_opens_document=True");
            replacement.Crop(new Int32Rect(0, 0, 2, 2));
            replacement.MarkSaved();
            var savedReplacement = new EditorDocument(image);
            prompted = RespondToConfirmation(false, () => App.Instance.SetDocument(savedReplacement));
            Check(!prompted && ReferenceEquals(App.Instance.Document, savedReplacement),
                "A saved document with undo history opens another image without a discard prompt.");
            report.Add("saved_undo_history_does_not_prompt=True");
            for (int n = 0; n < 120; n++) CaptureLibrary.AddNow(image, DateTime.Now.AddSeconds(n), "region", "Retention " + n, null);
            var window = App.Instance.EditorWindow!;
            ((Button)window.FindName("LibrarySettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var keep = (ComboBox)window.FindName("LibraryKeepCombo");
            keep.SelectedIndex = 1;
            await Task.Delay(300);
            Check(CaptureLibrary.Keep == "all" && CaptureLibrary.Count == 120,
                "Browsing retention choices must not change the saved policy or delete captures.");
            report.Add("retention_browse_preserves_captures=True");
            ((System.Windows.Controls.Primitives.Popup)window.FindName("LibrarySettingsPopup")).IsOpen = false;
            ((Button)window.FindName("LibrarySettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(keep.SelectedIndex == 0, "Closing the settings card cancels an unapplied retention choice.");
            keep.SelectedIndex = 1;
            ((Button)window.FindName("LibraryKeepSaveBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(300);
            Check(CaptureLibrary.Keep == "count:100" && CaptureLibrary.Count == 100,
                "Saving the selected retention choice applies that exact limit.");
            report.Add("retention_save_applies_selected_limit=True");
            Render((FrameworkElement)((System.Windows.Controls.Primitives.Popup)window.FindName("LibrarySettingsPopup")).Child,
                Path.Combine(folder, "retention-settings.png"));
            ((System.Windows.Controls.Primitives.Popup)window.FindName("LibrarySettingsPopup")).IsOpen = false;
            long target = CaptureLibrary.CreateCollection("Move target");
            var item = CaptureLibrary.Items().First();
            string key = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                File.ReadAllBytes(Path.Combine(folder, "library", "Library.key")), "KillerShot.Library.v1"u8.ToArray(), DataProtectionScope.CurrentUser));
            using var fault = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = CaptureLibrary.FilePath, Mode = SqliteOpenMode.ReadWrite, Password = key, Pooling = false }.ToString());
            fault.Open();
            using var command = fault.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_move BEFORE UPDATE ON captures BEGIN SELECT RAISE(ABORT, 'Synthetic move failure'); END;";
            command.ExecuteNonQuery();
            var view = window.TestLibrary;
            string? error = null;
            view.StatusMessage += message => error = message;
            view.ShowItemMenu(item, (FrameworkElement)window.FindName("LibrarySettingsButton"), null, "");
            await Task.Delay(50);
            var menu = PresentationSource.CurrentSources.Cast<PresentationSource>().Where(s => s.RootVisual != null)
                .SelectMany(s => Visuals(s.RootVisual)).OfType<ContextMenu>().Single(m => m.IsOpen);
            var move = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, LocaleManager.Loc("Str_Lib_MoveTo")));
            var destination = move.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Move target"));
            destination.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(error?.Contains("Synthetic move failure") == true && CaptureLibrary.Items().Single(i => i.Id == item.Id).CollectionId == null,
                "A failed album action reports the error and preserves the capture.");
            command.CommandText = "DROP TRIGGER fail_move;";
            command.ExecuteNonQuery();
            destination.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(CaptureLibrary.Items().Single(i => i.Id == item.Id).CollectionId == target,
                "The same album action succeeds after the database failure is removed.");
            menu.IsOpen = false;
            report.Add("album_action_failure_is_contained_and_retry_succeeds=True");
            foreach (ShortcutAction action in Enum.GetValues<ShortcutAction>()) CaptureSettings.SetShortcutText(action, "");
            CaptureSettings.SetShortcutText(ShortcutAction.LastMode, "Ctrl+Alt+Shift+F24");
            using var tray = new TrayHost();
            tray.RegisterShortcuts();
            Check(!tray.IsFailed(ShortcutAction.LastMode), "The synthetic shortcut could not be registered.");
            int shortcuts = 0;
            tray.CaptureRequested += (_, _, _) => shortcuts++;
            SendMessage(tray.Handle, 0x0312, (IntPtr)1, IntPtr.Zero);
            Check(shortcuts == 1, "The synthetic shortcut must reach the live tray host.");
            var shortcutWindow = new MainWindow(tray) { Left = -30000, Top = -30000, ShowInTaskbar = false };
            shortcutWindow.Show();
            // This build runs from the scratch folder, so it is a portable copy and offers to install.
            Check(App.IsPortable() && shortcutWindow.FindName("PortableBadge") is Button { Visibility: Visibility.Visible },
                "A portable copy must show the install badge.");
            Check(System.Windows.Automation.AutomationProperties.GetName((Button)shortcutWindow.FindName("PortableBadge"))
                    .StartsWith(LocaleManager.Loc("Str_Portable"), StringComparison.Ordinal),
                "The install badge's spoken name must start with its visible text.");
            report.Add("portable_badge=True");
            ((Button)shortcutWindow.FindName("CaptureSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var recorder = ((Grid)shortcutWindow.FindName("ShortcutRows")).Children.OfType<Button>()
                .Single(b => Equals(b.Tag, ShortcutAction.LastMode));
            recorder.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SendMessage(tray.Handle, 0x0312, (IntPtr)1, IntPtr.Zero);
            Check(shortcuts == 1, "Listening must suspend the shortcut.");
            var closed = new TaskCompletionSource();
            shortcutWindow.Closed += (_, _) => closed.TrySetResult();
            shortcutWindow.Opacity = 0;
            shortcutWindow.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            SendMessage(tray.Handle, 0x0312, (IntPtr)1, IntPtr.Zero);
            Check(shortcuts == 2, "Closing the listening window must restore global shortcuts.");
            report.Add("window_close_restores_shortcuts=True");
            CaptureLibrary.Keep = "all";
            command.CommandText = "CREATE TRIGGER fail_capture BEFORE INSERT ON captures BEGIN SELECT RAISE(ABORT, 'Synthetic save failure'); END;";
            command.ExecuteNonQuery();
            CaptureLibrary.Add(image, DateTime.Now, "region", "Pending capture", null);
            await CaptureLibrary.RetryPendingAsync();
            view.Reload();
            Check(CaptureLibrary.PendingCount == 1 && view.FindName("PendingBar") is FrameworkElement { Visibility: Visibility.Visible },
                "Failed captures must remain visible while the album is open.");
            report.Add("pending_captures_are_visible=True");
            App.Instance.SetDocument(null);
            App.Instance.ShowEditor();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            for (int attempt = 0; attempt < 100 && (!view.IsVisible
                || !((FrameworkElement)view.FindName("PendingBar")).IsVisible); attempt++) await Task.Delay(20);
            window.UpdateLayout();
            Check(view.IsVisible && ((FrameworkElement)view.FindName("PendingBar")).IsVisible,
                "The pending-capture screenshot must show the notice in the visible album.");
            Render(window, Path.Combine(folder, "pending-captures.png"));
            var originalTheme = ThemeManager.Current;
            foreach (var theme in new[] { Theme.Light, Theme.SE98 })
            {
                ThemeManager.Apply(theme);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                Render(window, Path.Combine(folder, "pending-captures-" + theme + ".png"));
            }
            ThemeManager.Apply(originalTheme);
            var exitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            bool exitPrompt = false;
            exitTimer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<ConfirmDialog>().FirstOrDefault(w => w.IsVisible);
                if (dialog == null) return;
                exitPrompt = true;
                exitTimer.Stop();
                Render(dialog, Path.Combine(folder, "pending-exit.png"));
                ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            exitTimer.Start();
            bool canExit;
            try { canExit = await App.Instance.CanExitAsync(); }
            finally { exitTimer.Stop(); }
            Check(exitPrompt && !canExit && CaptureLibrary.PendingCount == 1,
                "Canceling exit must retain the pending capture and running application.");
            command.CommandText = "DROP TRIGGER fail_capture;";
            command.ExecuteNonQuery();
            ((Button)view.FindName("RetryPendingBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (int attempt = 0; attempt < 100 && CaptureLibrary.PendingCount > 0; attempt++) await Task.Delay(20);
            view.Reload();
            Check(CaptureLibrary.PendingCount == 0 && CaptureLibrary.Items().Any(i => i.Window == "Pending capture")
                && ((FrameworkElement)view.FindName("PendingBar")).Visibility == Visibility.Collapsed,
                "Retry saves the pending capture and clears the notice.");
            Check(await App.Instance.CanExitAsync(), "Exit may proceed once all captures are saved.");
            report.Add("pending_exit_cancel_and_retry_preserve_capture=True");
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            if (Directory.Exists(folder)) File.WriteAllLines(Path.Combine(folder, "release-safety-report.txt"), report);
            App.Instance.Document?.MarkSaved();
            CaptureLibrary.Close();
            Application.Current.Shutdown(code);
        }
    }

    private static bool RespondToConfirmation(bool accept, Action action, bool alternative = false, string? renderPath = null)
    {
        bool seen = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<ConfirmDialog>().FirstOrDefault(w => w.IsVisible);
            if (dialog == null) return;
            seen = true;
            timer.Stop();
            if (renderPath != null) Render(dialog, renderPath);
            ((Button)dialog.FindName(alternative ? "AlternativeButton" : accept ? "OkButton" : "CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        timer.Start();
        try { action(); }
        finally { timer.Stop(); }
        return seen;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Render(FrameworkElement element, string path)
    {
        if (element is Window window) element = (FrameworkElement)window.Content;
        element.UpdateLayout();
        Check(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0,
            "Screenshots require a visible element with a completed layout.");
        var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element), null, bounds);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width),
            (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        ImageOutput.SavePng(bitmap, path);
    }
}
