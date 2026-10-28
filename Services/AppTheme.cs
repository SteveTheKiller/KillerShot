using System.IO;
using System.Windows;
using System.Windows.Media;

namespace KillerShot.Services
{
    /// <summary>
    /// KillerShot's side of the family theme engine. ThemeManager.cs is the KillerUI template and
    /// is never edited here; everything particular to KillerShot comes in through its hooks: the
    /// saved settings, the Dark/Teal first run, the values in Themes/App, and the tokens
    /// KillerShot's own views derive.
    /// </summary>
    internal static class AppTheme
    {
        /// <summary>Call once before ThemeManager.Initialize.</summary>
        internal static void Configure()
        {
            ThemeManager.GetSetting = Settings.Get;
            ThemeManager.SetSetting = Settings.Set;
            ThemeManager.SetDefaults(Theme.Dark, dark: Accent.Teal, light: Accent.Green, black: Accent.Teal, se98: Accent.Blue);
            ThemeManager.AppTokens = AppTokens;
            ThemeManager.AppDerivedTokens = AppDerivedTokens;

            // Dialog captions (Controls/DialogChrome.cs) and their title icon.
            Controls.DialogChrome.AppName = "KillerShot";
            Controls.DialogChrome.WordmarkSplit = 6;
            Controls.DialogChrome.IconUri = new Uri("pack://application:,,,/Resources/kst-icon.ico");

            // KillerShot has no switch for a translator's external strings file, so the kit's
            // live-reload hook stays off. Stated here so the compiler knows it is deliberate.
            LocaleManager.ExternalFile = null;
        }

        private static string FileName(Theme theme) => theme == Theme.SE98 ? "98SE" : theme.ToString();

        // Before the family contract: KillerShot's per-theme values, then its accent values, then
        // its defaults for whatever a theme left unsaid.
        private static void AppTokens(ResourceDictionary d, Theme theme, Accent accent)
        {
            Merge(d, $"Themes/App/{FileName(theme)}.xaml", overwrite: true);
            if (ThemeManager.SupportsAccents(theme))
                Merge(d, $"Themes/App/Accents/{FileName(theme)}/{accent}.xaml", overwrite: true);
            Merge(d, "Themes/App/Defaults.xaml", overwrite: false);

            // The title bar file name sits on the "Killer" baseline of KillerShot's title bar, one
            // pixel up, as KillerPDF's does; 98SE's plain caption keeps it level.
            if (!d.Contains("FileNameTransform"))
            {
                var shift = new TranslateTransform(0, theme == Theme.SE98 ? 0 : -1);
                shift.Freeze();
                d["FileNameTransform"] = shift;
            }
        }

        // After the family contract: tokens built from family ones.
        private static void AppDerivedTokens(ResourceDictionary d, Theme theme, Accent accent)
        {
            // The password and album unlock fields sit on the field surface.
            ThemeManager.Mirror(d, "InputFieldBrush", "SurfaceBrush");

            // 98SE's raised frame around About, Shortcuts and the dialogs takes the window frame's
            // outer pair and the family bevel's inner pair; the other themes draw none.
            bool square = theme == Theme.SE98;
            var clear = Frozen(Colors.Transparent);
            ThemeManager.SetIfAbsent(d, "OverlayBevelOuterLightBrush", square ? d["FrameOuterLightBrush"] : clear);
            ThemeManager.SetIfAbsent(d, "OverlayBevelOuterDarkBrush", square ? d["FrameOuterDarkBrush"] : clear);
            ThemeManager.SetIfAbsent(d, "OverlayBevelInnerLightBrush", square ? d["BevelLightBrush"] : clear);
            ThemeManager.SetIfAbsent(d, "OverlayBevelInnerDarkBrush", square ? d["BevelDarkBrush"] : clear);

            // The album manager's rows, as in the KillerNotes and Killendar database dialogs
            // (Services/DatabaseRowPalette.cs, copied from KillerNotes).
            DatabaseRowPalette.Complete(d);
        }

        private static void Merge(ResourceDictionary d, string path, bool overwrite)
        {
            ResourceDictionary source;
            try { source = new ResourceDictionary { Source = new Uri($"pack://application:,,,/{path}") }; }
            catch (IOException) { return; }   // no file for this theme or accent
            foreach (object key in source.Keys)
                if (overwrite || !d.Contains(key)) d[key] = source[key];
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
