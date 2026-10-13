using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShot.Capture;
using KillerShot.Services;
using KillerShot.Video;
using CaptureMode = KillerShot.Capture.CaptureMode;

// The video settings card (rail camera button) and the toolbar Record button.
namespace KillerShot.Shell
{
    public partial class MainWindow
    {
        private bool _syncingVideo;

        private void VideoSettingsButton_Click(object sender, RoutedEventArgs e) => ShowVideoSettings();
        private void VideoSettingsOverlay_Click(object sender, MouseButtonEventArgs e) => CloseVideoSettings();
        private void VideoSettingsClose_Click(object sender, RoutedEventArgs e) => CloseVideoSettings();

        private void RecordBtn_Click(object sender, RoutedEventArgs e)
        {
            Editor.CommitText();
            App.Instance.Video?.Toggle();
        }

        private void ShowVideoSettings()
        {
            foreach (var o in new UIElement[] { ShortcutsOverlay, AboutOverlay, CaptureSettingsOverlay })
                if (o.Visibility == Visibility.Visible) o.Visibility = Visibility.Collapsed;
            SyncVideoOptions();
            BuildShortcutRows();
            VideoSettingsScroll.MaxHeight = Math.Max(200, RootGrid.ActualHeight - 110);
            FadeOverlayIn(VideoSettingsOverlay);
        }

        private void CloseVideoSettings()
        {
            StopRecording(apply: null);
            FadeOverlayOut(VideoSettingsOverlay);
        }

        private static ComboBoxItem Item(string text, object tag) => new() { Content = text, Tag = tag };

        private static void Fill(ComboBox box, IEnumerable<ComboBoxItem> items, object selected)
        {
            box.Items.Clear();
            int i = 0, pick = 0;
            foreach (var it in items)
            {
                box.Items.Add(it);
                if (Equals(it.Tag, selected)) pick = i;
                i++;
            }
            box.SelectedIndex = pick;
        }

        private void SyncVideoOptions()
        {
            _syncingVideo = true;
            Fill(VideoFormatCombo, [Item(Loc("Str_VS_FormatMp4"), "mp4"), Item(Loc("Str_VS_FormatGif"), "gif")], VideoSettings.Format);
            Fill(Mp4FpsCombo, VideoSettings.Mp4FpsChoices.Select(f => Item(string.Format(Loc("Str_VS_FpsValue"), f), f)), VideoSettings.Mp4Fps);
            Fill(Mp4QualityCombo, [Item(Loc("Str_VS_QualityLow"), 0), Item(Loc("Str_VS_QualityMedium"), 1), Item(Loc("Str_VS_QualityHigh"), 2)], VideoSettings.Mp4Quality);
            Fill(SoundCombo, [Item(Loc("Str_VS_SoundOff"), SoundSource.None), Item(Loc("Str_VS_SoundSystem"), SoundSource.System),
                              Item(Loc("Str_VS_SoundMic"), SoundSource.Microphone), Item(Loc("Str_VS_SoundBoth"), SoundSource.Both)], VideoSettings.Sound);
            Fill(AudioKbpsCombo, VideoSettings.AudioKbpsChoices.Select(k => Item(string.Format(Loc("Str_VS_KbpsValue"), k), k)), VideoSettings.AudioKbps);
            Fill(GifFpsCombo, VideoSettings.GifFpsChoices.Select(f => Item(string.Format(Loc("Str_VS_FpsValue"), f), f)), VideoSettings.GifFps);
            Fill(GifColorsCombo, VideoSettings.GifColorChoices.Select(c => Item(c.ToString(), c)), VideoSettings.GifColors);
            Fill(GifSizeCombo, VideoSettings.GifSizeChoices.Select(c => Item(c + "%", c)), VideoSettings.GifSize);
            GifLoopToggle.IsChecked = VideoSettings.GifLoop;
            VideoCursorToggle.IsChecked = VideoSettings.IncludeCursor;
            VideoCountdownToggle.IsChecked = VideoSettings.Countdown;
            VideoCopyToggle.IsChecked = VideoSettings.CopyFile;
            VideoNotifyToggle.IsChecked = VideoSettings.Notify;
            VideoFolderBox.Text = VideoSettings.OutputFolder;
            VideoFileNameBox.Text = VideoSettings.FileNamePattern;
            _syncingVideo = false;
            RefreshVideoFormatUi();
        }

