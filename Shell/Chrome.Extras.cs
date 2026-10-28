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
            // Close to system tray means the window stays alive and only goes out of sight: the open
            // picture, a running recording, the album and its carousels all stay as they are, and
            // Open from the tray shows this same window at once instead of building a new one
            // (Steve, 2026-10-27). Exit and shutdown still close it for real.
            if (!App.Instance.IsExiting && ReferenceEquals(App.Instance.EditorWindow, this))
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            if (Anim.FadeOutAndClose(this, ref _closeFaded))
            {
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }

        private bool _hidingToTray;
        private int _hideGeneration;

        /// <summary>Fades the window out and hides it, keeping everything in it, and saves where it
        /// sat as a real close would. It fades back in when shown again.</summary>
        private void HideToTray()
        {
            if (_hidingToTray) return;
            _hidingToTray = true;
            CommitPendingText();
            bool savePlacement = true;
            AdjustSaveWindowPlacement(ref savePlacement);
            if (savePlacement) WindowPlacement.Save(this, Services.ThemeManager.SetSetting);
            int generation = ++_hideGeneration;
            Anim.FadeOut(RootGrid, () =>
            {
                // Asked to show again during the fade, or the app is going: leave the window be.
                if (generation != _hideGeneration || App.Instance.IsExiting || Dispatcher.HasShutdownStarted) return;
                Hide();
                _hidingToTray = false;
            });
        }

        /// <summary>Show was asked for while the window was still fading out to the tray: the hide is
        /// called off and the window fades back in (audit BF-006, 2026-10-27).</summary>
        internal void CancelHideToTray()
        {
            if (!_hidingToTray) return;
            _hidingToTray = false;
            _hideGeneration++;
            FadeInContent();
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
