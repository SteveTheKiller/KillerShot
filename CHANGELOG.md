# KillerShot Changelog

## [1.0.0] - Unreleased

KillerShot combines screenshot and screen recording tools with an encrypted capture album, image editing and local QR reading.

### Data protection and recovery

- Unreadable album edits open the flattened image safely and preserve recovery data. QR and text recognition follow edits and reject obsolete results; album actions stay bound to their original album.
- Redactions keep hidden pixels covered when resizing inserted pictures, including transparent and fractional layers.
- Albums recover after interrupted password changes or removal, preserving captures and recovery copies when cleanup fails. Failed setup keeps the album closed until the problem is corrected. (#1) (#4)
- Captures waiting in memory remain visible after a failed album write, save on retry and warn before exit. (#2) Waiting captures are capped at 1 GiB; past that, new captures still copy and save but skip the album with a warning, and nothing already waiting is lost.
- Failed album edits stay open for retry or recovery through Save As, and delayed edits cannot cross into another album. Exit and installation restart recheck work changed while saving, including text still being typed.
- Installation and removal serialize changes, preserve newer installations, and restore the previous image, shortcuts and registration when setup fails.
- Unsaved edits require confirmation before another image opens or the app exits, with Save, Discard and Cancel choices. Save As and image-edit dialogs reject a replaced image, and editor output controls are disabled while the album is visible. Retention changes apply only after Save.
- Saving animated PNG, GIF, TIFF, BMP or mismatched image files offers a separate static PNG or JPEG export to preserve the original. Image edits retain transparency, and reopened files show their latest contents.
- Fixed concurrent album edits, search recovery, settings writes and output-file collisions. Failed retention preserves search results, missing search entries repair on reopen, and capture IDs remain unique after deletion. OCR failures stay with their own album.
- QR and image-text links are checked locally for hidden destinations, disguised downloads and invisible address characters. Checks do not visit destinations or submit links to a third party. Links never open automatically, and warnings explain each finding.
- Updated the encrypted album engine to SQLCipher 4.19.0 and SQLite bindings to 10.0.13 for upstream security and reliability fixes.

### Capture and recording

- MP4 recordings hold the final frame through Stop, including sparse captures and stopping while paused.
- Print Screen captures a region, Alt+Print Screen the active window, Ctrl+Print Screen the monitor under the pointer, and Shift+Print Screen every monitor. These global shortcuts are configurable, and capture preserves open menus.
- Window capture includes covered areas through Windows Graphics Capture and warns when only visible desktop pixels are available. (#5)
- A movable capture bar selects Photo or Video, capture mode and output folder. Regions capture on release; Escape or right-click cancels without output. Options include capture delay, mouse pointer and camera sound.
- Scrolling capture stitches a long page or a wide one while you scroll it yourself: pick the region, scroll with the wheel, bar or keys, and press Enter. Sticky headers and footers are kept once, nothing is sent to the scrolled program, and a jump too far to follow keeps what matched. Output stays opaque regardless of unused screen-pixel bytes. Failed captures release their windows, shortcuts and capture resources.
- Repeat last region (Ctrl+Alt+Print Screen) captures the same area again without selecting, and asks for a new selection when the monitors changed. Saved regions keep a named place or a fixed size around the pointer, and interval capture takes the last region every few seconds to the album and folder until stopped. The command line adds --repeat, --preset and --interval with --count.
- After-capture choices include copying to the clipboard, saving to a folder, asking where to save and opening the editor, plus copying the saved file's path, opening the capture in up to three chosen programs and starting an email with it attached. Review before copying or saving (off by default) holds each capture in the editor until Copy and save (Ctrl+Enter), so redactions are in place before anything leaves KillerShot. Set a filename pattern, PNG or JPEG format, JPEG quality and notifications. Clipboard captures include both PNG and a standard bitmap.
- Record a region, window or monitor to MP4 with system sound, microphone or both, or to animated GIF. Frame rate and quality are configurable; multichannel audio keeps center and surround content in stereo.
- Start recording with Ctrl+R or the global Ctrl+Shift+Print Screen shortcut. A countdown, capture frame and Pause, Resume, Stop and Discard controls stay out of the recording, with keyboard and tray access while the editor stays hidden. Stop during countdown cancels startup.
- Stop and app exit wait for the recording to finish saving. Capture, encoder or audio device errors stop the recording right away and retain recoverable video; GIFs preserve long static intervals and play at their recorded speed at every frame rate.
- Missing recording frames reuse buffers, recover from brief interruptions and stop with an error when capture does not recover.

### Albums and collections

- Captures are stored in an encrypted SQLCipher album with AES-256 encryption, opened through the Windows account or an optional password. Create, open, rename and switch albums from the title bar; each keeps its own retention and QR settings. External albums open as managed copies.
- Copy text (Ctrl+Shift+C) reads the text in a picture on this computer, copies it with its line breaks and shows it in a card to select from; Read a region reads a selected area. Crop and text regions support Enter to start or apply, arrows to move, Ctrl+arrows to resize, Shift for larger steps and Escape to cancel, with spoken selection details. Choose the recognition language there, and read the album's text again from album settings after adding a language.
- Search window titles, program names, notes and recognized image text. Organize captures into collections, add notes and limit history by count or age. Existing Recent captures import on first start.
- Browse albums as shelves of carousels, one large carousel, a grid or a timeline, grouped by date or collection. Ctrl+L shows the album over an open image.
- Add local or network folders as saved collections, including subfolders, while keeping images in their original locations. Background scans refresh changed images, retry unavailable folders and locked files, and retain accessible images with an availability warning.
- The Windows Screenshots folder appears as a live collection with Open, Copy, Show in File Explorer and Add to the library actions. It can be hidden in album settings and stays under its own heading when grouped by collection. (#6)
- The collection sidebar docks left or right and the photo carousel above or below the picture, each with its own sizing and collapse controls. Over the album's large carousel and shelves views the editor's photo carousel steps aside, so pictures never appear in two carousels at once. Collection filters apply to the album and editor carousel; preview styles include compact photo piles.

### Editing and output

- Marks on album captures stay editable: the encrypted album keeps them beside the untouched original, reopening brings them back, and copying, printing, saving or dragging from the album always gives the flattened result. Select several marks with Ctrl+click or Ctrl+A to move, duplicate (Ctrl+D), reorder (Ctrl+] and Ctrl+[) or delete them together; resize from the selection's grip and edit placed text or callouts with F2 or a double-click.
- Annotate with pen, highlighter, arrows, lines, outlined or filled shapes, text, numbered steps, speech-bubble callouts and opaque redaction that replaces every pixel it touches, at any zoom and after moves or crops. Without a mouse, Enter places the current tool's mark, Tab moves between marks, arrows move and Ctrl+arrows resize them, and screen readers can name, select, move and resize each mark. Crop, rotate, flip, resize, add a margin or trim a plain border from the Image menu, with marks following the picture. Select and move annotations, undo and redo, and use Fit or 100% zoom. Exports keep the capture's exact pixel size.
- Print from the editor, album or tray through a themed preview with printer, paper, orientation, scale, color and copies controls. The preview fits smaller and scaled work areas. Over the album, Ctrl+C and Ctrl+P use the capture in focus, and the tray's Save Last keeps the capture it opened with.
- Ctrl+V pastes a copied picture onto the open capture as an object you can move, resize and keep in the album; Ctrl+Shift+V opens it as a new picture, and Insert picture from file places one from disk. Pasting keeps PNG transparency and never watches the clipboard.
- Opened and dropped images appear upright using their camera orientation, convert embedded color profiles to sRGB, and are refused with a clear message above 100 megapixels instead of exhausting memory.
- Album images and inserted pictures use the same size checks before decoding, with a smaller limit for thumbnails.
- Drag images into the album or editor and full-size PNG copies into other apps. Abandoned temporary exports expire after 24 hours while active sessions and locked files remain intact.

### QR reading

- Read QR codes locally from images or a selected region, even with background scanning off. Optional background scanning covers new captures and the existing album and is off by default. Decoded text stays encrypted and searchable, with a QR marker on album cards.
- Code outlines and result cards offer Copy, Open with warning confirmation, and Copy password for Wi-Fi codes. Image-text web links use the same result cards and local checks.

### Command line and integration

- The `killershot` command captures regions, rectangles, windows or monitors; records timed MP4 or GIF video; lists, searches, copies, exports and opens album captures; and reports monitors, windows and settings. JSON output and exit codes support scripts and KillerMCP.
- Repeated command-line captures accept spaced, inline and mixed-case interval options without recursive crashes.
- `killershot qr` reads QR codes with JSON output and link warnings. `killershot --help` lists commands; `--cli` and `KillerShot.Cli.exe` remain supported, and portable copies run from their folder or full path.
- `KillerShot.exe --open` hands images to a running instance. Concurrent requests queue in order, confirm delivery and retain the correct album. Command-line captures refresh the open album, report failed saves and password limits, and cancel when selection closes. Completed recordings survive audio errors and report a warning.

### Desktop, accessibility and localization

- The print preview caption switches between the wordmark and the plain title when the theme changes while it is open, and its wordmark takes the main title bar's spacing so its brush strokes have room. The file picker's recent locations and places lists are named for screen readers.
- File dialogs cancel stale loads, bound large listings and identify incomplete results without blocking the interface.
- Black table headers match the content surface, and softer grain keeps dark surfaces from looking gray.
- About groups settings in two columns and can hide shortcut hints in menus and tooltips without disabling keyboard shortcuts.
- Every album view keeps the themed content pane, with subtly shaded screenshot cards and restrained photo shadows in carousels.
- Portable stays clear of footer controls and long image readouts in narrow windows.
- In 98SE, menu hover fill follows the selected accent.
- Light is shades of gray with nothing white: the background, sidebar and footer are the darkest gray (#cccccc), menus and bars a step lighter, then panes, then fields.
- Theme changes cross-fade the whole window, app size runs from 70% to 250% with an "App size" readout, and confirmation prompts use the family dialog frame with the 98SE bevel and a translated Cancel.

- The compact toolbar groups capture, recording, editing and output actions, with configurable icons, labels, order and overflow. Alt+M replaces it with a title-bar menu. Context menus provide image, collection and workspace actions.
- Carousels coast and settle after dragging, stop on a new click and open the selected image after motion ends. Photo information and depth are adjustable.
- Screen readers receive control and file names, and can invoke collection headers to manage them. Keyboard users get visible focus, arrow-key navigation in option groups, scrollable menus, and focus that enters and returns from settings, About and shortcut cards.
- The capture bar supports Tab and Shift+Tab navigation. F2, arrow keys and Enter select regions, windows and monitors for screenshots and recording without a mouse, and regions for QR reading. (#3)
- Windows fit the current monitor's work area on small or scaled displays, and the capture bar stays centered across monitors with different scaling. File picking and collection browsing remain responsive during background loading.
- Thirteen family themes, accent colors, Film Grain and Windows contrast themes cover the interface. File pickers use content colors and app icons; 98SE dialogs have full bevels and compact fields that keep long paths clear of the file dialog's recent-locations arrow and search text clear of its magnifier. Buttons and menus use the theme's interface face, radio and check marks center on their labels, and dialog and card titles take the accent color. On gradient themes, the edges of the About, settings, Keyboard Shortcuts and capture bar cards and the shortcut list's top rule follow the gradient. Text contrast, themed menus and photo cards, square 98SE controls with a white, sunken main pane and Windows 11 snap layouts follow the family style.
- Twenty languages, including Slovenian, cover the app and help, with live language changes updating the sidebars and footer. F1 opens the shortcut list and keyboard map; F12 opens About.
- Optional Start with Windows and Close to system tray settings control background use. The tray reports Print Screen conflicts and links to the relevant Windows setting; About offers hardware or software rendering.
- One signed KillerShot.exe runs portable or installs itself. The PORTABLE badge in the footer installs it for you or for everyone, with an optional desktop shortcut, after the same checks as Exit for unsaved edits, recordings and waiting captures. `KillerShot.exe /silent` installs machine-wide with admin rights or for the current user without, Add/Remove Programs and `/uninstall-silent` remove it, and only validly signed builds install. Albums, captures and settings stay after uninstall. A failed uninstall step leaves an uninstall entry that still works, and an all-users uninstall waits for its confirmation and clears its helper and empty program folder at the next restart.
- About checks for a newer release, on startup unless that is turned off, and installs it in one click. The download must match the release checksum, carry a valid signature from the same certificate as the running copy and be a newer KillerShot version before it replaces the running copy, which then reopens.

### Help and website

- The website, Help, Technical and About pages explain the encrypted screenshot album and its protection limits. The website and MCP guide include Slovenian, and navigation labels follow the selected language.
- Website guides and dialogs support keyboard access and adjust to resizing and font changes. Album help points to the current settings location. The website's theme menu can turn Film Grain off (Alt+Shift+G).

### Changed

- Dialogs, the color picker and print preview are edged in the app border color and follow theme changes.
- Wordmarks and headings are raised with a subtle bevel in their own color instead of a dark outline; wordmarks keep their drop shadow. The Dark theme's red accent is the brand red again instead of a pink tint.
- Theme menu hover rows reach the right edge with the same inset as the left.
- Hovered menu and list items show their text in the theme accent, lightened or darkened only as far as needed to stay readable on the hover fill.
- KillerShot now shares the family's theme engine, control styles and palettes with KillerShell, KillerScan and the KillerUI template, so its colors, grain and geometry match the rest of the family. Visible differences: outline buttons are bare until hovered, title bar buttons hover in the accent, the close button is gray until hovered, combo boxes use the family field color, the window has the family 1px edge, wordmarks carry the family emboss, and 98SE uses the family's caption buttons, tabs and scroll bars. Hold Alt and drag anywhere in a window to move it.
- Outline buttons show their accent outline at rest again, as dialog buttons did before. Shared family files for the window caption menu, language menu and control styles now match the KillerUI template with no other visible change.
- The theme and language menus open in the content pane's lower corner beside the sidebar, as in the rest of the family.
- The file picker is now the family picker shared with the other Killer apps. Accept still stays off while a folder loads or an accept is running, and a mistyped folder still returns to the last folder that worked. Visible differences: the picker gains the family image preview, pictures in the icon view show their shell icon instead of a thumbnail, and the folder picker no longer moves focus to Select when its first folder loads.
