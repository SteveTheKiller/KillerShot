using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace KillerShot.Controls
{
    /// <summary>What a prompt asks for.</summary>
    internal enum PromptKind { Text, Password, NewPassword }

    /// <summary>
    /// Themed prompt with one text box (Text), one password box (Password) or a password and its
    /// confirmation (NewPassword). DialogResult is true on OK. A new password must be typed the
    /// same twice before OK closes the dialog.
    /// </summary>
    public partial class PromptDialog : Window
    {
        private readonly PromptKind _kind;

        public string Text => InputText.Text;
        public string Password => PwBox.Password;

        internal PromptDialog(string heading, string detail, string confirmText, string initialText = "", PromptKind kind = PromptKind.Text)
        {
            InitializeComponent();
            _kind = kind;
            var titleFrames = BitmapDecoder.Create(new Uri("pack://application:,,,/Resources/kst-icon.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
            TitleIcon.Source = titleFrames[0];

            HeadingText.Text = heading;
            DetailText.Text = detail;
            DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            OkButton.Content = confirmText;

            UIElement first;
            if (kind == PromptKind.Text)
            {
                InputText.Visibility = Visibility.Visible;
                InputText.Text = initialText;
                AutomationProperties.SetName(InputText, heading);
                first = InputText;
            }
            else
            {
                PwBox.Visibility = Visibility.Visible;
                AutomationProperties.SetName(PwBox, heading);
                if (kind == PromptKind.NewPassword) ConfirmLabel.Visibility = PwConfirmBox.Visibility = Visibility.Visible;
                first = PwBox;
            }
            Loaded += (_, _) =>
            {
                Anim.FadeIn(RootBorder);
                first.Focus();
                if (first == InputText) InputText.SelectAll();
            };
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            if (_kind == PromptKind.NewPassword && PwBox.Password != PwConfirmBox.Password)
            {
                ErrorText.Text = (string)FindResource("Str_Lib_PasswordsDiffer");
                ErrorText.Visibility = Visibility.Visible;
                PwConfirmBox.Clear();
                PwConfirmBox.Focus();
                return;
            }
            if (_kind != PromptKind.Text && PwBox.Password.Length == 0)
            {
                PwBox.Focus();
                return;
            }
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            OK_Click(sender, e);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
    }
}
