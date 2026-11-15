using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace KillerShot.Files;

public partial class FilesView
{
    /// <summary>The host supplies formats it can edit. An explicit host action never uses shell fallback.</summary>
    public Func<string, bool>? CanOpenInHost { get; set; }

    public async Task<bool> OpenSelectionInHost()
    {
        if (ResultsList.SelectedItem is not FileEntry { IsDirectory: false } entry
            || CanOpenInHost?.Invoke(entry.FilePath) != true || Browser.OpenInEditor == null) return false;
        try { return await Browser.OpenInEditor(entry.FilePath); }
        catch (Exception error) { SetCommandError(error.Message); return false; }
    }

    public ContextMenu BuildFilesMenu(FileEntry? seed = null, bool background = false)
    {
        Dispatcher.VerifyAccess();
        if (!background && seed != null && !ResultsList.SelectedItems.Contains(seed))
        {
            ResultsList.SelectedItems.Clear();
            ResultsList.SelectedItems.Add(seed);
        }
        // The file card, KillerShell 1.6.0's pattern: what was clicked, the clipboard and edit
        // actions as a strip, then the rest as rows. ShellContextMenu stays the native menu.
        var menu = KillerFamily.Controls.CardBuilder.Menu(320);
        bool onItem = !background && (seed != null || ResultsList.SelectedItems.Count > 0);
        if (onItem)
        {
            int count = ResultsList.SelectedItems.Count;
            var first = ResultsList.SelectedItem as FileEntry;
            menu.Items.Add(count > 1
                ? KillerFamily.Controls.CardBuilder.Header(string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc("Str_Files_SelectedCount"), count), Navigation.CurrentFolder)
                : KillerFamily.Controls.CardBuilder.Header(first?.FileName ?? "", first == null ? null : first.IsDirectory || first.SizeLabel.Length == 0 ? first.TypeName : first.TypeName + "  ·  " + first.SizeLabel));
            var strip = KillerFamily.Controls.CardBuilder.QuickStrip(Loc("Str_Card_Quick"),
            [
                Quick("", "Str_Menu_Cut", () => CopySelection(true), "Ctrl+X", CanCut),
                Quick("", "Str_Menu_Copy", () => CopySelection(), "Ctrl+C"),
                Quick("", "Str_Menu_Paste", () => StartCommand(PasteFiles()), "Ctrl+V", CanPaste),
                Quick("", "Str_Menu_Rename", () => BeginRename(), "F2", CanRename),
                Quick("", "Str_Btn_Delete", () => StartCommand(RecycleSelection()), "Delete", CanRecycle),
            ]);
            // Tags ride on Uid so the command surface reads the same on buttons and rows.
            string[] stripTags = ["cut", "copy", "paste", "rename", "delete"];
            int index = 0;
            foreach (var button in StripButtons(strip)) button.Uid = stripTags[index++];
            menu.Items.Add(strip);
            Add("open", "Str_Btn_Open", () =>
            {
                if (ResultsList.SelectedItem is FileEntry entry) StartCommand(Browser.ActivateEntry(entry));
            }, "Enter", glyph: "");
            if (ResultsList.SelectedItem is FileEntry { IsDirectory: false } entry && CanOpenInHost?.Invoke(entry.FilePath) == true)
                Add("host", "Str_Files_OpenInShot", () => StartCommand(OpenSelectionInHost()), glyph: "");
            Add("explorer", "Str_Menu_ShowExplorer", () =>
            {
                if (SelectedPaths.FirstOrDefault() is { } path)
                    Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = "/select,\"" + path + "\"" });
            }, glyph: "");
        }
        else
        {
            menu.Items.Add(KillerFamily.Controls.CardBuilder.Header(Path.GetFileName(Navigation.CurrentFolder.TrimEnd('\\')) is { Length: > 0 } name ? name : Navigation.CurrentFolder, Navigation.CurrentFolder));
            Add("paste", "Str_Menu_Paste", () => StartCommand(PasteFiles()), "Ctrl+V", CanPaste, "");
        }
        Add("newfolder", "Str_Menu_NewFolder", () => StartCommand(NewFolder()), "Ctrl+Shift+N", CanPaste, "");
        if (onItem)
        {
            menu.Items.Add(new Separator());
            string[] paths = SelectedPaths.ToArray();
            Add("shell", "Str_Menu_ShellMenu", () => AfterMenu(() =>
            {
                var owner = Window.GetWindow(this);
                if (ShellContextMenu.SpansFolders(paths))
                    SetCommandError(TryFindResource("Str_Status_ShellMixedFolders") as string
                        ?? "The shell menu works on items from one folder at a time.");
                else if (owner == null || !ShellContextMenu.Show(owner, paths)) SetCommandError(Loc("Str_Status_ShellFailed"));
            }), "Shift+F10", glyph: "");
            Add("properties", "Str_Menu_Properties", () => AfterMenu(() =>
            {
                if (paths.FirstOrDefault() is { } path && !ShellContextMenu.ShowProperties(path)) SetCommandError(Loc("Str_Status_ShellFailed"));
            }), "Alt+Enter", glyph: "");
        }
        menu.Items.Add(new Separator());
        Add("refresh", "Str_Btn_Reload", () => StartCommand(NavigateTo(Navigation.CurrentFolder)), "F5", glyph: "");
        return menu;

        Action Guarded(Action action) => () =>
        {
            try { action(); }
            catch (Exception error) { SetCommandError(error.Message); }
        };
        KillerFamily.Controls.CardAction Quick(string glyph, string key, Action action, string gesture, bool enabled = true) =>
            new(glyph, Loc(key), Loc(key), gesture, Guarded(action), enabled);
        void Add(string tag, string key, Action action, string gesture = "", bool enabled = true, string? glyph = null)
        {
            var item = KillerFamily.Controls.CardBuilder.Row(Loc(key), gesture, glyph, Guarded(action), enabled);
            item.Tag = tag;
            menu.Items.Add(item);
        }
    }

    /// <summary>The quick buttons on a card strip, in order.</summary>
    internal static IEnumerable<Button> StripButtons(MenuItem strip) =>
        strip.Header is Border { Child: System.Windows.Controls.Primitives.UniformGrid grid } ? grid.Children.OfType<Button>() : [];

    private void AfterMenu(Action action) => Dispatcher.BeginInvoke(action, DispatcherPriority.Background);

    private void InitFilesMenu()
    {
        ResultsList.ContextMenu = new ContextMenu();
        ResultsList.ContextMenuOpening += (_, e) =>
        {
            bool keyboard = e.CursorLeft < 0 && e.CursorTop < 0;
            var seed = keyboard ? ResultsList.SelectedItem as FileEntry
                : ItemUnder(e.OriginalSource as DependencyObject) is { } row ? DataFor(row)
                : ItemUnder(Mouse.DirectlyOver as DependencyObject) is { } over ? DataFor(over) : null;
            ResultsList.ContextMenu = BuildFilesMenu(seed, !keyboard && seed == null);
        };
        ResultsList.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (ItemUnder(e.OriginalSource as DependencyObject) is { } row && DataFor(row) is { } entry
                && !ResultsList.SelectedItems.Contains(entry))
            {
                ResultsList.SelectedItems.Clear();
                ResultsList.SelectedItems.Add(entry);
            }
        };
    }
}
