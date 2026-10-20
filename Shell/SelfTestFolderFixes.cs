using System.IO;
using System.Windows;
using System.Windows.Controls;
using KillerFamily.Layout;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static partial class SelfTestLibrary
{
    private static async Task RunFolderFixes(string folder)
    {
        int code = 1;
        var report = new List<string>();
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new folder-fix test directory.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            FolderCaptures.UseFolder(Path.Combine(folder, "Screenshots"));
            FolderCaptures.Enabled = false;
            App.InitializeKit();
            string photos = Path.Combine(folder, "photos");
            Directory.CreateDirectory(photos);
            string source = FolderCaptures.AddFolder(photos);
            await FolderCaptures.RefreshFolderAsync(source);
            using var strip = new CollectionStrip(new DockHost(), new Style(typeof(Slider)));
            strip.SetSource(source);
            string? failureMessage = null;
            strip.StatusMessage += message => failureMessage = message;
            string settings = Path.Combine(folder, "settings", "settings.json");
            var attributes = File.GetAttributes(settings);
            try
            {
                File.SetAttributes(settings, attributes | FileAttributes.ReadOnly);
                var remove = strip.SourceMenu(new Border()).Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, LocaleManager.Loc("Str_FolderCollection_Remove")));
                remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                if (string.IsNullOrWhiteSpace(failureMessage) || strip.Source != source
                    || !FolderCaptures.LinkedCollections().Any(item => item.Source == source))
                    throw new InvalidOperationException("Failed removal must retain the selected, registered collection.");
            }
            finally { File.SetAttributes(settings, attributes); }
            report.Add("remove_command_handles_read_only_settings_and_preserves_selection=PASS");
            strip.SourceMenu(new Border()).Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, LocaleManager.Loc("Str_FolderCollection_Remove")))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (strip.Source != "all" || FolderCaptures.LinkedCollections().Any(item => item.Source == source))
                throw new InvalidOperationException("A successful removal must reset selection and remove only the saved link.");
            report.Add("remove_command_succeeds_after_settings_recover=PASS");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            File.WriteAllLines(Path.Combine(folder, "folder-fixes-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }
}
