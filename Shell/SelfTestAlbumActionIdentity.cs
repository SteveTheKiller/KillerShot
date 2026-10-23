using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestAlbumActionIdentity
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-album-action-identity") return false;
        string folder = Path.GetFullPath(args[1]);
        var report = new List<string>();
        int exit = 1;
        try
        {
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            LocaleManager.Apply(Locale.EnUS);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            string albumA = CaptureLibrary.FilePath;
            CaptureLibrary.AddNow(Picture(20), DateTime.Now, "region", "Album A", null);
            var itemA = CaptureLibrary.Items().Single();
            string albumB = CaptureLibrary.CreateAlbum("B");
            CaptureLibrary.TrySwitchAlbum(albumB, null, out _);
            CaptureLibrary.AddNow(Picture(220), DateTime.Now, "region", "Album B", null);
            if (CaptureLibrary.Items().Single().Id != itemA.Id) throw new InvalidOperationException("Fixture IDs do not collide.");
            CaptureLibrary.TrySwitchAlbum(albumA, null, out _);
            itemA = CaptureLibrary.Items().Single();
            var view = new LibraryView();
            string destination = Path.Combine(folder, "A-export.png");
            view.PresentExportDialog = (dialog, owner) =>
            {
                CaptureLibrary.TrySwitchAlbum(albumB, null, out _);
                dialog.FileName = destination;
                return true;
            };
            view.SaveAs(itemA);
            if (File.Exists(destination)) throw new InvalidOperationException("PNG Save As wrote album B through an item from album A.");
            if (CaptureLibrary.Items().Single().Window != "Album B") throw new InvalidOperationException("Destination album changed.");
            report.Add("stale PNG Save As canceled at the actual dialog completion boundary PASS");
            CaptureLibrary.TrySwitchAlbum(albumA, null, out _);
            itemA = CaptureLibrary.Items().Single();
            view.PresentConfirmation = dialog =>
            {
                CaptureLibrary.TrySwitchAlbum(albumB, null, out _);
                return true;
            };
            view.Delete(itemA);
            if (CaptureLibrary.Items().Count != 1 || CaptureLibrary.Items().Single().Window != "Album B")
                throw new InvalidOperationException("A confirmed Delete from album A deleted album B's colliding capture.");
            report.Add("stale Delete canceled at the actual confirmation completion boundary PASS");
            CaptureLibrary.TrySwitchAlbum(albumA, null, out _);
            CaptureLibrary.SetNote(itemA.Id, "A note");
            itemA = CaptureLibrary.Items().Single();
            view.PresentPrompt = dialog =>
            {
                CaptureLibrary.TrySwitchAlbum(albumB, null, out _);
                return true;
            };
            view.EditNote(itemA);
            if (CaptureLibrary.Items().Single().Note != null) throw new InvalidOperationException("Album A's note was written into album B.");
            report.Add("stale note canceled after prompt PASS");
            foreach (string extension in new[] { "png", "jpg" })
            {
                CaptureLibrary.TrySwitchAlbum(albumA, null, out _);
                itemA = CaptureLibrary.Items().Single();
                string export = Path.Combine(folder, "stale." + extension);
                view.PresentExportDialog = (dialog, owner) =>
                {
                    CaptureLibrary.TrySwitchAlbum(albumB, null, out _);
                    dialog.FileName = export;
                    return true;
                };
                view.SaveAs(itemA);
                if (File.Exists(export)) throw new InvalidOperationException("Stale " + extension + " export was published.");
            }
            CaptureLibrary.TrySwitchAlbum(albumA, null, out _);
            long collectionA = CaptureLibrary.CreateCollection("A collection");
            long sessionA = CaptureLibrary.Session;
            itemA = CaptureLibrary.Items().Single();
            CaptureLibrary.TrySwitchAlbum(albumB, null, out _);
            long collectionB = CaptureLibrary.CreateCollection("B collection");
            if (collectionA != collectionB) throw new InvalidOperationException("Collection IDs do not collide.");
            foreach (string action in new[] { "rename", "delete" })
            {
                CaptureLibrary.TrySwitchAlbum(albumA, null, out _);
                sessionA = CaptureLibrary.Session;
                var menu = new System.Windows.Controls.ContextMenu();
                view.FillCollectionMenu(menu, collectionA, "A collection", sessionA);
                var command = menu.Items.OfType<System.Windows.Controls.MenuItem>()
                    .Single(m => Equals(m.Header, LocaleManager.Loc(action == "rename" ? "Str_Lib_RenameCollection" : "Str_Lib_DeleteCollection")));
                command.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                if (CaptureLibrary.FilePath != albumB || CaptureLibrary.Collections().Single().Name != "B collection")
                    throw new InvalidOperationException("Stale collection " + action + " changed album B.");
            }
            CaptureLibrary.TrySwitchAlbum(albumA, null, out _);
            itemA = CaptureLibrary.Items().Single();
            sessionA = itemA.Session;
            view.PresentPrompt = dialog =>
            {
                ((System.Windows.Controls.TextBox)dialog.FindName("InputText")).Text = "New A collection";
                CaptureLibrary.TrySwitchAlbum(albumB, null, out _);
                return true;
            };
            view.NewCollection(itemA);
            if (CaptureLibrary.Collections().Count != 1 || CaptureLibrary.Items().Single().CollectionId != null)
                throw new InvalidOperationException("Stale collection creation or assignment changed album B.");
            ExpectChanged(() => CaptureLibrary.Move([itemA.Id], collectionA, sessionA));
            ExpectChanged(() => CardColors.SetCollection(collectionA, Colors.Red, sessionA));
            if (CardColors.Collection(collectionB) != null) throw new InvalidOperationException("Stale color changed album B.");
            report.Add("PNG/JPEG, notes, collection create/rename/delete/assignment/color retain album identity PASS");
            var current = CaptureLibrary.Items().Single();
            CaptureLibrary.Move([current.Id], collectionB, current.Session);
            CaptureLibrary.SetNote(current.Id, "Current B note", current.Session);
            CaptureLibrary.RenameCollection(collectionB, "Current B collection", current.Session);
            if (CaptureLibrary.Items().Single().CollectionId != collectionB || CaptureLibrary.Items().Single().Note != "Current B note"
                || CaptureLibrary.Collections().Single().Name != "Current B collection") throw new InvalidOperationException("Valid bound mutations failed.");
            string valid = Path.Combine(folder, "valid.png");
            view.PresentExportDialog = (dialog, owner) => { dialog.FileName = valid; return true; };
            view.SaveAs(current);
            if (!File.Exists(valid) || ImageOutput.Load(valid).PixelWidth != 2) throw new InvalidOperationException("Valid bound export failed.");
            view.PresentExportDialog = (dialog, owner) => false;
            view.SaveAs(current);
            if (!File.Exists(valid)) throw new InvalidOperationException("Cancellation damaged the prior output.");
            report.Add("unchanged-album mutations/export and canceled export controls PASS");
            exit = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            CaptureLibrary.Close();
            File.WriteAllLines(Path.Combine(folder, "album-action-identity-report.txt"), report);
            App.Instance.Shutdown(exit);
        }
        return true;
    }

    private static void ExpectChanged(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("A stale session reached an album mutation.");
    }

    private static BitmapSource Picture(byte shade)
    {
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { shade, shade, shade, 255, shade, shade, shade, 255, shade, shade, shade, 255, shade, shade, shade, 255 }, 8);
        image.Freeze();
        return image;
    }
}
