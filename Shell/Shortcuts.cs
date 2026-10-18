using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShot.Editor;

// KillerShot keyboard shortcuts. Partial of MainWindow. ShortcutsOverlay.cs documents the same
// set from one table; change both together.
//
// Single-letter tool keys never fire while text is being typed, so everything bails out when a
// text field has focus. Modified combinations are checked first and are allowed regardless.
namespace KillerShot.Shell
{
    public partial class MainWindow
    {
        private static bool TypingInField()
            => Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is PasswordBox;

        private bool AnyOverlayOpen =>
            ShortcutsOverlay.Visibility == Visibility.Visible ||
            VideoSettingsOverlay.Visibility == Visibility.Visible ||
            AboutOverlay.Visibility == Visibility.Visible ||
            CaptureSettingsOverlay.Visibility == Visibility.Visible;

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // A shortcut recorder owns every key while it is listening.
            if (_recording != null) { RecorderKey(e); return; }
            // Alt+Shift+arrow docks the collection strip, or folds it on its own side.
            if (!AnyOverlayOpen && StripDock.HandleKey(e)) return;

            var mods = Keyboard.Modifiers;
            bool ctrl = (mods & ModifierKeys.Control) != 0;
            bool alt = (mods & ModifierKeys.Alt) != 0;
            bool shift = (mods & ModifierKeys.Shift) != 0;

            if (e.Key == Key.Escape)
            {
                if (CaptureSettingsOverlay.Visibility == Visibility.Visible) FadeOverlayOut(CaptureSettingsOverlay);
                else if (VideoSettingsOverlay.Visibility == Visibility.Visible) FadeOverlayOut(VideoSettingsOverlay);
                else if (ShortcutsOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutsOverlay);
                else if (AboutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(AboutOverlay);
                else if (_libraryShown && !TypingInField()) ToggleLibrary();   // back to the open image
                else if (!Editor.CancelGesture()) return;
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F1) { ToggleShortcutsOverlay(); e.Handled = true; return; }
            if (e.Key == Key.F12) { ShowAboutOverlay(); e.Handled = true; return; }
            if (AnyOverlayOpen) return;

            // Alt+M hides or shows the toolbar, as in KillerPDF. Alt combinations arrive as
            // Key.System with the letter in SystemKey.
            if (alt && !ctrl && e.Key == Key.System && e.SystemKey == Key.M)
            {
                ToggleToolbarHidden();
                e.Handled = true;
                return;
            }

            // Ctrl+Shift+1 to 6 pick the toolbar style: two icon sizes, then four text modes.
            if (ctrl && shift && !alt && e.Key is >= Key.D1 and <= Key.D6)
            {
                PickToolbarStyle(e.Key - Key.D0);
                e.Handled = true;
                return;
            }

            if (ctrl && !alt)
            {
                bool handled = true;
                switch (e.Key)
                {
                    case Key.N: StartCapture(Capture.CaptureMode.Region); break;
                    case Key.L: ToggleLibrary(); break;
                    case Key.F when Library.IsVisible: Library.FocusSearch(); break;
                    case Key.R: App.Instance.Video?.Toggle(); break;
                    case Key.O: OpenFile(); break;
                    case Key.S when shift: SaveAs(); break;
                    case Key.S: Save(); break;
                    case Key.C when !TypingInField(): CopyResult(); break;
                    case Key.P: PrintResult(); break;
                    case Key.Z when !TypingInField() && shift: Redo(); break;
                    case Key.Z when !TypingInField(): Undo(); break;
                    case Key.Y when !TypingInField(): Redo(); break;
                    case Key.D0 or Key.NumPad0: ZoomToFit(); break;
                    case Key.D1 or Key.NumPad1: SetZoom(1.0); break;
                    case Key.OemPlus or Key.Add: ZoomStep(+1); break;
                    case Key.OemMinus or Key.Subtract: ZoomStep(-1); break;
                    default: handled = false; break;
                }
                e.Handled = handled;
                return;
            }

            // Tool keys belong to the editor, not to the library over it.
            if (alt || TypingInField() || Editor.IsEditingText || Library.IsVisible) return;

            double step = shift ? 10 : 1;
            switch (e.Key)
            {
                case Key.V: SelectTool(ToolKind.Select); break;
                case Key.P: SelectTool(ToolKind.Pen); break;
                case Key.H: SelectTool(ToolKind.Highlighter); break;
                case Key.A: SelectTool(ToolKind.Arrow); break;
                case Key.R: SelectTool(ToolKind.Rectangle); break;
                case Key.E: SelectTool(ToolKind.Ellipse); break;
                case Key.T: SelectTool(ToolKind.Text); break;
                case Key.X: SelectTool(ToolKind.Redact); break;
                case Key.C: SelectTool(ToolKind.Crop); break;
                case Key.Delete or Key.Back: if (!Editor.DeleteSelected()) return; break;
                case Key.Left: if (!Editor.Nudge(new Vector(-step, 0))) return; break;
                case Key.Right: if (!Editor.Nudge(new Vector(step, 0))) return; break;
                case Key.Up: if (!Editor.Nudge(new Vector(0, -step))) return; break;
                case Key.Down: if (!Editor.Nudge(new Vector(0, step))) return; break;
                default: return;
            }
            e.Handled = true;
        }

        /// <summary>Windows delivers Print Screen only as a key-up, so the recorder listens here too.</summary>
        private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (_recording != null && (e.Key == Key.Snapshot || e.SystemKey == Key.Snapshot)) RecorderKey(e);
        }
    }
}
