using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KillerShot.Services
{
    /// <summary>
    /// The color of the cards captures sit on (library tiles, the strip's list and rolodex) and an
    /// optional color for each collection, which marks its cards and its heading. The shared card
    /// color lives in settings.json; collection colors stay in their album so equal collection
    /// IDs in other albums do not share colors. Each is #RRGGBB hex, or empty for the default.
    ///
    /// The card colors reach XAML as application resources, CaptureCardBrush and the text brushes
    /// that read on it, so a change repaints every tile at once.
    /// </summary>
    internal static class CardColors
    {
        /// <summary>The fallback when no card color is picked. The library and the strip draw their
        /// cards in the theme's pane gray instead (StripCard), since both sit on the window's own
        /// background.</summary>
        public static readonly Color DefaultCard = Color.FromRgb(0x14, 0x14, 0x14);

        /// <summary>The card color or a collection's color changed.</summary>
        public static event Action? Changed;

        public static Color Card => Parse(Settings.Get("CardColor")) ?? DefaultCard;

        public static bool CardIsDefault => Parse(Settings.Get("CardColor")) == null;

        /// <summary>The card color in the strip and the library, which sit on the window's own
        /// background: the picked card color, or the theme's pane gray by default.</summary>
        public static Color StripCard()
        {
            if (!CardIsDefault) return Card;
            return Application.Current?.TryFindResource("ContentPaneBrush") is SolidColorBrush pane ? pane.Color : Color.FromRgb(0x3A, 0x3A, 0x3A);
        }

        public static void SetCard(Color? color)
        {
            Settings.Set("CardColor", color is Color c ? Hex(c) : "");
            Apply();
            Changed?.Invoke();
        }

        /// <summary>A collection's own color, or null when it has none.</summary>
        public static Color? Collection(long? id) => id is long i && i > 0 ? Parse(CaptureLibrary.GetAlbumSetting("CollectionColor." + i.ToString(CultureInfo.InvariantCulture))) : null;

        public static void SetCollection(long id, Color? color)
        {
            CaptureLibrary.SetAlbumSetting("CollectionColor." + id.ToString(CultureInfo.InvariantCulture), color is Color c ? Hex(c) : "");
            Changed?.Invoke();
        }

        /// <summary>Puts the card brushes in the application resources. Called at startup and on
        /// every change.</summary>
        public static void Apply()
        {
            if (Application.Current is not { } app) return;
            Color card = Card;
            bool light = IsLight(card);
            app.Resources["CaptureCardBrush"] = Frozen(card);
            app.Resources["CaptureCardTextBrush"] = Frozen(light ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Color.FromRgb(0xEA, 0xEA, 0xEA));
            app.Resources["CaptureCardMutedBrush"] = Frozen(light ? Color.FromRgb(0x5A, 0x5A, 0x5A) : Color.FromRgb(0x9A, 0x9A, 0x9A));
        }

        public static bool IsLight(Color c) => c.R * 0.299 + c.G * 0.587 + c.B * 0.114 > 150;

        private static SolidColorBrush Frozen(Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }

        private static string Hex(Color c) => "#" + c.R.ToString("X2", CultureInfo.InvariantCulture) + c.G.ToString("X2", CultureInfo.InvariantCulture) + c.B.ToString("X2", CultureInfo.InvariantCulture);

        private static Color? Parse(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return null; }
        }
    }
}
