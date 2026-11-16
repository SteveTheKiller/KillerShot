using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerShot.Services;

namespace KillerShot.Controls
{
    /// <summary>
    /// The Redact settings: which kinds of sensitive text auto redact looks for, the color and
    /// feather of every redaction box, and whether new captures are scanned when they open. Every
    /// change is saved as it is made. Find runs the scan through the editor's callback, which
    /// returns how many boxes it added, or null when Windows cannot read text here. The dialog
    /// closes once boxes are added and stays open to explain when none were.
    /// </summary>
    public partial class RedactDialog : Window
    {
        private readonly Func<Task<int?>> _scan;
        private readonly (CheckBox Box, RedactKinds Kind)[] _kinds;
        private bool _loading = true;

        /// <summary>How many boxes the last Find added.</summary>
        public int Added { get; private set; }

        internal RedactDialog(Window? owner, Func<Task<int?>> scan)
        {
            InitializeComponent();
            Owner = owner;
            _scan = scan;
            if (!DialogChrome.SetTitleIcon(TitleIcon))
                TitleIcon.Visibility = Visibility.Collapsed;
            _kinds =
            [
                (EmailCheck, RedactKinds.Email), (CardCheck, RedactKinds.Card), (PhoneCheck, RedactKinds.Phone),
                (SsnCheck, RedactKinds.Ssn), (IpCheck, RedactKinds.Ip), (SecretCheck, RedactKinds.Secret),
            ];
            var kinds = RedactSettings.Kinds;
            foreach (var (box, kind) in _kinds)
            {
                box.IsChecked = kinds.HasFlag(kind);
                box.Checked += (_, _) => SaveKinds();
                box.Unchecked += (_, _) => SaveKinds();
            }
            ColorSwatch.Background = new SolidColorBrush(RedactSettings.Fill);
            FeatherSlider.Value = RedactSettings.Feather;
            ShowFeather();
            FeatherSlider.ValueChanged += (_, _) =>
            {
                ShowFeather();
                if (!_loading) RedactSettings.Feather = (int)FeatherSlider.Value;
            };
            EveryCaptureCheck.IsChecked = RedactSettings.ScanEveryCapture;
            EveryCaptureCheck.Checked += (_, _) => RedactSettings.ScanEveryCapture = true;
            EveryCaptureCheck.Unchecked += (_, _) => RedactSettings.ScanEveryCapture = false;
            _loading = false;
            SyncFind();
            Loaded += (_, _) => { Anim.FadeIn(RootBorder); FindButton.Focus(); };
        }

        private RedactKinds Picked => _kinds.Where(k => k.Box.IsChecked == true).Aggregate(RedactKinds.None, (all, k) => all | k.Kind);

        private void SaveKinds()
        {
            if (_loading) return;
            RedactSettings.Kinds = Picked;
            SyncFind();
        }

        private void SyncFind() => FindButton.IsEnabled = Picked != RedactKinds.None;

        private void ShowFeather() =>
            FeatherValue.Text = FeatherSlider.Value < 1 ? LocaleManager.Loc("Str_Redact_FeatherOff") : $"{FeatherSlider.Value:0} px";

        private void Color_Click(object sender, RoutedEventArgs e)
        {
            var picker = new ColorPickerDialog(this, RedactSettings.Fill);
            picker.ShowDialog();
            if (!picker.Accepted) return;
            RedactSettings.Fill = picker.SelectedColor;
            ColorSwatch.Background = new SolidColorBrush(RedactSettings.Fill);
        }

        private async void Find_Click(object sender, RoutedEventArgs e)
        {
            FindButton.IsEnabled = false;
            ShowStatus(LocaleManager.Loc("Str_Ocr_Reading"));
            int? added;
            try { added = await _scan(); }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                ShowStatus(error.Message);
                SyncFind();
                return;
            }
            if (!IsLoaded) return;
            if (added is > 0)
            {
                Added = added.Value;
                Close();
                return;
            }
            ShowStatus(LocaleManager.Loc(added == null ? "Str_Ocr_Unavailable" : "Str_Redact_None"));
            SyncFind();
        }

        private void ShowStatus(string text)
        {
            StatusText.Text = text;
            StatusText.Visibility = Visibility.Visible;
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
