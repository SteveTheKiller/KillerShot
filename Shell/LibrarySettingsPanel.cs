using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShot.Controls;
using KillerShot.Services;

// The title-bar library settings: how long captures are kept, and the optional
// password. Without a password the library opens with a random key sealed to the Windows
// account (DPAPI). With one, the password is the key and nothing else can open the file.
namespace KillerShot.Shell
{
    public partial class MainWindow
    {
        private static readonly string[] KeepChoices =
            ["all", "count:100", "count:500", "count:1000", "count:5000", "days:30", "days:90", "days:365"];

        private void LibrarySettingsButton_Click(object sender, RoutedEventArgs e) => ShowLibrarySettings();

        private void ShowLibrarySettings()
        {
            if (LibrarySettingsPopup.IsOpen) { LibrarySettingsPopup.IsOpen = false; return; }
            _syncingOptions = true;
            try { SyncLibraryOptions(); }
            finally { _syncingOptions = false; }
            LibrarySettingsPopup.IsOpen = true;
            Dispatcher.BeginInvoke(new Action(() => LibraryKeepCombo.Focus()));
        }

        private void LibrarySettingsPopup_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            LibrarySettingsPopup.IsOpen = false;
            e.Handled = true;
        }

        private void LibrarySettingsPopup_Closed(object sender, EventArgs e)
        {
            if (IsActive) LibrarySettingsButton.Focus();
        }

        private void SyncLibraryOptions()
        {
            LibraryKeepCombo.Items.Clear();
            foreach (var keep in KeepChoices)
            {
                var (kind, n) = CaptureLibrary.ParseKeep(keep);
                string text = kind switch
                {
                    "count" => string.Format(Loc("Str_CS_KeepCount"), n.ToString("N0")),
                    "days" => string.Format(Loc("Str_CS_KeepDays"), n),
                    _ => Loc("Str_CS_KeepAll"),
                };
                LibraryKeepCombo.Items.Add(new ComboBoxItem { Content = text, Tag = keep });
            }
            int i = Array.IndexOf(KeepChoices, CaptureLibrary.Keep);
            if (i < 0)
            {
                // A limit set by hand in settings.json stays selected as it is.
                LibraryKeepCombo.Items.Add(new ComboBoxItem { Content = CaptureLibrary.Keep, Tag = CaptureLibrary.Keep });
                i = LibraryKeepCombo.Items.Count - 1;
            }
            LibraryKeepCombo.SelectedIndex = i;
            LibraryFolderToggle.Content = string.Format(Loc("Str_CS_ShowFolder"), FolderCaptures.Name);
            LibraryFolderToggle.ToolTip = FolderCaptures.Folder;
            LibraryFolderToggle.IsChecked = FolderCaptures.Enabled;
            RefreshLibraryStatus();
        }

        private void RefreshLibraryStatus(string? error = null)
        {
            bool open = CaptureLibrary.IsOpen, pw = CaptureLibrary.HasPassword;
            LibraryStatusText.SetResourceReference(TextBlock.ForegroundProperty, error != null ? "DangerBrush" : "DimTextBrush");
            LibraryStatusText.Text = error
                ?? (!open && CaptureLibrary.NeedsPassword ? Loc("Str_CS_LibraryLocked")
                : !open ? Loc("Str_Lib_NotOpen")
                : pw ? Loc("Str_CS_LibraryPassword") : Loc("Str_CS_LibraryAccount"));
            LibraryPasswordBtn.Content = Loc(pw ? "Str_CS_ChangePassword" : "Str_CS_SetPassword");
            LibraryPasswordBtn.IsEnabled = open;
            LibraryRemovePasswordBtn.Visibility = pw ? Visibility.Visible : Visibility.Collapsed;
            LibraryRemovePasswordBtn.IsEnabled = open;
        }

        private void LibraryFolderToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingOptions) return;
            FolderCaptures.Enabled = LibraryFolderToggle.IsChecked == true;
        }

        private void LibraryKeepCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingOptions || LibraryKeepCombo.SelectedItem is not ComboBoxItem { Tag: string keep }) return;
            if (keep != CaptureLibrary.Keep) CaptureLibrary.Keep = keep;
        }

        /// <summary>A password already set must be typed again before it changes or goes.</summary>
        private bool ConfirmCurrentLibraryPassword()
        {
            if (!CaptureLibrary.HasPassword) return true;
            var dlg = new PromptDialog(Loc("Str_CS_CurrentPasswordTitle"), "", Loc("Str_Lib_Unlock"), kind: PromptKind.Password) { Owner = this };
            if (dlg.ShowDialog() != true) return false;
            if (CaptureLibrary.CheckPassword(dlg.Password)) return true;
            RefreshLibraryStatus(Loc("Str_Lib_WrongPassword"));
            return false;
        }

        private void LibraryPassword_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmCurrentLibraryPassword()) return;
            var dlg = new PromptDialog(Loc("Str_CS_SetPasswordTitle"), Loc("Str_CS_SetPasswordDetail"), Loc("Str_CS_SetPasswordOk"), kind: PromptKind.NewPassword) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            ApplyLibraryPassword(dlg.Password);
        }

        private void LibraryRemovePassword_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmCurrentLibraryPassword()) return;
            var dlg = new ConfirmDialog(Loc("Str_CS_RemovePasswordTitle"), Loc("Str_CS_RemovePasswordDetail"),
                Loc("Str_CS_RemovePassword"), Loc("Str_Btn_Cancel")) { Owner = this };
            dlg.ShowDialog();
            if (dlg.Confirmed) ApplyLibraryPassword(null);
        }

        private void ApplyLibraryPassword(string? password)
        {
            try
            {
                using (new WaitCursorScope()) CaptureLibrary.SetPassword(password);
                RefreshLibraryStatus();
            }
            catch (Exception ex)
            {
                RefreshLibraryStatus(string.Format(Loc("Str_CS_PasswordFailed"), ex.Message));
            }
        }

        /// <summary>Shows the wait cursor while the library file is rewritten.</summary>
        private sealed class WaitCursorScope : IDisposable
        {
            private readonly System.Windows.Input.Cursor? _old = System.Windows.Input.Mouse.OverrideCursor;
            public WaitCursorScope() => System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            public void Dispose() => System.Windows.Input.Mouse.OverrideCursor = _old;
        }
    }
}
