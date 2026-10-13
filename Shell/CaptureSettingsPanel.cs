using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerShot.Capture;
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
            (ShortcutAction.LastMode, "Str_CS_LastMode"),
            (ShortcutAction.Window, "Str_Mode_Window"),
            (ShortcutAction.Monitor, "Str_Mode_Monitor"),
            (ShortcutAction.AllMonitors, "Str_Mode_AllMonitors"),
        ];

        private Button? _recording;
        private bool _syncingOptions;

        private void CaptureSettingsButton_Click(object sender, RoutedEventArgs e) => ShowCaptureSettings();

        private void ShowCaptureSettings()
        {
            if (ShortcutsOverlay.Visibility == Visibility.Visible) ShortcutsOverlay.Visibility = Visibility.Collapsed;
            if (AboutOverlay.Visibility == Visibility.Visible) AboutOverlay.Visibility = Visibility.Collapsed;
            _syncingOptions = true;
            OpenEditorToggle.IsChecked = CaptureSettings.OpenEditorAfterCapture;
            NotifyToggle.IsChecked = CaptureSettings.Notify;
            StartupToggle.IsChecked = StartupRegistration.Enabled;
            _syncingOptions = false;
            BuildShortcutRows();
            FadeOverlayIn(CaptureSettingsOverlay);
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
            if (_syncingOptions) return;
            if (sender == OpenEditorToggle) CaptureSettings.OpenEditorAfterCapture = OpenEditorToggle.IsChecked == true;
            else if (sender == NotifyToggle) CaptureSettings.Notify = NotifyToggle.IsChecked == true;
            else if (sender == StartupToggle && !StartupRegistration.Set(StartupToggle.IsChecked == true))
            {
                _syncingOptions = true;
                StartupToggle.IsChecked = StartupRegistration.Enabled;
                _syncingOptions = false;
            }
        }

        private void BuildShortcutRows()
        {
            ShortcutRows.Children.Clear();
            ShortcutRows.RowDefinitions.Clear();
            var tray = App.Instance.Tray;
            int row = 0;
            foreach (var (action, labelKey) in ShortcutActions)
            {
                ShortcutRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Text = Loc(labelKey), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 12, 3) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                Grid.SetRow(label, row);
                ShortcutRows.Children.Add(label);

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
                ShortcutRows.Children.Add(button);
                row++;
            }
            RefreshConflictText();
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
