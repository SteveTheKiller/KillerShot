using System.Windows;

namespace KillerShot.Shell;

// Back and forward, from the toolbar's Back button, Alt+Left and the mouse's side buttons, the way
// KillerShell and KillerPDF retrace history. An open image goes back to the library it came
// from; in Files the buttons walk the folder history, and forward from the place the image was
// left returns to that image.
//
// The mouse arrives as WM_XBUTTONUP, or as WM_APPCOMMAND from a keyboard or a mouse driver that
// sends browser back and forward instead. Only the window message navigates, never the WPF
// mouse-down as well, so one press moves one step (KillerShell's MouseHistoryStepTests).
public partial class MainWindow
{
    private const int WM_XBUTTONUP = 0x020C;
    private const int WM_APPCOMMAND = 0x0319;
    private const int XBUTTON1 = 1;
    private const int XBUTTON2 = 2;
    private const int APPCOMMAND_BROWSER_BACKWARD = 1;
    private const int APPCOMMAND_BROWSER_FORWARD = 2;

    /// <summary>-1 for back, 1 for forward, 0 for any other message.</summary>
    internal static int MouseHistoryDirection(int msg, IntPtr wParam, IntPtr lParam)
    {
        int command;
        if (msg == WM_XBUTTONUP) command = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
        else if (msg == WM_APPCOMMAND) command = (int)((lParam.ToInt64() >> 16) & 0x0FFF);
        else return 0;
        if (msg == WM_XBUTTONUP ? command == XBUTTON1 : command == APPCOMMAND_BROWSER_BACKWARD) return -1;
        if (msg == WM_XBUTTONUP ? command == XBUTTON2 : command == APPCOMMAND_BROWSER_FORWARD) return 1;
        return 0;
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e) => HistoryStep(-1);

    // Where the library stood when it came up over the open picture. Forward returns to the picture
    // only from that same place: once the user moves to another view, collection or folder, the
    // picture is no longer the next step, the way a browser drops forward history on a new visit.
    private string? _pictureReturnPlace;

    private string LibraryPlace() => Library.CurrentView + "|" + Library.CurrentSource + "|"
        + (Library.FilesActive ? Library.Files.Navigation.CurrentFolder : "");

    /// <summary>Call whenever the library comes up over the open picture. Showing the library can
    /// move Files (a collection picked while the picture was up), so the place is read once that
    /// navigation has landed, not before.</summary>
    private async void RememberPictureReturn()
    {
        _pictureReturnPlace = null;
        if (_doc == null || !_libraryShown) return;
        var document = _doc;
        if (Library.FilesActive)
        {
            try { await Library.FilesNavigation; }
            catch (Exception) { }   // a failed folder still leaves Files somewhere to compare
        }
        if (ReferenceEquals(_doc, document) && _libraryShown) _pictureReturnPlace = LibraryPlace();
    }

    /// <summary>One step back or forward. Returns whether anything moved.</summary>
    internal bool HistoryStep(int direction)
    {
        // The same moments the keyboard is held off (Shortcuts.cs): an overlay, a video import,
        // a shortcut recording, or an album switch replacing the picture.
        if (AnyOverlayOpen || Services.CaptureLibrary.VideoBusy || _recording != null || _albumSwitchFrozen) return false;
        bool files = Library.IsVisible && Library.FilesActive;
        if (direction < 0)
        {
            if (_doc != null && !_libraryShown) { ToggleLibrary(); return true; }
            if (files && Library.Files.Navigation.CanGoBack) { _ = Library.Files.GoBack(); return true; }
            return false;
        }
        // The picture was opened from this very place, so it is newer than any folder ahead in the
        // Files history; that history only comes next once the user leaves this place.
        if (_doc != null && _libraryShown && _pictureReturnPlace == LibraryPlace()) { ToggleLibrary(); return true; }
        if (files && Library.Files.Navigation.CanGoForward) { _ = Library.Files.GoForward(); return true; }
        return false;
    }
}
