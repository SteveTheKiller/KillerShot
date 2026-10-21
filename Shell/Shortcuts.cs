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
            // An album switch is replacing the picture: no shortcut may change it meanwhile.
            if (_albumSwitchFrozen) { e.Handled = true; return; }
            // Alt+Shift+arrow docks the collection strip, or folds it on its own side.
            var dockKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (!AnyOverlayOpen && (dockKey is Key.Left or Key.Right ? NavigationDock.HandleKey(e)
                : StripDock.HandleKey(e))) return;

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

            // F5 to F8 pick the library's view, as they pick KillerPDF's view mode, and bring the
            // library up over an open picture.
            if (!ctrl && !alt && !shift && e.Key is >= Key.F5 and <= Key.F8)
            {
                ShowLibraryView(e.Key switch { Key.F5 => "shelves", Key.F6 => "flow", Key.F7 => "grid", _ => "timeline" });
                e.Handled = true;
                return;
            }

            // Alt+Shift+G, the family Film Grain chord. A text field takes no Alt chords, so it is
            // safe while typing.
            if (alt && shift && !ctrl && e.Key == Key.System && e.SystemKey == Key.G)
            {
                if (!e.IsRepeat) _themePicker.ToggleFilmGrain();
                e.Handled = true;
                return;
            }

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
                    case Key.Enter when InReview && !TypingInField(): AcceptReview(); break;
                    case Key.L: ToggleLibrary(); break;
                    case Key.F when Library.IsVisible: Library.FocusSearch(); break;
                    case Key.R when shift: if (!e.IsRepeat) App.Instance.Video?.TogglePause(); break;
                    case Key.D when shift:
                        if (!e.IsRepeat && App.Instance.Video is { IsRecording: true } recording) _ = recording.StopAsync(true);
                        break;
                    case Key.R: if (!e.IsRepeat) App.Instance.Video?.Toggle(); break;
                    case Key.O: OpenFile(); break;
                    case Key.S when shift: SaveAs(); break;
                    case Key.S: Save(); break;
                    // Over the album these act on the capture in view, never the hidden editor image.
                    case Key.C when shift && !TypingInField() && _doc != null && !Library.IsVisible: _ = ReadPictureTextAsync(null, copy: true); break;
                    case Key.C when !TypingInField(): if (Library.IsVisible) Library.CopyFocused(); else CopyResult(); break;
                    case Key.P: if (Library.IsVisible) Library.PrintFocused(); else PrintResult(); break;
                    case Key.Z when !TypingInField() && shift: Redo(); break;
                    case Key.Z when !TypingInField(): Undo(); break;
                    case Key.Y when !TypingInField(): Redo(); break;
                    case Key.V when !TypingInField():
                        if (shift || _doc == null || Library.IsVisible) PasteAsNewPicture(); else PasteIntoPicture();
                        break;
                    case Key.A when EditorKeysApply(): if (!Editor.SelectAll()) handled = false; break;
                    case Key.D when !shift && EditorKeysApply(): if (!Editor.DuplicateSelected()) handled = false; break;
                    case Key.OemCloseBrackets when EditorKeysApply(): if (!Editor.Reorder(+1, shift)) handled = false; break;
                    case Key.OemOpenBrackets when EditorKeysApply(): if (!Editor.Reorder(-1, shift)) handled = false; break;
                    case Key.Left when EditorKeysApply(): if (!Editor.ResizeSelected(new Vector(shift ? -10 : -1, 0))) handled = false; break;
                    case Key.Right when EditorKeysApply(): if (!Editor.ResizeSelected(new Vector(shift ? 10 : 1, 0))) handled = false; break;
                    case Key.Up when EditorKeysApply(): if (!Editor.ResizeSelected(new Vector(0, shift ? -10 : -1))) handled = false; break;
                    case Key.Down when EditorKeysApply(): if (!Editor.ResizeSelected(new Vector(0, shift ? 10 : 1))) handled = false; break;
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
            if (alt || TypingInField() || Editor.IsEditingText || Library.IsVisible || _strip?.IsKeyboardFocusWithin == true
                || _navigationStrip?.IsKeyboardFocusWithin == true) return;

            double step = shift ? 10 : 1;
            switch (e.Key)
            {
                case Key.V: SelectTool(ToolKind.Select); break;
                case Key.P: SelectTool(ToolKind.Pen); break;
                case Key.H: SelectTool(ToolKind.Highlighter); break;
                case Key.A: SelectTool(ToolKind.Arrow); break;
                case Key.L: SelectTool(ToolKind.Line); break;
                case Key.F: ToggleFill(); break;
                case Key.N: SelectTool(ToolKind.Step); break;
                case Key.B: SelectTool(ToolKind.Callout); break;
                case Key.R: SelectTool(ToolKind.Rectangle); break;
                case Key.E: SelectTool(ToolKind.Ellipse); break;
                case Key.T: SelectTool(ToolKind.Text); break;
                case Key.X: SelectTool(ToolKind.Redact); break;
                case Key.C: SelectTool(ToolKind.Crop); break;
                case Key.Delete or Key.Back: if (!Editor.DeleteSelected()) return; break;
                case Key.F2: if (!Editor.EditSelectedText()) return; break;
                // On the picture itself: Enter places the tool's mark, Tab moves between marks.
                case Key.Enter when Editor.IsKeyboardFocused: if (!Editor.PlaceMark()) return; break;
                case Key.Tab when Editor.IsKeyboardFocused && !ctrl: if (!Editor.SelectNext(shift)) return; break;
                case Key.Left: if (!Editor.Nudge(new Vector(-step, 0))) return; break;
                case Key.Right: if (!Editor.Nudge(new Vector(step, 0))) return; break;
                case Key.Up: if (!Editor.Nudge(new Vector(0, -step))) return; break;
                case Key.Down: if (!Editor.Nudge(new Vector(0, step))) return; break;
                default: return;
            }
            e.Handled = true;
        }

        /// <summary>Editor mark keys apply: the picture is showing and no text field has the keys.</summary>
        private bool EditorKeysApply() => _doc != null && !Library.IsVisible && !TypingInField() && !Editor.IsEditingText;

        /// <summary>Windows delivers Print Screen only as a key-up, so the recorder listens here too.</summary>
        private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (_recording != null && (e.Key == Key.Snapshot || e.SystemKey == Key.Snapshot)) RecorderKey(e);
        }
    }
}
