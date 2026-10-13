# Changelog

## [0.1.0] - Unreleased
- Icon-only buttons give screen readers the name from their tooltip, and controls reached with the keyboard show a dotted focus outline.

- Added a tray app that captures with Print Screen. Plain Print Screen repeats the last mode used, Alt+Print Screen captures the active window, Ctrl+Print Screen captures the monitor under the pointer, and Shift+Print Screen captures every monitor.
- Added region capture over a frozen copy of the desktop on every monitor, so menus and tooltips stay in the picture. Escape or a right-click cancels with nothing copied.
- Added window capture through Windows Graphics Capture, including covered windows, with rounded corners filled from the desktop behind them.
- Captures go straight to the clipboard as PNG and as a standard bitmap, and the last capture can be saved as a PNG from the tray menu.
- The tray icon reports when another program or a Windows setting already owns Print Screen and links to that setting.
- Added the editor with pen, highlighter, arrow, rectangle, ellipse, text, opaque redaction and crop, plus select and move, undo and redo, Fit and 100% zoom, and exports at the capture's exact pixel size.
- Added Open, Save, Save as and Copy, and a Recent list that keeps the last 20 captures.
- Added capture settings with recorders for the four global shortcuts, and options to open the editor after each capture, show a notification, and start with Windows.
- Added the family window, rail, toolbar styles, all 13 themes with Black and Teal as the default, 19 languages, the F1 shortcuts card with a keyboard map, and the F12 About card with a hardware or software rendering choice.
- Added after-capture choices to copy to the clipboard, save straight to an output folder (Windows' Screenshots folder by default), ask where to save, or open the editor, with a file name pattern, PNG or JPEG with a quality setting, and a capture delay.
- Added options to include the mouse pointer, play a camera sound, and pick the window to capture with a click. Hovering in region mode highlights a window and a single click takes it.
- Added printing from the editor (Ctrl+P) and the tray, with shrink, enlarge and rotate options.
- Added screen recording to MP4 (H.264, through the encoder built into Windows) with optional system sound, microphone or both, or animated GIF, with frame rate and quality settings for each, a video settings card on the rail, a Record button (Ctrl+R) and a global shortcut (Ctrl+Shift+Print Screen) that starts and stops it.
- Recording picks a region, window or monitor, counts down, and shows a frame and a control bar with Pause, Stop and Discard that never appear in the video. GIFs store only what changed between frames, with a palette per frame.
