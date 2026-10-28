using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static partial class SelfTestAlbums
{
    private static async Task CheckManagerInteractionAsync(string folder, List<string> report)
    {
        report.Add("H1: Canceling a manager prompt preserves the active album and unsaved document.");
        report.Add("H2: Accepting a switch clears the old editor only after destination validation and discard consent.");
        report.Add("H3: Password cancellation or a wrong password leaves the original album live and permits retry.");
        report.Add("H4: Rename keeps the album and matching key together, while a duplicate name shows a visible error.");
        FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
        FolderCaptures.Enabled = false;
        ThemeManager.Apply(Theme.Dark);
        LocaleManager.Apply(Locale.EnUS);
        App.CloseToTray = true;
        string original = CaptureLibrary.FilePath;
        App.Instance.OpenLibraryCapture(CaptureLibrary.Items().Single());
        App.Instance.ShowEditor();
        var window = App.Instance.EditorWindow!;
        var area = SystemParameters.WorkArea;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = area.Left + 20;
        window.Top = area.Top + 20;
        window.Width = Math.Max(window.MinWidth, Math.Min(1100, area.Width - 40));
        window.Height = Math.Max(window.MinHeight, Math.Min(760, area.Height - 40));
        window.UpdateLayout();
        await Task.Delay(150);
        var document = App.Instance.Document!;
        long editedId = App.Instance.CurrentItem!.Id;
        document.Crop(new Int32Rect(0, 0, 1, 1));
        window.TestUseModelessAlbums();
        var list = (ListBox)window.FindName("AlbumList");

        async Task ShowManager()
        {
            if (!window.TestAlbumsOpen) await ManagerClickAsync((Button)window.FindName("LibrarySettingsButton"));
            await Task.Delay(70);
            Check(window.TestAlbumsOpen, "The album manager must remain open after a dialog.");
        }
        Button Named(string name) => (Button)window.FindName(name);
        Button NewButton() => ManagerVisuals(window.TestAlbumsCard!).OfType<Button>()
            .Single(button => Equals(button.Content, LocaleManager.Loc("Str_Album_New")));
        void Select(string path)
        {
            list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(row => row.Tag is CaptureLibrary.AlbumInfo album
                && string.Equals(album.Path, path, StringComparison.OrdinalIgnoreCase));
            list.ScrollIntoView(list.SelectedItem);
        }
        void Preserved(string context) => Check(CaptureLibrary.FilePath == original && CaptureLibrary.Count == 1
            && ReferenceEquals(App.Instance.Document, document)
            && App.Instance.CurrentItem != null, context);

        await ShowManager();
        await ManagerClickAsync(NewButton(), dialog => ManagerCancel<PromptDialog>(dialog));
        Preserved("Canceling New must preserve the original album and unsaved editor.");
        Check(CaptureLibrary.Albums().Count == 1 && window.TestAlbumsOpen, "Canceling New must not create a file or hide the manager.");
        report.Add("New dialog Cancel preserves album, files, unsaved editor, and manager: PASS");

        // An album capture keeps its edits in its album, so a new album opens without asking to
        // discard them.
        await ManagerClickAsync(NewButton(), dialog => ManagerText(dialog, "Work"));
        string work = CaptureLibrary.Albums().Single(album => album.Name == "Work").Path;
        Check(CaptureLibrary.FilePath == work && CaptureLibrary.Count == 0 && App.Instance.Document == null
            && App.Instance.CurrentItem == null && ((TextBlock)window.FindName("TitleLibraryLabel")).Text == "Work",
            "New must activate Work and clear the old editor context.");
        Check(window.TestAlbumsOpen, "Creating an album must leave the manager visible.");
        report.Add("New activates the album without a discard prompt; the album capture's edits stay in its album: PASS");

        await ManagerClickAsync(NewButton(), dialog => ManagerText(dialog, "Personal"));
        string personal = CaptureLibrary.FilePath;
        Check(Path.GetFileName(personal) == "Personal.kshot" && CaptureLibrary.Count == 0,
            "New without unsaved edits must activate the requested album.");
        CaptureLibrary.AddNow(Picture(0x99), DateTime.Now, "region", "Project marker", null);
        await ShowManager();
        await ManagerClickAsync(Named("AlbumRenameBtn"), dialog => ManagerCancel<PromptDialog>(dialog));
        Check(CaptureLibrary.FilePath == personal && File.Exists(personal), "Canceling Rename must preserve the original filename.");
        await ManagerClickAsync(Named("AlbumRenameBtn"), dialog => ManagerText(dialog, "Project"));
        string project = CaptureLibrary.FilePath;
        Check(Path.GetFileName(project) == "Project.kshot" && !File.Exists(personal)
            && File.Exists(Path.ChangeExtension(project, ".key")) && CaptureLibrary.Count == 1
            && ((TextBlock)window.FindName("TitleLibraryLabel")).Text == "Project",
            "Rename must preserve the active data, move its key, and refresh the title.");
        await ManagerClickAsync(Named("AlbumRenameBtn"), dialog => ManagerText(dialog, "Work"));
        var status = (TextBlock)window.FindName("LibraryStatusText");
        Check(CaptureLibrary.FilePath == project && CaptureLibrary.Count == 1 && File.Exists(work)
            && window.TestAlbumsOpen && status.IsVisible && status.Text.Contains("already exists", StringComparison.OrdinalIgnoreCase),
            "A duplicate rename must preserve both albums and show its error in the manager.");
        report.Add("Rename Cancel, successful active rename with key preservation, and visible duplicate-name rejection: PASS");

        await ManagerClickAsync(Named("LibraryPasswordBtn"), dialog =>
        {
            Check(dialog is PromptDialog, "Setting a password must use the real password prompt.");
            ((PasswordBox)dialog.FindName("PwBox")).Password = "manager fixture password";
            ((PasswordBox)dialog.FindName("PwConfirmBox")).Password = "mismatch";
            ManagerAccept<PromptDialog>(dialog);
            Check(((TextBlock)dialog.FindName("ErrorText")).IsVisible, "Mismatched passwords must remain in the prompt with an error.");
            ((PasswordBox)dialog.FindName("PwConfirmBox")).Password = "manager fixture password";
            ManagerAccept<PromptDialog>(dialog);
        });
        Check(CaptureLibrary.HasPassword && !File.Exists(Path.ChangeExtension(project, ".key")) && CaptureLibrary.Count == 1,
            "Setting a password through the manager must preserve captures and remove the account key.");
        await ShowManager();
        Select(original);
        await ManagerClickAsync(Named("AlbumOpenBtn"));
        Check(CaptureLibrary.FilePath == original && CaptureLibrary.Count == 1, "The account-protected original must reopen.");
        Check(CaptureLibrary.LoadMarks(editedId)?.Image.PixelWidth == 1, "The original album lost the edits made before switching.");
        Select(project);
        await ManagerClickAsync(Named("AlbumOpenBtn"), dialog => ManagerCancel<PromptDialog>(dialog));
        Check(CaptureLibrary.FilePath == original && CaptureLibrary.Count == 1 && window.TestAlbumsOpen,
            "Canceling the unlock prompt must preserve the active album and manager.");
        await ManagerClickAsync(Named("AlbumOpenBtn"), dialog => ManagerPassword(dialog, "wrong password"), dialog =>
        {
            Check(((TextBlock)dialog.FindName("DetailText")).Text == LocaleManager.Loc("Str_Lib_WrongPassword"),
                "Wrong-password retry must explain the failed password.");
            Check(CaptureLibrary.FilePath == original && CaptureLibrary.Count == 1,
                "The original album must remain live while retrying a password.");
            ManagerPassword(dialog, "manager fixture password");
        });
        Check(CaptureLibrary.FilePath == project && CaptureLibrary.HasPassword && CaptureLibrary.Count == 1 && window.TestAlbumsOpen,
            "Correct password retry must activate the selected album without losing captures.");
        report.Add("Password mismatch, account-album reopen, unlock Cancel, wrong-password retry, and successful unlock: PASS");
        Check(Settings.Get("ActiveAlbum") == "Project.kshot", "The manager's final selection must be persisted for restart.");
        window.TestCloseAlbums();
        App.Instance.Document?.MarkSaved();
        window.Close();
    }

    private static async Task ManagerClickAsync(Button button, params Action<Window>[] responses)
    {
        Check(button.IsEnabled && button.IsVisible, "The manager action must be visible and enabled: " + button.Name);
        int answered = 0;
        Exception? failure = null;
        var seen = new HashSet<Window>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            var dialog = App.Instance.Windows.Cast<Window>().FirstOrDefault(candidate => candidate.IsVisible
                && candidate is PromptDialog or ConfirmDialog && !seen.Contains(candidate));
            if (dialog == null) return;
            if (DateTime.UtcNow > deadline || answered >= responses.Length)
            {
                failure ??= new InvalidOperationException("An unexpected manager dialog remained open.");
                dialog.Close();
                return;
            }
            seen.Add(dialog);
            try { responses[answered++](dialog); }
            catch (Exception error) { failure = error; dialog.Close(); }
        };
        timer.Start();
        try
        {
            await button.Dispatcher.InvokeAsync(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))).Task.WaitAsync(TimeSpan.FromSeconds(15));
            // Album storage work runs in the background, so its dialogs can follow the click.
            while (DateTime.UtcNow < deadline && failure == null
                && (answered < responses.Length || App.Instance.Windows.OfType<MainWindow>().Any(w => w.TestAlbumBusy)))
                await Task.Delay(25);
            if (failure != null) throw failure;
            Check(answered == responses.Length, "The manager did not show every expected dialog.");
            await button.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(70);
        }
        finally { timer.Stop(); }
    }

    private static void ManagerText(Window dialog, string value)
    {
        Check(dialog is PromptDialog, "The manager must display a text prompt.");
        ((TextBox)dialog.FindName("InputText")).Text = value;
        ManagerAccept<PromptDialog>(dialog);
    }

    private static void ManagerPassword(Window dialog, string value)
    {
        Check(dialog is PromptDialog, "The manager must display a password prompt.");
        ((PasswordBox)dialog.FindName("PwBox")).Password = value;
        ManagerAccept<PromptDialog>(dialog);
    }

    private static void ManagerAccept<T>(Window dialog) where T : Window
    {
        Check(dialog is T, "The manager displayed an unexpected dialog type.");
        ((Button)dialog.FindName("OkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void ManagerCancel<T>(Window dialog) where T : Window
    {
        Check(dialog is T, "The manager displayed an unexpected dialog type.");
        ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static IEnumerable<DependencyObject> ManagerVisuals(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in ManagerVisuals(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