        /// <summary>Only the chosen format's settings are live; the other set is grayed.</summary>
        private void RefreshVideoFormatUi()
        {
            bool gif = VideoSettings.Format == "gif";
            Mp4FpsCombo.IsEnabled = Mp4QualityCombo.IsEnabled = SoundCombo.IsEnabled = !gif;
            AudioKbpsCombo.IsEnabled = !gif && VideoSettings.Sound != SoundSource.None;
            GifFpsCombo.IsEnabled = GifColorsCombo.IsEnabled = GifSizeCombo.IsEnabled = GifLoopToggle.IsEnabled = gif;
            var name = OutputFiles.FileName(VideoSettings.FileNamePattern, DateTime.Now, CaptureMode.Region, 1920, 1080, gif ? "gif" : "mp4");
            VideoFileNameExampleText.Text = string.Format(Loc("Str_CS_Example"), name);
        }

        private void VideoCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingVideo || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: var tag } }) return;
            if (sender == VideoFormatCombo && tag is string f) VideoSettings.Format = f;
            else if (sender == Mp4FpsCombo && tag is int a) VideoSettings.Mp4Fps = a;
            else if (sender == Mp4QualityCombo && tag is int q) VideoSettings.Mp4Quality = q;
            else if (sender == SoundCombo && tag is SoundSource snd) VideoSettings.Sound = snd;
            else if (sender == AudioKbpsCombo && tag is int k) VideoSettings.AudioKbps = k;
            else if (sender == GifFpsCombo && tag is int g) VideoSettings.GifFps = g;
            else if (sender == GifColorsCombo && tag is int c) VideoSettings.GifColors = c;
            else if (sender == GifSizeCombo && tag is int z) VideoSettings.GifSize = z;
            RefreshVideoFormatUi();
        }

        private void VideoOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingVideo || sender is not CheckBox box) return;
            bool on = box.IsChecked == true;
            if (box == GifLoopToggle) VideoSettings.GifLoop = on;
            else if (box == VideoCursorToggle) VideoSettings.IncludeCursor = on;
            else if (box == VideoCountdownToggle) VideoSettings.Countdown = on;
            else if (box == VideoCopyToggle) VideoSettings.CopyFile = on;
            else if (box == VideoNotifyToggle) VideoSettings.Notify = on;
        }

        private void VideoFileNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingVideo) return;
            var text = VideoFileNameBox.Text.Trim();
            VideoSettings.FileNamePattern = text.Length > 0 ? text : CaptureSettings.DefaultFileNamePattern;
            RefreshVideoFormatUi();
        }

        private void VideoFolderBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var text = VideoFolderBox.Text.Trim().Trim('"');
            if (text.Length == 0) { VideoDefaultFolder_Click(sender, e); return; }
            try { VideoSettings.OutputFolder = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(text)); }
            catch { }
            VideoFolderBox.Text = VideoSettings.OutputFolder;
        }

        private void VideoBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Folder)
            {
                Title = Loc("Str_Dlg_VideoFolderTitle"),
                InitialDirectory = System.IO.Directory.Exists(VideoSettings.OutputFolder) ? VideoSettings.OutputFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            };
            if (dlg.ShowDialog(this) != true) return;
            VideoSettings.OutputFolder = dlg.FileName;
            VideoFolderBox.Text = dlg.FileName;
        }

        private void VideoDefaultFolder_Click(object sender, RoutedEventArgs e)
        {
            Settings.Set("VideoFolder", "");
            VideoFolderBox.Text = VideoSettings.OutputFolder;
        }
    }
}
