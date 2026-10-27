using System.Windows;

namespace KillerShot.Shell
{
    /// <summary>The theme flyout's window half: the handler the XAML binds to, forwarded to ThemePicker.</summary>
    public partial class MainWindow
    {
        private Controls.ThemePicker _themePicker = null!;

        private void InitThemePicker()
        {
            _themePicker = new Controls.ThemePicker(this, ThemeMenu, ThemeButton);
        }

        private void ThemeButton_Click(object sender, RoutedEventArgs e) => _themePicker.Toggle();
    }
}
