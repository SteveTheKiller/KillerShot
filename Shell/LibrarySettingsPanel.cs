using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;
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

        internal void TestPrepareAlbumManager(double height)
        {
            RefreshAlbumManager();
            LibrarySettingsCard.Width = 460;
            LibrarySettingsScroll.MaxHeight = height;
        }

        private void ShowLibrarySettings()
        {
            if (LibrarySettingsPopup.IsOpen) { LibrarySettingsPopup.IsOpen = false; return; }
            RefreshAlbumManager();
            LibrarySettingsCard.Width = Math.Max(200, Math.Min(460, ContentPane.ActualWidth - 16));
            LibrarySettingsScroll.MaxHeight = Math.Max(100, ContentPane.ActualHeight - 18);
            FlyoutPlacement.AttachInsidePane(LibrarySettingsPopup, LibrarySettingsButton, alignRight: true);
            LibrarySettingsPopup.IsOpen = true;
            Dispatcher.BeginInvoke(new Action(() => AlbumList.Focus()));
        }

        private void LibrarySettingsPopup_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N) { e.Handled = true; AlbumNew_Click(sender, e); return; }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { e.Handled = true; AlbumImport_Click(sender, e); return; }
            if (e.Key == Key.F2) { e.Handled = true; AlbumRename_Click(sender, e); return; }
            if (e.Key == Key.Enter && AlbumList.IsKeyboardFocusWithin) { e.Handled = true; AlbumOpen_Click(sender, e); return; }
            if (e.Key != Key.Escape) return;
            LibrarySettingsPopup.IsOpen = false;
            e.Handled = true;
        }

        private void LibrarySettingsPopup_Closed(object sender, EventArgs e)
        {
            if (IsActive) LibrarySettingsButton.Focus();
        }

        private bool? ShowAlbumDialog(Window dialog)
        {
            try { return dialog.ShowDialog(); }
            finally { LibrarySettingsPopup.IsOpen = true; }
        }

        private void RefreshAlbumManager(string? selectedPath = null)
        {
            _syncingOptions = true;
            try
            {
                selectedPath ??= (AlbumList.SelectedItem as ListBoxItem)?.Tag is CaptureLibrary.AlbumInfo selected ? selected.Path : CaptureLibrary.FilePath;
                AlbumList.Items.Clear();
                foreach (var album in CaptureLibrary.Albums())
                {
                    var content = new StackPanel { Margin = new Thickness(8, 5, 8, 5) };
                    var name = new TextBlock { Text = album.Name + (album.Active ? " (" + Loc("Str_Album_Active") + ")" : ""), FontWeight = album.Active ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
                    name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                    content.Children.Add(name);
                    var details = new TextBlock { Text = album.Modified.ToString("g") + "   " + (album.Bytes / 1024d).ToString("N0") + " KB", FontSize = 10, Margin = new Thickness(0, 3, 0, 0) };
                    details.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
                    content.Children.Add(details);
                    var row = new ListBoxItem { Content = content, Tag = album, ToolTip = album.Path };
                    System.Windows.Automation.AutomationProperties.SetName(row, name.Text);
                    AlbumList.Items.Add(row);
                    if (string.Equals(album.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) AlbumList.SelectedItem = row;
                }
                if (AlbumList.SelectedItem == null && AlbumList.Items.Count > 0) AlbumList.SelectedIndex = 0;
                ActiveAlbumHeading.Text = Path.GetFileNameWithoutExtension(CaptureLibrary.FilePath);
                SyncLibraryOptions();
                TitleLibraryLabel.Text = ActiveAlbumHeading.Text;
                TitleLibraryLabel.ToolTip = CaptureLibrary.FilePath;
            }
            finally { _syncingOptions = false; }
            AlbumList_SelectionChanged(AlbumList, null!);
        }

        private void AlbumList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AlbumOpenBtn == null || AlbumRenameBtn == null) return;
            var album = (AlbumList.SelectedItem as ListBoxItem)?.Tag as CaptureLibrary.AlbumInfo;
            AlbumOpenBtn.IsEnabled = album != null && (!album.Active || !CaptureLibrary.IsOpen);
            AlbumRenameBtn.IsEnabled = album != null;
        }

        private void AlbumList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(AlbumList, source) is ListBoxItem)
                AlbumOpen_Click(sender, e);
        }

        private void AlbumOpen_Click(object sender, RoutedEventArgs e)
        {
            if ((AlbumList.SelectedItem as ListBoxItem)?.Tag is CaptureLibrary.AlbumInfo album) _ = OpenAlbumAsync(album.Path);
        }

        private bool _albumBusy;
        internal bool TestAlbumBusy => _albumBusy;

        /// <summary>Runs album storage work off the interface thread, so the window keeps painting
        /// while a large album is copied, re-encrypted or waits for another process's lock. The
        /// album controls stay disabled until it finishes; confirmations stay on this thread.</summary>
        private async Task<T> AlbumWorkAsync<T>(Func<T> work)
        {
            _albumBusy = true;
            var content = LibrarySettingsPopup.Child;
            if (content != null) content.IsEnabled = false;
            RefreshLibraryStatus(Loc("Str_Album_Working"));
            try
            {
                using (new WaitCursorScope()) return await Task.Run(work);
            }
            finally
            {
                if (content != null) content.IsEnabled = true;
                _albumBusy = false;
            }
        }

        /// <summary>True from the moment the user lets the album switch replace the picture until the
        /// switch ends: the window takes no input, so no edit is made that the switch would drop.</summary>
        private bool _albumSwitchFrozen;
        internal bool TestAlbumSwitchFrozen => _albumSwitchFrozen;

        /// <summary>Asks about unsaved edits on the interface thread when album work needs to, then
        /// freezes the window for the short flush and swap that follow.</summary>
        private bool ConfirmReplacementFromWorker() => Dispatcher.Invoke(() =>
        {
            if (!App.Instance.ConfirmDocumentReplacement()) return false;
            _albumSwitchFrozen = true;
            if (Content is UIElement root) root.IsEnabled = false;
            return true;
        });

        private void ThawAfterAlbumSwitch()
        {
            if (!_albumSwitchFrozen) return;
            _albumSwitchFrozen = false;
            if (Content is UIElement root) root.IsEnabled = true;
        }

        internal Task TestOpenAlbumAsync(string path, string? password = null) => OpenAlbumAsync(path, password);

        private async Task OpenAlbumAsync(string path, string? password = null)
        {
            if (_albumBusy) return;
            try
            {
                if (string.Equals(path, CaptureLibrary.FilePath, StringComparison.OrdinalIgnoreCase) && CaptureLibrary.IsOpen) return;
                while (true)
                {
                    string? attempt = password;
                    var (opened, needsPassword) = await AlbumWorkAsync(() =>
                        (CaptureLibrary.TrySwitchAlbum(path, attempt, out bool locked, ConfirmReplacementFromWorker), locked));
                    RefreshLibraryStatus();
                    if (opened)
                    {
                        App.Instance.CompleteAlbumSwitch();
                        RefreshAlbumManager(path);
                        Library.Reload();
                        QrReadingButton.Tag = CaptureLibrary.QrReadingEnabled ? "on" : null;
                        return;
                    }
                    if (!needsPassword) return;
                    var dialog = new PromptDialog(string.Format(Loc("Str_Album_UnlockTitle"), Path.GetFileNameWithoutExtension(path)),
                        password != null ? Loc("Str_Lib_WrongPassword") : "", Loc("Str_Lib_Unlock"), kind: PromptKind.Password) { Owner = this };
                    if (ShowAlbumDialog(dialog) != true) return;
                    password = dialog.Password;
                }
            }
            catch (Exception error) { RefreshLibraryStatus(string.Format(Loc("Str_Album_Failed"), error.Message)); }
            finally
            {
                ThawAfterAlbumSwitch();
                LibrarySettingsPopup.IsOpen = true;
            }
        }

        private void AlbumNew_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new PromptDialog(Loc("Str_Album_NewTitle"), "", Loc("Str_Lib_Create")) { Owner = this };
            if (ShowAlbumDialog(dialog) != true || string.IsNullOrWhiteSpace(dialog.Text)) return;
            try
            {
                string path = CaptureLibrary.CreateAlbum(dialog.Text);
                RefreshAlbumManager(path);
                _ = OpenAlbumAsync(path);
            }
            catch (Exception error) { RefreshLibraryStatus(string.Format(Loc("Str_Album_Failed"), error.Message)); }
        }

        private void AlbumRename_Click(object sender, RoutedEventArgs e)
        {
            if ((AlbumList.SelectedItem as ListBoxItem)?.Tag is not CaptureLibrary.AlbumInfo album) return;
            var dialog = new PromptDialog(Loc("Str_Album_RenameTitle"), "", Loc("Str_Lib_Rename"), album.Name) { Owner = this };
            if (ShowAlbumDialog(dialog) != true || string.IsNullOrWhiteSpace(dialog.Text)) return;
            try { RefreshAlbumManager(CaptureLibrary.RenameAlbum(album.Path, dialog.Text)); }
            catch (Exception error) { RefreshLibraryStatus(string.Format(Loc("Str_Album_Failed"), error.Message)); }
        }

        private async void AlbumImport_Click(object sender, RoutedEventArgs e)
        {
            if (_albumBusy) return;
            var dialog = new FileDialog(FileDialogMode.Open) { Title = Loc("Str_Album_OpenFile"), Filter = Loc("Str_Album_Filter"), DefaultExt = ".kshot" };
            bool? chosen;
            try { chosen = dialog.ShowDialog(this); }
            finally { LibrarySettingsPopup.IsOpen = true; }
            if (chosen != true) return;
            string? password = null;
            try
            {
                while (true)
                {
                    string? attempt = password, file = dialog.FileName;
                    var (path, needsPassword) = await AlbumWorkAsync(() =>
                        (CaptureLibrary.ImportAlbum(file, attempt, out bool locked), locked));
                    RefreshLibraryStatus();
                    if (path != null) { RefreshAlbumManager(path); await OpenAlbumAsync(path, password); return; }
                    if (!needsPassword) return;
                    var unlock = new PromptDialog(string.Format(Loc("Str_Album_UnlockTitle"), Path.GetFileNameWithoutExtension(dialog.FileName)),
                        password != null ? Loc("Str_Lib_WrongPassword") : "", Loc("Str_Lib_Unlock"), kind: PromptKind.Password) { Owner = this };
                    if (ShowAlbumDialog(unlock) != true) return;
                    password = unlock.Password;
                }
            }
            catch (Exception error) { RefreshLibraryStatus(string.Format(Loc("Str_Album_Failed"), error.Message)); }
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
            LibraryKeepSaveBtn.IsEnabled = false;
            LibraryFolderToggle.Content = string.Format(Loc("Str_CS_ShowFolder"), FolderCaptures.Name);
            LibraryFolderToggle.ToolTip = FolderCaptures.Folder;
            LibraryFolderToggle.IsChecked = FolderCaptures.Enabled;
            RefreshLibraryStatus();
        }

        private void RefreshLibraryStatus(string? error = null)
        {
            bool open = CaptureLibrary.IsOpen, pw = CaptureLibrary.HasPassword;
            LibraryKeepCombo.IsEnabled = open;
            if (!open) LibraryKeepSaveBtn.IsEnabled = false;
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
            LibraryKeepSaveBtn.IsEnabled = keep != CaptureLibrary.Keep;
        }

        private void LibraryKeepSave_Click(object sender, RoutedEventArgs e)
        {
            if (LibraryKeepCombo.SelectedItem is not ComboBoxItem { Tag: string keep }) return;
            try
            {
                if (keep != CaptureLibrary.Keep) CaptureLibrary.Keep = keep;
                LibraryKeepSaveBtn.IsEnabled = false;
            }
            catch (Exception ex) { RefreshLibraryStatus(ex.Message); }
        }

        /// <summary>A password already set must be typed again before it changes or goes.</summary>
        private bool ConfirmCurrentLibraryPassword()
        {
            if (!CaptureLibrary.HasPassword) return true;
            var dlg = new PromptDialog(Loc("Str_CS_CurrentPasswordTitle"), "", Loc("Str_Lib_Unlock"), kind: PromptKind.Password) { Owner = this };
            if (ShowAlbumDialog(dlg) != true) return false;
            if (CaptureLibrary.CheckPassword(dlg.Password)) return true;
            RefreshLibraryStatus(Loc("Str_Lib_WrongPassword"));
            return false;
        }

        private void LibraryPassword_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmCurrentLibraryPassword()) return;
            var dlg = new PromptDialog(Loc("Str_CS_SetPasswordTitle"), Loc("Str_CS_SetPasswordDetail"), Loc("Str_CS_SetPasswordOk"), kind: PromptKind.NewPassword) { Owner = this };
            if (ShowAlbumDialog(dlg) != true) return;
            ApplyLibraryPassword(dlg.Password);
        }

        private void LibraryRemovePassword_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmCurrentLibraryPassword()) return;
            var dlg = new ConfirmDialog(Loc("Str_CS_RemovePasswordTitle"), Loc("Str_CS_RemovePasswordDetail"),
                Loc("Str_CS_RemovePassword"), Loc("Str_Btn_Cancel")) { Owner = this };
            ShowAlbumDialog(dlg);
            if (dlg.Confirmed) ApplyLibraryPassword(null);
        }

        private async void ApplyLibraryPassword(string? password)
        {
            if (_albumBusy) return;
            try
            {
                await AlbumWorkAsync(() => { CaptureLibrary.SetPassword(password); return true; });
                RefreshLibraryStatus();
            }
            catch (Exception ex)
            {
                RefreshLibraryStatus(string.Format(Loc("Str_CS_PasswordFailed"), ex.Message));
            }
        }

        /// <summary>Reads every capture's text again, for example after adding a recognition language.</summary>
        private void LibraryReindex_Click(object sender, RoutedEventArgs e)
        {
            // Without a recognition language nothing could read the text back, so the stored text stays.
            if (CaptureLibrary.OcrEngineFactory() == null) { RefreshLibraryStatus(Loc("Str_Ocr_Unavailable")); return; }
            CaptureLibrary.ReindexOcr();
            RefreshLibraryStatus(Loc("Str_Ocr_Reindexing"));
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
