using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerShot.Capture;
using CaptureMode = KillerShot.Capture.CaptureMode;
using KillerShot.Services;
using Shortcut = KillerShot.Services.Shortcut;

// The capture settings card: one recorder per global shortcut, and the capture options.
// A recorder listens after a click: the next key combination becomes the shortcut, Backspace or
// Delete turns it off, Escape leaves it unchanged. The global shortcuts are released while a
// recorder listens so Print Screen reaches it instead of starting a capture.
namespace KillerShot.Shell
{
    public partial class MainWindow
    {
        private static readonly (ShortcutAction Action, string LabelKey)[] ShortcutActions =
        [
            (ShortcutAction.LastMode, "Str_Mode_Region"),
            (ShortcutAction.Window, "Str_Mode_Window"),
            (ShortcutAction.Monitor, "Str_Mode_Monitor"),
            (ShortcutAction.AllMonitors, "Str_Mode_AllMonitors"),
        ];

        private Button? _recording;
        private bool _syncingOptions;

        private void CaptureSettingsButton_Click(object sender, RoutedEventArgs e) => ShowCaptureSettings();

        private static readonly int[] DelayChoices = [0, 1, 2, 3, 5, 10];
        private static readonly int[] QualityChoices = [100, 95, 90, 85, 80, 70, 60];

        private void ShowCaptureSettings()
        {
            if (ShortcutsOverlay.Visibility == Visibility.Visible) ShortcutsOverlay.Visibility = Visibility.Collapsed;
            if (AboutOverlay.Visibility == Visibility.Visible) AboutOverlay.Visibility = Visibility.Collapsed;
            if (VideoSettingsOverlay.Visibility == Visibility.Visible) VideoSettingsOverlay.Visibility = Visibility.Collapsed;
            SyncCaptureOptions();
            BuildShortcutRows();
            CaptureSettingsScroll.MaxHeight = Math.Max(200, RootGrid.ActualHeight - 110);
            FadeOverlayIn(CaptureSettingsOverlay);
        }

        /// <summary>Puts the stored choices into every control without firing their handlers.</summary>
        private void SyncCaptureOptions()
        {
            _syncingOptions = true;
            CopyToggle.IsChecked = CaptureSettings.CopyToClipboard;
            SaveToFolderToggle.IsChecked = CaptureSettings.SaveToFolder;
            AskToSaveToggle.IsChecked = CaptureSettings.AskToSave;
            OpenEditorToggle.IsChecked = CaptureSettings.OpenEditorAfterCapture;
            NotifyToggle.IsChecked = CaptureSettings.Notify;
            CursorToggle.IsChecked = CaptureSettings.IncludeCursor;
            SoundToggle.IsChecked = CaptureSettings.PlaySound;
            PickWindowToggle.IsChecked = CaptureSettings.PickWindow;
            PrintShrinkToggle.IsChecked = CaptureSettings.PrintShrink;
            PrintEnlargeToggle.IsChecked = CaptureSettings.PrintEnlarge;
            PrintRotateToggle.IsChecked = CaptureSettings.PrintRotate;

            OutputFolderBox.Text = CaptureSettings.OutputFolder;
            FileNamePatternBox.Text = CaptureSettings.FileNamePattern;
            FileNamePlaceholdersText.Text = string.Format(Loc("Str_CS_Placeholders"), OutputFiles.Placeholders);

            FormatCombo.Items.Clear();
            FormatCombo.Items.Add(new ComboBoxItem { Content = "PNG", Tag = "png" });
            FormatCombo.Items.Add(new ComboBoxItem { Content = "JPEG", Tag = "jpg" });
            FormatCombo.SelectedIndex = CaptureSettings.Format == "jpg" ? 1 : 0;

            QualityCombo.Items.Clear();
            foreach (var q in QualityChoices) QualityCombo.Items.Add(new ComboBoxItem { Content = q + "%", Tag = q });
            int qi = Array.IndexOf(QualityChoices, CaptureSettings.JpegQuality);
            QualityCombo.SelectedIndex = qi >= 0 ? qi : 2;
            QualityCombo.IsEnabled = CaptureSettings.Format == "jpg";

            DelayCombo.Items.Clear();
            foreach (var d in DelayChoices)
                DelayCombo.Items.Add(new ComboBoxItem { Content = d == 0 ? Loc("Str_CS_DelayNone") : string.Format(Loc("Str_CS_Seconds"), d), Tag = d });
            int di = Array.IndexOf(DelayChoices, CaptureSettings.DelaySeconds);
            DelayCombo.SelectedIndex = di >= 0 ? di : 0;

            _syncingOptions = false;
            RefreshFileNameExample();
        }

