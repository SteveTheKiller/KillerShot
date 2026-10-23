using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Guest-only real modal dispatcher schedule. No screen capture or clipboard use.</summary>
internal static class SelfTestAlbumModalIdentity
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 3 || args[0] != "--selftest-album-modal-identity") return false;
        _ = RunAsync(Path.GetFullPath(args[1]), args[2]);
        return true;
    }

    private static async Task RunAsync(string folder, string action)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int exit = 1;
        FileStream? preparation = null;
        Task? switching = null;
        try
        {
            if (Environment.MachineName.ToUpperInvariant() is not ("SN01" or "SN02" or "KILLERLAB-W11"))
                throw new InvalidOperationException("This modal check is restricted to the assigned Windows test guests.");
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Path.Combine(folder, "Screenshots"));
            FolderCaptures.Enabled = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            App.CloseToTray = true;
            CaptureSettings.OutputFolder = folder;
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            string albumA = CaptureLibrary.FilePath;
            var image = SelfTestRecognitionPrivacy.Fixture();
            long idA = CaptureLibrary.AddNow(image, DateTime.Now, "region", "A capture", null);
            CaptureLibrary.SetNote(idA, "A note");
            long collectionA = CaptureLibrary.CreateCollection("A collection");
            string albumB = CaptureLibrary.CreateAlbum("B");
            CaptureLibrary.TrySwitchAlbum(albumB, null, out _);
            long idB = CaptureLibrary.AddNow(image, DateTime.Now, "region", "B capture", null);
            CaptureLibrary.SetNote(idB, "B note");
            long collectionB = CaptureLibrary.CreateCollection("B collection");
            if (idA != idB || collectionA != collectionB) throw new InvalidOperationException("The fixture did not produce colliding local IDs.");
            CaptureLibrary.TrySwitchAlbum(albumA, null, out _);
            var itemA = CaptureLibrary.Items().Single();
            App.Instance.ShowEditor();
            var window = App.Instance.EditorWindow!;
            var view = (LibraryView)window.FindName("Library");
            var menu = new ContextMenu();
            view.FillCollectionMenu(menu, collectionA, "A collection", itemA.Session);

            // The actual destination preparation blocks here while A's action opens its dialog.
            preparation = new FileStream(Path.ChangeExtension(albumB, ".open.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            switching = window.TestOpenAlbumAsync(albumB);
            if (!window.TestAlbumBusy) throw new InvalidOperationException("Destination preparation was not pending.");
            bool sawSwitchInsideModal = false;
            Exception? modalError = null;
            bool? CompleteModal(Window dialog, Func<bool?> show, Action accept)
            {
                var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                deadline.Tick += (_, _) => { modalError = new TimeoutException("Modal schedule timed out."); dialog.Close(); };
                dialog.Loaded += async (_, _) =>
                {
                    try
                    {
                        preparation.Dispose();
                        preparation = null;
                        await switching.WaitAsync(TimeSpan.FromSeconds(8));
                        sawSwitchInsideModal = dialog.IsVisible && CaptureLibrary.FilePath == albumB;
                        ((System.Windows.Controls.Primitives.Popup)window.FindName("LibrarySettingsPopup")).IsOpen = false;
                        if (dialog is FileDialog files)
                        {
                            var button = (Button)files.FindName("AcceptButton");
                            for (int i = 0; i < 200 && !button.IsEnabled; i++) await Task.Delay(10);
                            if (!button.IsEnabled) throw new InvalidOperationException("The real file dialog did not become ready.");
                        }
                        accept();
                    }
                    catch (Exception error) { modalError = error; dialog.Close(); }
                };
                deadline.Start();
                try { return show(); }
                finally { deadline.Stop(); }
            }
            string output = Path.Combine(folder, "A-export." + (action == "jpg" ? "jpg" : "png"));
            view.PresentExportDialog = (dialog, owner) => CompleteModal(dialog, () => dialog.ShowDialog(owner), () =>
            {
                ((TextBox)dialog.FindName("FileNameBox")).Text = output;
                ((Button)dialog.FindName("AcceptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            view.PresentConfirmation = dialog =>
            {
                CompleteModal(dialog, () => dialog.ShowDialog(), () => ((Button)dialog.FindName("OkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                return dialog.Confirmed;
            };
            view.PresentPrompt = dialog => CompleteModal(dialog, () => dialog.ShowDialog(), () =>
            {
                ((TextBox)dialog.FindName("InputText")).Text = "A action";
                ((Button)dialog.FindName("OkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            switch (action)
            {
                case "png": case "jpg": view.SaveAs(itemA); break;
                case "delete": view.Delete(itemA); break;
                case "note": view.EditNote(itemA); break;
                case "new-collection": view.NewCollection(itemA); break;
                case "rename-collection": case "delete-collection":
                    string key = action == "rename-collection" ? "Str_Lib_RenameCollection" : "Str_Lib_DeleteCollection";
                    menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, LocaleManager.Loc(key)))
                        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    break;
                default: throw new ArgumentException("Unsupported modal action.");
            }
            if (modalError != null) throw modalError;
            if (!sawSwitchInsideModal) throw new InvalidOperationException("B did not activate while A's real dialog remained visible.");
            if (File.Exists(output)) throw new InvalidOperationException("A's modal export published B's capture.");
            var remaining = CaptureLibrary.Items();
            if (remaining.Count != 1 || remaining[0].Window != "B capture" || remaining[0].Note != "B note" || remaining[0].CollectionId != null
                || CaptureLibrary.Collections().Count != 1 || CaptureLibrary.Collections()[0].Name != "B collection")
                throw new InvalidOperationException("A's modal action changed B's capture or collection metadata.");
            report.Add(action + ": real modal opened on A, pending preparation released, B activated inside nested dispatcher, confirmed stale action canceled PASS");
            exit = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            preparation?.Dispose();
            if (switching != null) try { await switching.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            App.Instance.EditorWindow?.Close();
            CaptureLibrary.Close();
            File.WriteAllLines(Path.Combine(folder, "album-modal-identity-report.txt"), report);
            App.Instance.Shutdown(exit);
        }
    }
}
