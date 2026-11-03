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
        var menu = new ContextMenu();
        menu.SetResourceReference(Control.FontFamilyProperty, "MenuFontFamily");
        bool onItem = !background && (seed != null || ResultsList.SelectedItems.Count > 0);
        if (onItem)
        {
            Add("open", "Str_Btn_Open", () =>
            {
                if (ResultsList.SelectedItem is FileEntry entry) StartCommand(Browser.ActivateEntry(entry));
            }, "Enter");
            if (ResultsList.SelectedItem is FileEntry { IsDirectory: false } entry && CanOpenInHost?.Invoke(entry.FilePath) == true)
                Add("host", "Str_Files_OpenInShot", () => StartCommand(OpenSelectionInHost()));
            Add("explorer", "Str_Menu_ShowExplorer", () =>
            {
                if (SelectedPaths.FirstOrDefault() is { } path)
                    Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = "/select,\"" + path + "\"" });
            });
            menu.Items.Add(new Separator());
            Add("cut", "Str_Menu_Cut", () => CopySelection(true), "Ctrl+X", CanCut);
            Add("copy", "Str_Menu_Copy", () => CopySelection(), "Ctrl+C");
        }
        Add("paste", "Str_Menu_Paste", () => StartCommand(PasteFiles()), "Ctrl+V", CanPaste);
        if (onItem) Add("rename", "Str_Menu_Rename", () => BeginRename(), "F2", CanRename);
        Add("newfolder", "Str_Menu_NewFolder", () => StartCommand(NewFolder()), "Ctrl+Shift+N", CanPaste);
        if (onItem)
        {
            Add("delete", "Str_Btn_Delete", () => StartCommand(RecycleSelection()), "Delete", CanRecycle);
            menu.Items.Add(new Separator());
            string[] paths = SelectedPaths.ToArray();
            Add("shell", "Str_Menu_ShellMenu", () => AfterMenu(() =>
            {
                var owner = Window.GetWindow(this);
                if (owner == null || !ShellContextMenu.Show(owner, paths)) SetCommandError(Loc("Str_Status_ShellFailed"));
            }));
            Add("properties", "Str_Menu_Properties", () => AfterMenu(() =>
            {
                if (paths.FirstOrDefault() is { } path && !ShellContextMenu.ShowProperties(path)) SetCommandError(Loc("Str_Status_ShellFailed"));
            }));
        }
        menu.Items.Add(new Separator());
        Add("refresh", "Str_Btn_Reload", () => StartCommand(NavigateTo(Navigation.CurrentFolder)));
        return menu;

        void Add(string tag, string key, Action action, string gesture = "", bool enabled = true)
        {
            var item = new MenuItem { Tag = tag, InputGestureText = gesture, IsEnabled = enabled };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, key);
            item.Click += (_, _) =>
            {
                try { action(); }
                catch (Exception error) { SetCommandError(error.Message); }
            };
            menu.Items.Add(item);
        }
    }

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
