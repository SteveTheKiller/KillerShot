using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace KillerShot.Controls
{
    /// <summary>Crossfades a palette using a non-layout adorner over the outgoing surface.</summary>
    internal static class ThemeTransition
    {
        private const int DurationMs = 180;

        internal static void CrossFade(FrameworkElement surface, Action applyTheme)
        {
            var layer = AdornerLayer.GetAdornerLayer(surface);
            if (layer == null || surface.ActualWidth <= 0 || surface.ActualHeight <= 0)
            {
                applyTheme();
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(surface);
            int width = Math.Max(1, (int)Math.Round(surface.ActualWidth * dpi.DpiScaleX));
            int height = Math.Max(1, (int)Math.Round(surface.ActualHeight * dpi.DpiScaleY));
            var snapshot = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX,
                dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            snapshot.Render(surface);
            snapshot.Freeze();

            var adorner = new SnapshotAdorner(surface, snapshot);
            layer.Add(adorner);
            applyTheme();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(DurationMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            fade.Completed += (_, __) => layer.Remove(adorner);
            adorner.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private sealed class SnapshotAdorner : Adorner
        {
            private readonly ImageSource _snapshot;
            internal SnapshotAdorner(UIElement adorned, ImageSource snapshot) : base(adorned)
            {
                _snapshot = snapshot;
                IsHitTestVisible = false;
                SnapsToDevicePixels = true;
            }
            protected override void OnRender(DrawingContext dc)
                => dc.DrawImage(_snapshot, new Rect(AdornedElement.RenderSize));
        }
    }
}
