using System.ComponentModel;
using System.Windows.Controls;

namespace KillerShot.Shell
{
    /// <summary>The footer's traffic light beside the status line, as in the rest of the family:
    /// green when things are fine, amber while a mode waits on the user, red when something failed.
    /// The status line is set from many places as finished text, so the tone is read back from it
    /// against the templates of the messages that can appear there.</summary>
    public partial class MainWindow
    {
        private static readonly string[] StatusErrorKeys =
        [
            "Str_Status_OpenFailed", "Str_Status_SaveFailed", "Str_Status_PrintFailed", "Str_Status_NoClipboardImage",
            "Str_Img_Invalid", "Str_Img_DocumentChanged", "Str_Save_DocumentChanged", "Str_Marks_Recovery",
            "Str_Status_InstallFailed",
        ];

        private static readonly string[] StatusBusyKeys = ["Str_Status_CropHint", "Str_Ocr_RegionHint"];

        private void WatchStatusTone()
        {
            // The descriptor holds its handlers strongly, so the hook comes off when the window
            // closes or the window would never be collected.
            var text = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            EventHandler changed = (_, _) => ApplyStatusTone();
            text.AddValueChanged(StatusText, changed);
            Closed += (_, _) => text.RemoveValueChanged(StatusText, changed);
            ApplyStatusTone();
        }

        private void ApplyStatusTone()
            => StatusDot.SetResourceReference(Border.BackgroundProperty, StatusBrushKey(StatusText.Text));

        private static string StatusBrushKey(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "OkBrush";
            if (StatusErrorKeys.Any(key => Matches(text, key))) return "DangerRed";
            if (StatusBusyKeys.Any(key => Matches(text, key))) return "WarnBrush";
            return "OkBrush";

            // A formatted message matches its template up to the first placeholder.
            static bool Matches(string text, string key)
            {
                string template = Services.LocaleManager.Loc(key);
                int hole = template.IndexOf('{');
                string fixedPart = hole >= 0 ? template[..hole] : template;
                return fixedPart.Length > 0 && text.StartsWith(fixedPart, StringComparison.CurrentCulture);
            }
        }
    }
}
