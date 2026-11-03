using System.IO;
using System.Windows;
using System.Windows.Input;
using KillerShot.Services;

namespace KillerShot.Controls
{
    /// <summary>
    /// The one-time question after the first recording: keep recordings as plain files in the
    /// recording folder, or move them into the encrypted library. The folder is picked to start
    /// with, so Continue alone keeps the simple behavior. Closing with the X answers nothing, and
    /// the next recording asks again.
    /// </summary>
    public partial class RecordingStorageDialog : Window
    {
        /// <summary>RecordingStorage.Folder or RecordingStorage.Library, or null when closed unanswered.</summary>
        public string? Choice { get; private set; }

        /// <summary>Whether the answer was Open the clip rather than Continue.</summary>
        public bool OpenClip { get; private set; }

        public RecordingStorageDialog(string recording)
        {
            InitializeComponent();
            TaskbarIdentity.Track(this);
            if (!DialogChrome.SetTitleIcon(TitleIcon))
                TitleIcon.Visibility = Visibility.Collapsed;
            FolderDetail.Text = string.Format(LocaleManager.Loc("Str_Rec_FolderDetail"), ShortFolder(Path.GetDirectoryName(recording) ?? ""));
            Poster.Source = VideoCaptures.Poster(recording, 256);
            var (_, _, duration) = VideoCaptures.Probe(recording);
            if (duration > TimeSpan.Zero)
            {
                DurationText.Text = VideoCaptures.FormatDuration(duration);
                DurationBadge.Visibility = Visibility.Visible;
            }
            Loaded += (_, _) => { Anim.FadeIn(RootBorder); ContinueButton.Focus(); };
        }

        /// <summary>The folder as people know it: Videos\Screen Recordings rather than the full path
        /// when it is inside the user's own profile.</summary>
        internal static string ShortFolder(string folder)
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\') + "\\";
            return folder.StartsWith(profile, StringComparison.OrdinalIgnoreCase) ? folder[profile.Length..] : folder;
        }

        private string Picked => LibraryChoice.IsChecked == true ? RecordingStorage.Library : RecordingStorage.Folder;

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            Choice = Picked;
            Close();
        }

        private void OpenClip_Click(object sender, RoutedEventArgs e)
        {
            Choice = Picked;
            OpenClip = true;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
            base.OnKeyDown(e);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DialogChrome.Drag(this, e);
    }
}
