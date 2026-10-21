# Changelog

## [1.0.0] - Unreleased

KillerShot combines screenshot and screen recording tools with an encrypted capture album, image editing and local QR reading.

### Data protection and recovery

- Albums recover after interrupted password changes or removal, preserving captures and recovery copies when cleanup fails. Failed setup keeps the album closed until the problem is corrected. (#1) (#4)
- Captures waiting in memory remain visible after a failed album write, save on retry and warn before exit. (#2) Waiting captures are capped at 1 GiB; past that, new captures still copy and save but skip the album with a warning, and nothing already waiting is lost.
- Unsaved edits require confirmation before another image opens or the app exits, with Save, Discard and Cancel choices. Save As cancels if its image changes while the dialog is open. Retention changes apply only after Save.
- Saving animated PNG, GIF, TIFF, BMP or mismatched image files offers a separate static PNG or JPEG export to preserve the original. Image edits retain transparency, and reopened files show their latest contents.
- Fixed concurrent album edits, search recovery, settings writes and output-file collisions. Failed retention preserves search results, missing search entries repair on reopen, and capture IDs remain unique after deletion.
- QR and image-text links are checked locally for hidden destinations, disguised downloads and invisible address characters. Checks do not visit destinations or submit links to a third party. Links never open automatically, and warnings explain each finding.
- Updated the encrypted album engine to SQLCipher 4.19.0 and SQLite bindings to 10.0.13 for upstream security and reliability fixes.

### Capture and recording

- Print Screen captures a region, Alt+Print Screen the active window, Ctrl+Print Screen the monitor under the pointer, and Shift+Print Screen every monitor. These global shortcuts are configurable, and capture preserves open menus.
- Window capture includes covered areas through Windows Graphics Capture and warns when only visible desktop pixels are available. (#5)
- A movable capture bar selects Photo or Video, capture mode and output folder. Regions capture on release; Escape or right-click cancels without output. Options include capture delay, mouse pointer and camera sound.
- After-capture choices include copying to the clipboard, saving to a folder, asking where to save and opening the editor. Set a filename pattern, PNG or JPEG format, JPEG quality and notifications. Clipboard captures include both PNG and a standard bitmap.
- Record a region, window or monitor to MP4 with system sound, microphone or both, or to animated GIF. Frame rate and quality are configurable; multichannel audio keeps center and surround content in stereo.
- Start recording with Ctrl+R or the global Ctrl+Shift+Print Screen shortcut. A countdown, capture frame and Pause, Resume, Stop and Discard controls stay out of the recording, with keyboard and tray access while the editor stays hidden.
- Stop and app exit wait for the recording to finish saving. Capture, encoder or audio device errors stop the recording right away and retain recoverable video; GIFs preserve long static intervals and play at their recorded speed at every frame rate.

### Albums and collections

- Captures are stored in an encrypted SQLCipher album with AES-256 encryption, opened through the Windows account or an optional password. Create, open, rename and switch albums from the title bar; each keeps its own retention and QR settings. External albums open as managed copies.
- Search window titles, program names, notes and recognized image text. Organize captures into collections, add notes and limit history by count or age. Existing Recent captures import on first start.
- Browse albums as shelves of carousels, one large carousel, a grid or a timeline, grouped by date or collection. Ctrl+L shows the album over an open image.
- Add local or network folders as saved collections, including subfolders, while keeping images in their original locations. Background scans refresh changed images, retry unavailable folders and locked files, and retain accessible images with an availability warning.
- The Windows Screenshots folder appears as a live collection with Open, Copy, Show in File Explorer and Add to the library actions. It can be hidden in album settings and stays under its own heading when grouped by collection. (#6)
- The collection sidebar, photo carousel and main view have independent placement, sizing and collapse controls. Over the album's large carousel and shelves views the editor's photo carousel steps aside, so pictures never appear in two carousels at once. Collection filters apply to the album and editor carousel; preview styles include compact photo piles.

### Editing and output

- Annotate with pen, highlighter, arrows, shapes, text and opaque redaction that replaces every pixel it touches, at any zoom and after moves or crops. Crop, select and move annotations, undo and redo, and use Fit or 100% zoom. Exports keep the capture's exact pixel size.
- Print from the editor, album or tray through a themed preview with printer, paper, orientation, scale, color and copies controls. The preview fits smaller and scaled work areas. Over the album, Ctrl+C and Ctrl+P use the selected capture, and the tray's Save Last keeps the capture it opened with.
- Opened and dropped images appear upright using their camera orientation, convert embedded color profiles to sRGB, and are refused with a clear message above 100 megapixels instead of exhausting memory.
- Drag images into the album or editor and full-size PNG copies into other apps. Abandoned temporary exports expire after 24 hours while active sessions and locked files remain intact.

### QR reading

- Read QR codes locally from images or a selected region, even with background scanning off. Optional background scanning covers new captures and the existing album and is off by default. Decoded text stays encrypted and searchable, with a QR marker on album cards.
- Code outlines and result cards offer Copy, Open with warning confirmation, and Copy password for Wi-Fi codes. Image-text web links use the same result cards and local checks.

### Command line and integration

- The `killershot` command captures regions, rectangles, windows or monitors; records timed MP4 or GIF video; lists, searches, copies, exports and opens album captures; and reports monitors, windows and settings. JSON output and exit codes support scripts and KillerMCP.
- `killershot qr` reads QR codes with JSON output and link warnings. `killershot --help` lists commands; `--cli` and `KillerShot.Cli.exe` remain supported, and portable copies run from their folder or full path.
- `KillerShot.exe --open` hands images to a running instance. Concurrent requests queue in order, confirm delivery and retain the correct album. Command-line captures refresh the open album, report failed saves and password limits, and cancel when selection closes. Completed recordings survive audio errors and report a warning.

### Desktop, accessibility and localization

- The compact toolbar groups capture, recording, editing and output actions, with configurable icons, labels, order and overflow. Alt+M replaces it with a title-bar menu. Context menus provide image, collection and workspace actions.
- Carousels coast and settle after dragging, stop on a new click and open the selected image after motion ends. Photo information and depth are adjustable; vertical Rolodex cards keep straight top and bottom edges.
- Screen readers receive control and file names. Keyboard users get visible focus, arrow-key navigation in option groups, scrollable menus, and focus that enters and returns from settings, About and shortcut cards.
- The capture bar supports Tab and Shift+Tab navigation. F2, arrow keys and Enter select regions for screenshots, QR reading and recording without a mouse. (#3)
- Windows fit the current monitor's work area on small or scaled displays, and the capture bar stays centered across monitors with different scaling. File picking and collection browsing remain responsive during background loading.
- Thirteen family themes, accent colors, Film Grain and Windows contrast themes cover the interface. File pickers use theme content colors for files and bookmarks. Text contrast, themed menus and photo cards, square 98SE controls and Windows 11 snap layouts follow the family style.
- Twenty languages, including Slovenian, cover the app and help, with live language changes updating the sidebars and footer. F1 opens the shortcut list and keyboard map; F12 opens About.
- Optional Start with Windows and Close to system tray settings control background use. The tray reports Print Screen conflicts and links to the relevant Windows setting; About offers hardware or software rendering.

### Help and website

- The website, Help, Technical and About pages explain the encrypted screenshot album and its protection limits. The website and MCP guide include Slovenian, and navigation labels follow the selected language.
- Website guides and dialogs support keyboard access and adjust to resizing and font changes. Album help points to the current settings location.