        private void RefreshFileNameExample()
        {
            var name = OutputFiles.FileName(CaptureSettings.FileNamePattern, DateTime.Now, CaptureMode.Region, 1920, 1080, CaptureSettings.Format);
            FileNameExampleText.Text = string.Format(Loc("Str_CS_Example"), name);
        }

        private void FileNamePatternBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingOptions) return;
            var text = FileNamePatternBox.Text.Trim();
            CaptureSettings.FileNamePattern = text.Length > 0 ? text : CaptureSettings.DefaultFileNamePattern;
            RefreshFileNameExample();
        }

        private void OutputFolderBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var text = OutputFolderBox.Text.Trim().Trim('"');
            if (text.Length == 0) { DefaultFolder_Click(sender, e); return; }
            try { CaptureSettings.OutputFolder = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(text)); }
            catch { }
            OutputFolderBox.Text = CaptureSettings.OutputFolder;
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Folder)
            {
                Title = Loc("Str_Dlg_FolderTitle"),
                InitialDirectory = System.IO.Directory.Exists(CaptureSettings.OutputFolder) ? CaptureSettings.OutputFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dlg.ShowDialog(this) != true) return;
            CaptureSettings.OutputFolder = dlg.FileName;
            OutputFolderBox.Text = dlg.FileName;
        }

        private void DefaultFolder_Click(object sender, RoutedEventArgs e)
        {
            Settings.Set("OutputFolder", "");
            OutputFolderBox.Text = CaptureSettings.OutputFolder;
        }

        private void CaptureCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingOptions || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: var tag } }) return;
            if (sender == FormatCombo && tag is string f)
            {
                CaptureSettings.Format = f;
                QualityCombo.IsEnabled = f == "jpg";
                RefreshFileNameExample();
            }
            else if (sender == QualityCombo && tag is int q) CaptureSettings.JpegQuality = q;
            else if (sender == DelayCombo && tag is int d) CaptureSettings.DelaySeconds = d;
        }

        private void CloseCaptureSettings()
        {
            StopRecording(apply: null);
            FadeOverlayOut(CaptureSettingsOverlay);
        }

        private void CaptureSettingsOverlay_Click(object sender, MouseButtonEventArgs e) => CloseCaptureSettings();
        private void CaptureSettingsClose_Click(object sender, RoutedEventArgs e) => CloseCaptureSettings();

        private void PrintScreenSetting_Click(object sender, RoutedEventArgs e) => TrayHost.OpenPrintScreenSetting();

        private void CaptureOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingOptions || sender is not CheckBox box) return;
            bool on = box.IsChecked == true;
            if (box == CopyToggle) CaptureSettings.CopyToClipboard = on;
            else if (box == SaveToFolderToggle) CaptureSettings.SaveToFolder = on;
            else if (box == AskToSaveToggle) CaptureSettings.AskToSave = on;
            else if (box == OpenEditorToggle) CaptureSettings.OpenEditorAfterCapture = on;
            else if (box == NotifyToggle) CaptureSettings.Notify = on;
            else if (box == CursorToggle) CaptureSettings.IncludeCursor = on;
            else if (box == SoundToggle) CaptureSettings.PlaySound = on;
            else if (box == PickWindowToggle) CaptureSettings.PickWindow = on;
            else if (box == PrintShrinkToggle) CaptureSettings.PrintShrink = on;
            else if (box == PrintEnlargeToggle) CaptureSettings.PrintEnlarge = on;
            else if (box == PrintRotateToggle) CaptureSettings.PrintRotate = on;
        }

        private void BuildShortcutRows()
        {
            BuildRecorderRows(ShortcutRows, ShortcutActions);
            BuildRecorderRows(VideoShortcutRows, [(ShortcutAction.Video, "Str_VS_ShortcutLabel")]);
            RefreshConflictText();
        }

        /// <summary>One label and recorder button per action, in a two-column grid.</summary>
        private void BuildRecorderRows(Grid grid, (ShortcutAction Action, string LabelKey)[] actions)
        {
            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            var tray = App.Instance.Tray;
            int row = 0;
            foreach (var (action, labelKey) in actions)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Text = Loc(labelKey), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 12, 3) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                Grid.SetRow(label, row);
                grid.Children.Add(label);

                var button = new Button
                {
                    Tag = action,
                    Style = (Style)FindResource("OutlineButton"),
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(0, 3, 0, 3),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                SetRecorderText(button, action, tray?.IsFailed(action) == true);
                button.Click += (_, _) => StartRecording(button);
                Grid.SetRow(button, row);
                Grid.SetColumn(button, 1);
                grid.Children.Add(button);
                row++;
            }
        }

        private void SetRecorderText(Button b, ShortcutAction action, bool failed)
        {
            var text = CaptureSettings.ShortcutText(action);
            string shown = string.IsNullOrWhiteSpace(text) ? Loc("Str_CS_Off")
                : Shortcut.TryParse(text, out var sc) ? sc.Short : text;
            var content = new TextBlock { Text = shown, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
            if (failed)
            {
                content.Text = shown + "  " + Loc("Str_CS_InUse");
                content.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            }
            b.Content = content;
            b.ToolTip = Loc("Str_CS_RecordTip");
        }

        private void RefreshConflictText()
        {
            var tray = App.Instance.Tray;
            bool any = tray != null && tray.FailedShortcuts.Count > 0;
            ShortcutConflictText.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (any) ShortcutConflictRun.Text = string.Format(Loc("Str_Tray_ShortcutTaken"), string.Join(", ", tray!.FailedShortcuts));
        }

        private void StartRecording(Button b)
        {
            StopRecording(apply: null);
            _recording = b;
            App.Instance.Tray?.Suspend();
            b.Content = new TextBlock { Text = Loc("Str_CS_Listening"), FontSize = 12 };
            b.SetResourceReference(ForegroundProperty, "PrimaryBrush");
            b.Focus();
        }

        /// <summary>Ends a recording. <paramref name="apply"/> is the new shortcut text, an empty
        /// string to turn the shortcut off, or null to leave it unchanged.</summary>
        private void StopRecording(string? apply)
        {
            var b = _recording;
            if (b == null) return;
            _recording = null;
            var action = (ShortcutAction)b.Tag;
            if (apply != null) CaptureSettings.SetShortcutText(action, apply);
            b.ClearValue(ForegroundProperty);
            App.Instance.Tray?.Resume();
            BuildShortcutRows();
            BuildCaptureMenu();
            RefreshDocumentUi();
            ApplyToolbarStyle();
        }

        private void RecorderKey(KeyEventArgs e)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { StopRecording(apply: null); return; }
            if (key is Key.Back or Key.Delete) { StopRecording(apply: ""); return; }
            // Wait for the main key; a bare modifier is not a shortcut.
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.None) return;

            string? name = key switch
            {
                Key.Snapshot => "Print Screen",
                >= Key.A and <= Key.Z => key.ToString(),
                >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
                >= Key.F1 and <= Key.F24 => key.ToString(),
                _ => null,
            };
            if (name == null) return;

            var mods = Keyboard.Modifiers;
            var parts = new List<string>();
            if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((mods & ModifierKeys.Windows) != 0) parts.Add("Win");
            // Only Print Screen and the F keys may stand alone; a bare letter would steal typing.
            if (parts.Count == 0 && key != Key.Snapshot && !(key >= Key.F1 && key <= Key.F24)) return;
            parts.Add(name);
            StopRecording(apply: string.Join("+", parts));
        }
    }
}
