# Changelog

## [0.1.0] - Unreleased

- Added a tray app that captures with Print Screen. Plain Print Screen repeats the last mode used, Alt+Print Screen captures the active window, Ctrl+Print Screen captures the monitor under the pointer, and Shift+Print Screen captures every monitor.
- Added region capture over a frozen copy of the desktop on every monitor, so menus and tooltips stay in the picture. Escape or a right-click cancels with nothing copied.
- Added window capture through Windows Graphics Capture, including covered windows, with rounded corners filled from the desktop behind them.
- Captures go straight to the clipboard as PNG and as a standard bitmap, and the last capture can be saved as a PNG from the tray menu.
- The tray icon reports when another program or a Windows setting already owns Print Screen and links to that setting.
