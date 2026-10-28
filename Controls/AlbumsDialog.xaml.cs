using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Controls;

/// <summary>
/// The album manager, the KillerShot counterpart of the KillerNotes and Killendar database
/// dialogs, in the family dialog frame (AlbumsDialog.xaml, CustomizeToolbarDialog's card). The
/// album and library controls themselves stay in MainWindow, where their handlers live; the window
/// lends them to this card while it is open and takes them back when it closes.
/// </summary>
public partial class AlbumsDialog : Window
{
    public AlbumsDialog(Window owner, FrameworkElement body)
    {
        InitializeComponent();
        Owner = owner;
        TaskbarIdentity.Track(this);
        var titleFrames = BitmapDecoder.Create(new Uri("pack://application:,,,/Resources/kst-icon.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
        TitleIcon.Source = titleFrames[0];
        Title = "KillerShot - " + LocaleManager.Loc("Str_Album_Title");
        SubtitleText.Text = LocaleManager.Loc("Str_Album_Title");
        BodyHost.Content = body;
        Loaded += (_, _) => Anim.FadeIn(RootBorder);
        Loaded += (_, _) => KillerFamily.Windowing.WorkAreaFit.Fit(this, keepMargin: true);
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !e.Handled) { e.Handled = true; Close(); } };
    }

    /// <summary>Takes the body out again so MainWindow can put it back where it lives.</summary>
    public void Release() => BodyHost.Content = null;

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
