using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            (ShortcutAction.RepeatRegion, "Str_Region_Repeat"),
            (ShortcutAction.Interval, "Str_Region_IntervalToggle"),
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
            CaptureSettingsScroll.MaxHeight = Math.Max(200, RootGrid.ActualHeight - 110) + CaptureSettingsScroll.Padding.Top + CaptureSettingsScroll.Padding.Bottom;
            CaptureSettingsOffset.X = CaptureSettingsOffset.Y = 0;   // each opening starts centered
            FadeOverlayIn(CaptureSettingsOverlay);
        }

        // The card moves by its top band. The offset is clamped so the band stays inside the window.
        private Point _settingsDragStart;
        private Vector _settingsDragOrigin;

        private void CaptureSettingsDrag_Down(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindAncestor<ButtonBase>(source) != null) return;
            _settingsDragStart = e.GetPosition(CaptureSettingsOverlay);
            _settingsDragOrigin = new Vector(CaptureSettingsOffset.X, CaptureSettingsOffset.Y);
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        private void CaptureSettingsDrag_Move(object sender, MouseEventArgs e)
        {
            if (!((UIElement)sender).IsMouseCaptured) return;
            var moved = _settingsDragOrigin + (e.GetPosition(CaptureSettingsOverlay) - _settingsDragStart);
            var card = CaptureSettingsCard.TransformToAncestor(CaptureSettingsOverlay)
                .TransformBounds(new Rect(CaptureSettingsCard.RenderSize));
            double homeLeft = card.Left - CaptureSettingsOffset.X, homeTop = card.Top - CaptureSettingsOffset.Y;
            // Keep 60px of the card on screen sideways and its top band reachable.
            moved.X = Math.Clamp(moved.X, 60 - homeLeft - card.Width, CaptureSettingsOverlay.ActualWidth - 60 - homeLeft);
            moved.Y = Math.Clamp(moved.Y, -homeTop, CaptureSettingsOverlay.ActualHeight - 36 - homeTop);
            CaptureSettingsOffset.X = moved.X;
            CaptureSettingsOffset.Y = moved.Y;
        }

        private void CaptureSettingsDrag_Up(object sender, MouseButtonEventArgs e)
        {
            if (((UIElement)sender).IsMouseCaptured) ((UIElement)sender).ReleaseMouseCapture();
            e.Handled = true;
        }

        private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
        {
            while (node != null && node is not T) node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            return node as T;
        }

        /// <summary>Puts the stored choices into every control without firing their handlers.</summary>
        private void SyncCaptureOptions()
        {
            _syncingOptions = true;
            CopyToggle.IsChecked = CaptureSettings.CopyToClipboard;
            SaveToFolderToggle.IsChecked = CaptureSettings.SaveToFolder;
            AskToSaveToggle.IsChecked = CaptureSettings.AskToSave;
            OpenEditorToggle.IsChecked = CaptureSettings.OpenEditorAfterCapture;
            ReviewFirstToggle.IsChecked = CaptureSettings.ReviewFirst;
            CopyPathToggle.IsChecked = CaptureSettings.CopyFilePath;
            OpenWithToggle.IsChecked = CaptureSettings.OpenWithApps;
            EmailToggle.IsChecked = CaptureSettings.EmailCapture;
            SyncOpenWithLabel();
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

        /// <summary>Shows the chosen programs on the Open with option.</summary>
        private void SyncOpenWithLabel()
        {
            var names = Handoff.Apps.Select(Handoff.AppName).ToList();
            OpenWithToggle.Content = names.Count == 0 ? Loc("Str_CS_OpenWith")
                : Loc("Str_CS_OpenWith") + " (" + string.Join(", ", names) + ")";
        }

        /// <summary>The programs Open with starts: each can be removed, and up to three added.</summary>
        private void OpenWithApps_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = OpenWithAppsBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (string app in Handoff.Apps)
            {
                string path = app;
                var item = new MenuItem { Header = string.Format(Loc("Str_CS_RemoveApp"), Handoff.AppName(path)), ToolTip = path };
                item.Click += (_, _) => { Handoff.Apps = Handoff.Apps.Where(a => a != path).ToList(); SyncOpenWithLabel(); };
                menu.Items.Add(item);
            }
            var add = new MenuItem { Header = Loc("Str_CS_AddApp"), IsEnabled = Handoff.Apps.Count < Handoff.MaxApps };
            add.Click += (_, _) =>
            {
                var dialog = new KillerShot.Controls.FileDialog(KillerShot.Controls.FileDialogMode.Open)
                {
                    Title = Loc("Str_CS_AddApp"), Filter = Loc("Str_CS_AppFilter"),
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                };
                if (dialog.ShowDialog(this) != true || !Handoff.IsProgram(dialog.FileName)) return;
                Handoff.Apps = [.. Handoff.Apps, dialog.FileName];
                CaptureSettings.OpenWithApps = true;
                SyncCaptureOptions();
            };
            menu.Items.Add(add);
            // Opening in programs with none chosen would do nothing, so the option turns itself off.
            menu.Closed += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (Handoff.Apps.Count > 0 || !CaptureSettings.OpenWithApps) return;
                CaptureSettings.OpenWithApps = false;
                SyncCaptureOptions();
            });
            menu.IsOpen = true;
        }

        private void CaptureOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingOptions || sender is not CheckBox box) return;
            bool on = box.IsChecked == true;
            if (box == CopyToggle) CaptureSettings.CopyToClipboard = on;
            else if (box == SaveToFolderToggle) CaptureSettings.SaveToFolder = on;
            else if (box == AskToSaveToggle) CaptureSettings.AskToSave = on;
            else if (box == OpenEditorToggle) CaptureSettings.OpenEditorAfterCapture = on;
            else if (box == ReviewFirstToggle) CaptureSettings.ReviewFirst = on;
            else if (box == CopyPathToggle) CaptureSettings.CopyFilePath = on;
            else if (box == OpenWithToggle) { CaptureSettings.OpenWithApps = on; if (on && Handoff.Apps.Count == 0) OpenWithApps_Click(OpenWithAppsBtn, e); }
            else if (box == EmailToggle) CaptureSettings.EmailCapture = on;
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
            var tray = _shortcutHost;
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
            var tray = _shortcutHost;
            bool any = tray != null && tray.FailedShortcuts.Count > 0;
            ShortcutConflictText.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (any) ShortcutConflictRun.Text = string.Format(Loc("Str_Tray_ShortcutTaken"), string.Join(", ", tray!.FailedShortcuts));
        }

        private void StartRecording(Button b)
        {
            StopRecording(apply: null);
            _recording = b;
            _shortcutHost?.Suspend();
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
            _shortcutHost?.Resume();
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
