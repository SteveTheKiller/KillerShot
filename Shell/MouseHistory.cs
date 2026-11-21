using System.Windows;

namespace KillerShot.Shell;

// Back and forward, from the editor's Back button and the mouse's side buttons, the way
// KillerShell and KillerPDF retrace history. An open image goes back to the library it came
// from; in Files the buttons walk the folder history first, and forward from the library over an
// open image returns to that image.
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

    private void EditorBack_Click(object sender, RoutedEventArgs e) => HistoryStep(-1);

    /// <summary>One step back or forward. Returns whether anything moved.</summary>
    internal bool HistoryStep(int direction)
    {
        if (AnyOverlayOpen) return false;
        bool files = Library.IsVisible && Library.FilesActive;
        if (direction < 0)
        {
            if (_doc != null && !_libraryShown) { ToggleLibrary(); return true; }
            if (files && Library.Files.Navigation.CanGoBack) { _ = Library.Files.GoBack(); return true; }
            return false;
        }
        if (files && Library.Files.Navigation.CanGoForward) { _ = Library.Files.GoForward(); return true; }
        if (_doc != null && _libraryShown) { ToggleLibrary(); return true; }
        return false;
    }
}
