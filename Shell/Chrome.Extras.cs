using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using KillerShot.Controls;

namespace KillerShot.Shell
{
    // KillerShot's half of the kit window chrome (Shell/Chrome.cs): close to tray with the window
    // fade, restore to maximized after a minimize, square corners on 98SE, the themed system menu,
    // and releasing the recorder, document and strips when the window closes.
    public partial class MainWindow
    {
        // The first close request is held while the whole window fades. The helper sets this
        // before issuing its own deferred Close(), so Alt+F4, the caption button, the system
        // menu and Application.Shutdown all share the same exit animation without recursion.
        private bool _closeFaded;
        private WindowState _stateBeforeMinimize = WindowState.Normal;
        private bool _restoringFromMinimize;

        /// <summary>Call from the constructor after InitializeComponent.</summary>
        private void InitChromeExtras()
        {
            SourceInitialized += (_, _) =>
            {
                if (WindowState != WindowState.Minimized) _stateBeforeMinimize = WindowState;
                // Added after the kit's WndProc, so it sees the system menu messages first.
                HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(SystemMenuHook);
                Services.ThemeManager.ThemeChanged += ApplyWindowFrameTheme;
                ApplyWindowFrameTheme();
            };
            Closed += (_, _) =>
            {
                Services.ThemeManager.ThemeChanged -= ApplyWindowFrameTheme;
                StopRecording(apply: null);
                DetachDocument();
                _strip?.Dispose();
                _navigationStrip?.Dispose();
            };
        }

        // Swallow the two messages that raise Windows' stock white system menu and show the
        // themed one instead. (SystemMenu.cs)
        private IntPtr SystemMenuHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (TryHandleSystemMenu(msg, wParam, lParam))
            {
                handled = true;
                return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        private void ApplyWindowFrameTheme()
        {
            DwmChrome.SetRoundedCorners(this, rounded: RoundedCornersWanted());
            DwmChrome.SetThemeBorder(this);
        }

        partial void AdjustRoundedCorners(ref bool rounded)
        {
            if (Services.ThemeManager.Current == Services.Theme.SE98) rounded = false;
        }

        partial void AfterWindowStateChanged()
        {
            if (WindowState == WindowState.Minimized)
            {
                _restoringFromMinimize = true;
                return;
            }
            if (_restoringFromMinimize && WindowState == WindowState.Normal &&
                _stateBeforeMinimize == WindowState.Maximized)
            {
                _restoringFromMinimize = false;
                WindowState = WindowState.Maximized;
                return;
            }
            _restoringFromMinimize = false;
            _stateBeforeMinimize = WindowState;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!App.CloseToTray && !App.Instance.IsExiting)
            {
                e.Cancel = true;
                Dispatcher.BeginInvoke(new Action(App.Instance.RequestExit));
                return;
            }
            if (Anim.FadeOutAndClose(this, ref _closeFaded))
            {
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }

        /// <summary>The bottom of this monitor's work area (above the taskbar) as a y position in
        /// <paramref name="element"/>'s coordinates, or null before the window has a handle.</summary>
        internal double? WorkAreaBottom(FrameworkElement element)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || PresentationSource.FromVisual(element) == null) return null;
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
            return element.PointFromScreen(new Point(info.rcWork.left, info.rcWork.bottom)).Y;
        }
    }
}
