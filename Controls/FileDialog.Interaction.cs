using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerShot.Controls
{
    public partial class FileDialog
    {
        // ── Accept / cancel ──────────────────────────────────────────────────────

        internal Func<string, bool> AcceptDirectoryExists { get; set; } = Directory.Exists;
        internal Func<string, bool> AcceptFileExists { get; set; } = File.Exists;
        private bool _accepting;

        private void OK_Click(object sender, RoutedEventArgs e) => Accept();

        /// <summary>
        /// Resolves the name box to a full path and applies the mode's rules. Anything that fails
        /// leaves the dialog OPEN with focus back in the name box - a file dialog that closes on a
        /// bad name and makes you start over is the worst outcome.
        /// </summary>
        private async void Accept()
        {
            if (_dialogClosed || !_directoryReady || _accepting) return;
            _accepting = true;
            AcceptButton.IsEnabled = false;
            long version = _navigationVersion;
            string currentDir = _currentDir;
            string originalName = FileNameBox.Text ?? "";
            int filterIndex = FilterCombo.SelectedIndex;
            var selection = FileList.SelectedItem;
            bool Current() => !_dialogClosed && _directoryReady && version == _navigationVersion &&
                string.Equals(currentDir, _currentDir, StringComparison.OrdinalIgnoreCase) &&
                FileNameBox.Text == originalName && FilterCombo.SelectedIndex == filterIndex &&
                ReferenceEquals(FileList.SelectedItem, selection);

            try
            {
                if (_mode == FileDialogMode.Folder)
                {
                    bool exists = await Task.Run(() => AcceptDirectoryExists(currentDir));
                    if (!Current() || !exists) return;
                    FileName = currentDir;
                    DialogResult = true;
                    Close();
                    return;
                }

                var typed = originalName.Trim().Trim('"');
                if (typed.Length == 0)
                {
                    // Nothing typed but a file is highlighted: take that.
                    if (selection is PickerEntry sel && !sel.IsFolder) typed = sel.Name;
                    else { FileNameBox.Focus(); return; }
                }

                var full = Path.IsPathRooted(typed) ? typed : Path.Combine(currentDir, typed);

                if (_mode == FileDialogMode.Save)
                {
                    // The extension follows the active filter, with DefaultExt as fallback.
                    if (AddExtension && string.IsNullOrEmpty(Path.GetExtension(full)))
                    {
                        string? ext = ActiveFilterExt();
                        if (ext == null && !string.IsNullOrEmpty(DefaultExt))
                            ext = DefaultExt.StartsWith(".") ? DefaultExt : "." + DefaultExt;
                        if (ext != null) full += ext;
                    }

                    // The directory must exist; we do not silently create trees on the user's behalf.
                    var dir = Path.GetDirectoryName(full);
                    bool exists = !string.IsNullOrEmpty(dir) &&
                        await Task.Run(() => AcceptDirectoryExists(dir));
                    if (!Current()) return;
                    if (!exists)
                    {
                        SelMeta.Text = Loc("Str_Dlg_NoSuchFolder");
                        FileNameBox.Focus();
                        return;
                    }

                    if (OverwritePrompt)
                    {
                        bool fileExists = await Task.Run(() => AcceptFileExists(full));
                        if (!Current()) return;
                        if (fileExists)
                        {
                            // Empty detail collapses the optional line in ConfirmDialog.
                            var confirm = new ConfirmDialog(
                                string.Format(Loc("Str_Dlg_OverwriteMsg"), Path.GetFileName(full)),
                                "",
                                Loc("Str_Btn_Replace")) { Owner = this };
                            confirm.ShowDialog();
                            if (!confirm.Confirmed) { FileNameBox.Focus(); return; }
                        }
                    }
                }
                else if (CheckFileExists)
                {
                    bool fileExists = await Task.Run(() => AcceptFileExists(full));
                    if (!Current()) return;
                    if (!fileExists)
                    {
                        SelMeta.Text = Loc("Str_Dlg_NoSuchFile");
                        FileNameBox.Focus();
                        FileNameBox.SelectAll();
                        return;
                    }
                }

                if (!Current()) return;
                FileName = full;
                RememberAcceptedDirectory(currentDir);
                DialogResult = true;
                Close();
            }
            catch (IOException) { if (Current()) AcceptFailed(); }
            catch (UnauthorizedAccessException) { if (Current()) AcceptFailed(); }
            catch (ArgumentException) { if (Current()) AcceptFailed(); }
            catch (NotSupportedException) { if (Current()) AcceptFailed(); }
            finally
            {
                _accepting = false;
                if (!_dialogClosed) AcceptButton.IsEnabled = _directoryReady;
            }
        }

        private void AcceptFailed()
        {
            if (_dialogClosed) return;
            SelMeta.Text = Loc(_mode == FileDialogMode.Open ? "Str_Dlg_NoSuchFile" : "Str_Dlg_NoSuchFolder");
            FileNameBox.Focus();
        }

        private void RememberAcceptedDirectory(string currentDir)
        {
            if (currentDir.Length > 0 && string.Equals(currentDir, _lastValidDir, StringComparison.OrdinalIgnoreCase))
                Services.ThemeManager.SetSetting(
                    _mode == FileDialogMode.Open ? LastOpenKey : LastSaveKey, currentDir);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Handled, or the press bubbles on to Resize_MouseDown AFTER DragMove's modal loop
            // returns - by then the button is UP, and WM_NCLBUTTONDOWN with no button held puts
            // Windows into its sticky keyboard-style size loop: the window chases the mouse,
            // resizing, until a click. (2026-07-30)
            e.Handled = true;
            DragMove();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        // ---- edge resize, done by hand ----
        //
        // This dialog carries no shell:WindowChrome - on an AllowsTransparency window it fills its
        // own non-client area and that paints as a flat band around the card. So the 10px halo does
        // the job instead: work out which edge the pointer is in and hand the drag to Windows with
        // WM_NCLBUTTONDOWN, exactly as Shell/Chrome.cs does for the main window's corner grip.
        // Windows then runs its own resize loop, so this gets the real snapping and live preview
        // rather than a hand-rolled approximation. (2026-07-30)

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                          HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        /// <summary>Width of the grab band, matching the ResizeBorderThickness WindowChrome used.</summary>
        private const double ResizeEdge = 8;

        /// <summary>Which edge the pointer is in, or 0 for none.</summary>
        private int HitTestEdge(Point p)
        {
            bool left   = p.X <= ResizeEdge;
            bool right  = p.X >= ActualWidth  - ResizeEdge;
            bool top    = p.Y <= ResizeEdge;
            bool bottom = p.Y >= ActualHeight - ResizeEdge;

            if (top && left)     return HTTOPLEFT;
            if (top && right)    return HTTOPRIGHT;
            if (bottom && left)  return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (left)            return HTLEFT;
            if (right)           return HTRIGHT;
            if (top)             return HTTOP;
            if (bottom)          return HTBOTTOM;
            return 0;
        }

        private void Resize_MouseMove(object sender, MouseEventArgs e)
        {
            Cursor = HitTestEdge(e.GetPosition(this)) switch
            {
                HTLEFT or HTRIGHT           => Cursors.SizeWE,
                HTTOP or HTBOTTOM           => Cursors.SizeNS,
                HTTOPLEFT or HTBOTTOMRIGHT  => Cursors.SizeNWSE,
                HTTOPRIGHT or HTBOTTOMLEFT  => Cursors.SizeNESW,
                _                           => Cursors.Arrow,
            };
        }

        private void Resize_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Only a press on the halo Grid ITSELF may start a resize. Every press on the card
            // bubbles up here too (with OriginalSource somewhere in the card's tree), and a stale
            // bubbled press must never reach WM_NCLBUTTONDOWN - see TitleBar_MouseLeftButtonDown.
            if (!ReferenceEquals(e.OriginalSource, sender)) return;
            int ht = HitTestEdge(e.GetPosition(this));
            if (ht == 0) return;
            e.Handled = true;
            SendMessage(new System.Windows.Interop.WindowInteropHelper(this).Handle,
                        WM_NCLBUTTONDOWN, new IntPtr(ht), IntPtr.Zero);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
