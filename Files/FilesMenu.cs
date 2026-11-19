using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KillerFamily.Controls;
using KillerShot.Services;

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
        // The file card, the family tile card: what was clicked, with Delete (to the Recycle Bin) at
        // the right end of the header, then three-across tile groups, most used first. A tile that
        // does not apply to this selection stays in its place, dimmed. ShellContextMenu stays the
        // native menu, opened from its own tile.
        var menu = KillerShot.Shell.ShotCards.Menu();
        bool onItem = !background && (seed != null || ResultsList.SelectedItems.Count > 0);
        var tags = new List<string>();
        CardTile Tile(string tag, string glyph, string key, Action action, string? keys = null, bool enabled = true)
        {
            tags.Add(tag);
            return new CardTile(glyph, Loc(key), keys, Guarded(action), false, enabled);
        }
        var groups = new List<IEnumerable<CardTile>>();
        if (onItem)
        {
            int count = ResultsList.SelectedItems.Count;
            var first = ResultsList.SelectedItem as FileEntry;
            string title = count > 1 ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc("Str_Files_SelectedCount"), count) : first?.FileName ?? "";
            string? meta = count > 1 ? Navigation.CurrentFolder
                : first == null ? null : first.IsDirectory || first.SizeLabel.Length == 0 ? first.TypeName : first.TypeName + "  ·  " + first.SizeLabel;
            menu.Items.Add(KillerShot.Shell.ShotCards.Header(menu, title, meta, Loc("Str_Btn_Delete"), "Delete",
                Guarded(() => StartCommand(RecycleSelection())), CanRecycle));
            bool host = ResultsList.SelectedItem is FileEntry { IsDirectory: false } file && CanOpenInHost?.Invoke(file.FilePath) == true;
            groups.Add(
            [
                Tile("open", "\uE8E5", "Str_Btn_Open", () =>
                {
                    if (ResultsList.SelectedItem is FileEntry entry) StartCommand(Browser.ActivateEntry(entry));
                }, "Enter"),
                Tile("host", "\uE70F", "Str_Files_OpenInShot", () => StartCommand(OpenSelectionInHost()), enabled: host),
                Tile("explorer", "\uE838", "Str_Menu_ShowExplorer", () =>
                {
                    if (SelectedPaths.FirstOrDefault() is { } path)
                        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = "/select,\"" + path + "\"" });
                }),
            ]);
            groups.Add(
            [
                Tile("cut", "\uE8C6", "Str_Menu_Cut", () => CopySelection(true), "Ctrl+X", CanCut),
                Tile("copy", "\uE8C8", "Str_Menu_Copy", () => CopySelection(), "Ctrl+C"),
                Tile("paste", "\uE77F", "Str_Menu_Paste", () => StartCommand(PasteFiles()), "Ctrl+V", CanPaste),
            ]);
            var edit = new List<CardTile>
            {
                Tile("rename", "\uE8AC", "Str_Menu_Rename", () => BeginRename(), "F2", CanRename),
                Tile("newfolder", "\uE8F4", "Str_Menu_NewFolder", () => StartCommand(NewFolder()), "Ctrl+Shift+N", CanPaste),
            };
            if (KillerPdfHandoff.IsInstalled)
            {
                var folderEntry = count == 1 ? ResultsList.SelectedItem as FileEntry : null;
                edit.Add(Tile("makepdf", "\uEA90", "Str_Menu_MakePdf", () => { if (folderEntry != null) MakePdf(folderEntry.FilePath); },
                    enabled: folderEntry is { IsDirectory: true }));
            }
            groups.Add(edit);
            string[] paths = SelectedPaths.ToArray();
            groups.Add(
            [
                Tile("shell", "\uE712", "Str_Menu_ShellMenu", () => AfterMenu(() =>
                {
                    var owner = Window.GetWindow(this);
                    if (ShellContextMenu.SpansFolders(paths))
                        SetCommandError(TryFindResource("Str_Status_ShellMixedFolders") as string
                            ?? "The shell menu works on items from one folder at a time.");
                    else if (owner == null || !ShellContextMenu.Show(owner, paths)) SetCommandError(Loc("Str_Status_ShellFailed"));
                }), "Shift+F10"),
                Tile("properties", "\uE946", "Str_Menu_Properties", () => AfterMenu(() =>
                {
                    if (paths.FirstOrDefault() is { } path && !ShellContextMenu.ShowProperties(path)) SetCommandError(Loc("Str_Status_ShellFailed"));
                }), "Alt+Enter"),
                Tile("refresh", "\uE72C", "Str_Btn_Reload", () => StartCommand(NavigateTo(Navigation.CurrentFolder)), "F5"),
            ]);
        }
        else
        {
            menu.Items.Add(KillerShot.Shell.ShotCards.Header(menu,
                Path.GetFileName(Navigation.CurrentFolder.TrimEnd('\\')) is { Length: > 0 } name ? name : Navigation.CurrentFolder, Navigation.CurrentFolder));
            var here = new List<CardTile>
            {
                Tile("paste", "\uE77F", "Str_Menu_Paste", () => StartCommand(PasteFiles()), "Ctrl+V", CanPaste),
                Tile("newfolder", "\uE8F4", "Str_Menu_NewFolder", () => StartCommand(NewFolder()), "Ctrl+Shift+N", CanPaste),
            };
            if (KillerPdfHandoff.IsInstalled)
            {
                string folder = Navigation.CurrentFolder;
                here.Add(Tile("makepdf", "\uEA90", "Str_Menu_MakePdf", () => MakePdf(folder)));
            }
            groups.Add(here);
            groups.Add([Tile("refresh", "\uE72C", "Str_Btn_Reload", () => StartCommand(NavigateTo(Navigation.CurrentFolder)), "F5")]);
        }
        var grid = KillerShot.Shell.ShotCards.Grid(menu, [.. groups]);
        menu.Items.Add(grid);
        // Tags ride on Uid so the self-tests read the commands the same way on every card.
        int index = 0;
        foreach (var button in StripButtons(grid)) button.Uid = tags[index++];
        return menu;

        Action Guarded(Action action) => () =>
        {
            try { action(); }
            catch (Exception error) { SetCommandError(error.Message); }
        };
    }

    /// <summary>Make PDF: KillerPDF opens the folder's pictures as one new document.</summary>
    private void MakePdf(string folder)
    {
        string? problem = KillerPdfHandoff.MessageKey(KillerPdfHandoff.MakePdf(folder));
        if (problem != null) SetCommandError(Loc(problem));
    }

    /// <summary>The tiles in a card's grid item (or a header's buttons), in order.</summary>
    internal static IEnumerable<Button> StripButtons(MenuItem strip) =>
        KillerShot.Shell.ShotCards.ButtonsIn(strip);

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
