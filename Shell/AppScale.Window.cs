using System;
using System.Windows;

namespace KillerShot.Shell
{
    // KillerShot's half of the kit app scale (Shell/AppScale.cs). The kit still scales ScaleHost;
    // this only grows the window's minimum size with the scale, so a larger UI never clips the
    // capture bar or the album below the fixed title bar and footer.
    public partial class MainWindow
    {
        private const double BaseContentMinHeight = 400;
        private const double FixedChromeHeight = 60; // 36 title + 24 footer
        private const double BaseMinWidth = 640;

        partial void ApplyAppScaleTo(double scale, ref bool handled)
        {
            MinWidth = Math.Min(BaseMinWidth * Math.Max(1.0, scale), SystemParameters.WorkArea.Width);
            MinHeight = Math.Min(FixedChromeHeight + BaseContentMinHeight * scale,
                                 SystemParameters.WorkArea.Height);

            if (WindowState != WindowState.Normal) return;
            double currentWidth = ActualWidth > 0 ? ActualWidth : Width;
            double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
            Width = Math.Max(currentWidth, MinWidth);
            Height = Math.Max(currentHeight, MinHeight);
            // Keep the grown window inside its own monitor's work area. Before the HWND exists
            // (the constructor restoring the saved scale) the SourceInitialized fit does this.
            if (PresentationSource.FromVisual(this) != null)
                KillerFamily.Windowing.WorkAreaFit.Fit(this, keepMargin: false);
        }
    }
}
