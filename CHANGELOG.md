# KillerShot Changelog

## [1.2.1]

KillerShot 1.2.1 carries Trash Polka into the Files view and fixes how the album's views scroll.

### Fixed
- Shelves, Grid and Timeline show the edge shadows while more captures lie above or below. The captures also scroll to the pane's edges instead of stopping short of the bottom.
- A window capture of KillerShot keeps its own dialog when one sits over the window.
- Make PDF shows only for folders that have pictures in them, in the album's folder cards and in the Files view.
- Opening KillerShot on the Files view no longer draws the editor's frame around it.

### Changed
- Under Trash Polka, the Files pane shows the lighter stretch of the ink behind its rows, the way the editor's card does. The column header stays matte.

## [1.2.0] - 2026-11-21

KillerShot 1.2.0 adds the Files view with folder tabs, recordings in the library, auto redact, Make PDF and the Trash Polka theme, fixes updates that could not install, and keeps large libraries responsive.

### Security
- The Print Screen helper is native code and no longer starts the .NET runtime. Another program running as you can no longer use .NET environment variables to load code into its elevated window access.
- Update and uninstall helpers load PowerShell modules only from Windows' own folder, and the elevated update step compiles its code in a folder only administrators can open. Code planted by another program running as you can no longer run inside them as administrator.
- Updates check certificate revocation and the exact release publisher, stream downloads with a size cap and a deadline, and read only the release's own fields, so text quoted in release notes cannot confuse them.
- Setting an album password retires the account key before the album file is swapped, so an album that fails to reopen shows as password protected, never unprotected. The old key is removed once the album opens with its password.

### Fixed
- Updates install again. The updater could not read the release checksum list and refused every release, so KillerShot 1.0.0 to 1.1.1 cannot update themselves and need 1.2.0 downloaded by hand once.
- An all-users installation applies automatic updates again and reopens KillerShot for the signed-in user at normal rights. An update started from an administrator's copy also reopens at normal rights.
- A failed update reopens KillerShot instead of leaving it closed, even when antivirus or a locked-down PowerShell stops the helper. A copy that fails its final check is swapped back for the previous version, and that restore retries busy files.
- A finished update is no longer reported as failed, and one that stalls or cannot close the app says so. The Update button stays off once an update starts, the window no longer freezes during the handoff, and updating no longer removes a relaunch another Killer app scheduled.
- The Microsoft Store copy leaves its updates to the Store. It would otherwise have replaced itself with the GitHub exe, which needs the .NET Desktop Runtime.
- A per-user uninstall finishes when the Print Screen helper is in Program Files and asks once to remove it. A silent uninstall leaves the helper in place and lists the command that removes it.
- Uninstall handles an apostrophe in the install path, retries while a copy is still running, reports the real reason when it fails, and clears leftovers from earlier failed attempts. Install, update and uninstall share one lock, and each one's cleanup follows its install scope.
- An unexpected error no longer closes KillerShot silently. It is written to crash.log in the KillerShot folder under local app data, the tray shows a warning, and the app keeps running when it can.
- With a history limit set, the one-time import of the old Recent folder no longer deletes screenshots older than the limit.
- Ctrl+S on a PNG missing its end chunk saves a copy instead of crashing and losing the edits. An MP4 with sound that fails while saving reports the error instead of crashing.
- Deleting the open capture from the editor closes it, and a capture deleted elsewhere while open can still be saved as a file. The editor used to keep failing to save and refuse to open another picture.
- A damaged album says why it cannot open instead of calling the password wrong, and an album from a newer KillerShot is refused with a translated message to update. A failed rename or an interrupted password change leaves the album as it was, with no leftover copy.
- Damaged settings fall back one value at a time and are backed up once, and one damaged saved region no longer erases the others. Unsaved changes survive later saves, a settings file another program holds for a moment no longer leaves KillerShot on empty settings, and a failed save never overwrites a newer value.
- The shortcut recorder refuses chords Windows owns, such as Alt+F4 and Win key chords. Alt+F4 could be recorded as a capture shortcut and then stopped closing windows in every app.
- Global shortcuts come back when a shortcut recorder is left listening and the window hides to the tray, loses focus or closes its settings.
- Print Screen shows the frozen screen sooner, without waiting for the capture bar. On three monitors the first press went from about 615 ms to 340 ms.
- A frozen desktop capture always gets finished pixels, never a frame GDI is still drawing.
- Recorded sound stays in sync after the microphone or system audio stalls briefly, and a recording stops cleanly when the monitor resolution changes. The recording frame and its bar stay on the recorded area on a second monitor with a different display scale.
- Scrolling capture keeps its place when the region includes the window's scroll bar, and unavailable scrolling-capture keys show a warning. Holding Alt while clicking during a region capture no longer drags the dimmed screen off its monitor.
- Large libraries stay responsive. Collection strips, searches, thumbnails, image decoding and failed recognition jobs no longer hold up the UI thread or other library work, importing a large album no longer stalls the library, and shelf scrolling works normally.
- The command line and KillerMCP work while another KillerShot of the same version with a different SQLCipher engine is running. Every command used to fail.
- `killershot print` prints only the picture it was given, and a picture that fails to open no longer blocks later open requests. A command that times out is withdrawn, so a script that retries it does not get it done twice.
- `killershot edit --out` takes a folder, and the command line refuses an option given as another option's value, an oversized region or an unwritable file type as usage errors. `killershot qr` refuses picture files over 400 MB and has a work limit.
- `killershot regions` prints plain minus signs, recognition languages match in any case, and `capture --editor --no-save` reports success with a warning when only the clipboard got the capture. The headless `--selftest` accepts a hexadecimal `--window` handle.
- Linked folders refresh from file changes, recover from missed events and back off on unavailable network paths. A file saved through a temporary copy no longer vanishes from the list, and deleting a picture that is already gone says so.
- The file picker loads icons and previews in the background, no longer stalls on a disconnected network folder, and reopens on any monitor. It refuses relative or malformed typed paths, filters on a pattern like *.log, and accepts a full typed path even when the shown folder failed to list.
- Capture and recording file names use the Gregorian year on Thai, Arabic and Persian region settings instead of years like 2569. A recording's Save as strips characters a file name cannot carry from the window title.
- Canceling the discard prompt keeps a capture's review bar and Ctrl+Enter, and Escape while dragging a Read a region area cancels the drag instead of cropping. Deleting a capture while its marks save, or changing recognition languages mid-read, no longer causes errors or stale text.
- The theme and language menus line their labels up with their marks, keep 8px between rows and the card edge, and fit inside a short window instead of running past its bottom.
- Dialog title bars match the rest of the dialog in every theme except 98SE, Mourning highlights selections in its original peach again, and 98SE close buttons draw the Windows 98 pixel X.
- The sidebar's right-click card always opens, and About, the shortcuts sheet or a settings panel reopened while fading out stays open. The album manager and toolbar customizer repaint when a contrast theme changes, and Escape closes an open drop-down first.
- The keyboard map shows both actions on keys like Ctrl+M, Del and F2, and the version number no longer runs into the corner grip in a narrow portable window.
- More of the interface follows the app language and has screen reader names, including the color picker, settings fields, the keyboard map, scrollbars and the Magenta accent. Touch presses no longer crash dialogs, and focus cues and error warnings use the theme's colors.

### Changed
- The album has four views: Shelves, Grid, Timeline and Files. The large single carousel is gone, the footer keeps only the view buttons, and with one collection picked Shelves shows a carousel per day.
- Right-click menus, the tray menu and the toolbar menus are family tile cards, most used first, with actions that don't apply dimmed in place. The pencil on the tray, Capture, picture, capture and file cards lets you reorder or hide tiles.
- A capture taken while the window is open goes to the album and folder without replacing what's on screen, unless Open editor after capture is on.
- Albums keep a write-ahead log, so deleting, noting or recognizing captures no longer fails with "database is locked" during long album work. An album folder on a network share is refused, because the log does not work over a network.
- Updated the encrypted album engine to SQLCipher 4.20.0 (SQLite 3.54.1) and SQLite bindings to 11.0.0. Existing albums open unchanged.
- Text fields match the height of the dropdowns beside them, and menus and cards use the full text color with an even 8 px edge. The footer copyright ends 8 px before the corner grip, as in every Killer app.

### Added
- Files (F9) browses folders in list, icons and details views, with file commands, rename, clipboard, drag and drop and thumbnails. Images open in the editor, and Ctrl+wheel steps through the view sizes the way KillerShell zooms.
- Files opens folders in tabs like KillerShell's, kept across restarts, and a linked folder can open as a Files tab. The location row stars folders (Ctrl+D), lists the last 15 folders (Alt+R), exports the listing as CSV or HTML, and docks a details pane (Alt+P).
- Screen recordings show up in the library. After your first one, KillerShot asks once whether to keep them as plain video files or move them into the encrypted album, where they play from a temporary copy deleted when the album closes.
- Auto redact, beside Redact, finds email addresses, card numbers, phone numbers, US Social Security numbers, IP addresses, keys and passwords and covers each with a box you can remove before saving. It can also scan every new capture before it is saved, copied or added to the album.
- Make PDF turns a folder's or a collection's pictures into one new KillerPDF document from its right-click card. It appears only when KillerPDF is installed.
- Trash Polka, a new theme under 98SE, is black with a tattoo-style ink background in your accent color, Blood red by default. The ink is prepared in the background, so the first switch takes under a second at 4K.
- Dragging a capture onto a collection in the sidebar, the library or the toolbar picker moves it there, the same as Move to (Ctrl+M). Dropping it on Unfiled takes it out of its collection.
- Back, first on the toolbar, and Alt+Left return from an open picture to the library. The mouse's back and forward buttons step through the editor, library and Files history.
- Capture remembers the last Region, Window, Monitor or All Monitors choice, even when the selector is canceled.
- The save and folder pickers have a New folder button (Ctrl+Shift+N).

## [1.1.1] - 2026-10-28

KillerShot 1.1.1 catches Print Screen in apps run as administrator and fixes problems found after 1.1.0.

### Added

- "Use KillerShot for the Print Screen key" now works in apps run as administrator too, such as an admin PowerShell window or Registry Editor. Windows hides those keys from ordinary programs, so KillerShot adds a small helper in Program Files that Windows allows to see them. An all-users install includes it; for any other copy, turning the switch on offers to install it with one Windows permission prompt. The helper stops whenever KillerShot closes, so Print Screen goes back to Snipping Tool as before.

### Fixed

- With the sidebar on the right, the theme and language menus open in the bottom-right corner beside the rail, instead of across the window in the bottom-left corner.
- The theme menu's accent colors are drawn whole instead of cut off on the right, and they sit right beside the theme names.
- With "Use KillerShot for the Print Screen key" on, the tray menu shows the Print Screen shortcuts again.
- Alt+Print Screen no longer leaves the captured window's menu bar selected, and a Windows key shortcut on Print Screen no longer opens Start.
- The Print Screen key keeps working after KillerShot is busy for a while. Windows could quietly take the key back if KillerShot's window was slow to respond, until KillerShot was restarted.

### Changed

- Clicking the tray icon opens KillerShot instead of starting a region capture. Print Screen and the tray menu still capture, and a click still stops a recording.
- The footer shows the picture's size with its unit, for example "1920 x 1080 px".

## [1.1.0] - 2026-10-27

KillerShot 1.1.0 is a much smaller download and fixes problems found after the first release.

### Fixed

- Updates keep working after the publisher's code signing certificate is renewed. A release must still carry a valid signature naming the same publisher, but no longer the exact certificate of the installed copy.
- Exit from the tray icon closes the window the normal way, so the prompt for unsaved edits can still cancel it.
- In High Contrast, hovered menu items use the system highlight colors.
- Dragging the carousel quickly through a large folder no longer leaves the pictures it lands on blank. Thumbnails load for the pictures in view first, and ones skipped along the way load when you come back to them.
- A large linked folder fills in as it is scanned instead of showing only a loading spinner until the last file is read.
- Window mode can pick KillerShot's own editor window, so KillerShot can take a picture of itself. Its capture overlays and bar stay out of the picker.
- A right-click menu opens right against the pointer. A tall menu no longer starts about 30 pixels above the click and runs far up the screen, and the tray icon's menu opens from the pointer upward, out of the notification area, as in every Killer app.
- Delete works on pictures in folder collections. It asks first and sends the file to the Recycle Bin, so a mistake can be undone there. In a folder with no Recycle Bin, such as a network share or a USB drive, it says the file is deleted for good before asking.
- A folder that cannot be added as a collection reports the reason in the status line instead of an unstyled message box.
- In a maximized window, each carousel in Multiple carousels reaches both ends of the window when it has enough pictures, instead of filling a column in the middle.
- Dragging the carousel slider moves the carousel as fast as the pointer. A long jump lands at once and glides only the last few pictures, instead of slowly turning past every picture on the way.

### Changed

- The download is about 7 MB instead of 33 MB. Window and screen capture, recording and text recognition now call Windows directly instead of shipping the 25 MB Windows SDK projection.
- Dates and numbers keep the Windows region format whatever the interface language, as in the other Killer apps.
- The album manager opens as its own window in the family dialog frame, with a title bar, a close button and a draggable caption, like the database windows in KillerNotes and Killendar, instead of a dropdown that closed on any outside click. Its album list is the family database list, with the same recessed pane and row selection.
- Right-click menus belong to where you click. The sidebar's menu holds the collection, its previews and which side the sidebar is on; the carousel's menu holds where the carousel sits and its info cards; the album area's menu holds the views. The position options read "Sidebar on the left" and "Carousel at the top" instead of "Dock on the left", and "Reverse icon order" is gone.
- The capture bar is title-bar height, with its buttons and mode list all the same height, instead of buttons that made it taller.
- In About, rendering is a dropdown beside its label instead of three radio rows, and the options sit in two columns of even length. The Print Screen option explains itself in its tooltip, with a note under it only when Windows has taken the key back.
- The mouse wheel steps the carousels the other way: wheel up moves forward and wheel down moves back.
- The footer is a plain status line, as in KillerNotes, Killendar and KillerPDF, without the green light or a standing "Ready".
- Menus show only what can be done right now, instead of greyed-out rows, and a menu with nothing to offer does not open.
- The sidebar slides closed, open and to its other side instead of jumping.
- With Close to system tray on, closing the window hides it and keeps everything in it, so Open from the tray shows it again at once instead of rebuilding it. The tray icon's menu leaves out what cannot be done right now.
- The sidebar's right-click menu no longer offers Library, which opened the album manager a second way and could leave the window not responding. The album manager opens from its button in the title bar.

### Added

- Minimize collections, in the sidebar's right-click menu, shows each collection as one line with its name and count, so many more fit in the sidebar. The preview style comes back when it is turned off.
- The picture's right-click menu in the editor has Delete for an album or folder capture.
- "Use KillerShot for the Print Screen key", in About and as a tick item in the tray icon's menu, gives Print Screen to KillerShot instead of Snipping Tool while KillerShot is running. Closing KillerShot gives the key straight back to Snipping Tool, and Windows' own setting is never changed. Win+Shift+S stays with Snipping Tool.

## [1.0.0] - 2026-10-27

KillerShot combines screenshot and screen recording tools with an encrypted capture album, image editing and local QR reading.

### Data protection and recovery

- Self-update checks the downloaded and staged copies for the publisher's signature inside the helper, keeps every parent folder pinned while it publishes, refuses redirected paths and malformed requests, and offers only stable releases.
- Unreadable album edits open the flattened image safely and preserve recovery data. QR and text recognition follow edits and reject obsolete results; album actions stay bound to their original album.
- Redactions keep hidden pixels covered when resizing inserted pictures, including transparent and fractional layers.
- Albums recover after interrupted password changes or removal, preserving captures and recovery copies when cleanup fails. Failed setup keeps the album closed until the problem is corrected. (#1) (#4)
- Captures waiting in memory remain visible after a failed album write, save on retry and warn before exit. (#2) Waiting captures are capped at 1 GiB; past that, new captures still copy and save but skip the album with a warning, and nothing already waiting is lost.
- Failed album edits stay open for retry or recovery through Save As, and delayed edits cannot cross into another album. Exit and installation restart recheck work changed while saving, including text still being typed.
- Installation and removal serialize changes, preserve newer installations, and restore the previous image, shortcuts and registration when setup fails.
- Unsaved edits require confirmation before another image opens or the app exits, with Save, Discard and Cancel choices. Save As and image-edit dialogs reject a replaced image, and editor output controls are disabled while the album is visible. Retention changes apply only after Save.
- Saving animated PNG, GIF, TIFF, BMP or mismatched image files offers a separate static PNG or JPEG export to preserve the original. Image edits retain transparency, and reopened files show their latest contents.
- Concurrent album edits, search recovery, settings writes and output-file collisions are handled safely. Failed retention preserves search results, missing search entries repair on reopen, and capture IDs remain unique after deletion. OCR failures stay with their own album.
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

- Shelves open on their middle picture, so each carousel fills the band from edge to edge.
- The photo strip's carousel keeps every picture's top in view when a wide, short capture is in front.

- Captures are stored in an encrypted SQLCipher album with AES-256 encryption, opened through the Windows account or an optional password. Create, open, rename and switch albums from the title bar; each keeps its own retention and QR settings. External albums open as managed copies.
- Copy text (Ctrl+Shift+C) reads the text in a picture on this computer, copies it with its line breaks and shows it in a card to select from; Read a region reads a selected area. Crop and text regions support Enter to start or apply, arrows to move, Ctrl+arrows to resize, Shift for larger steps and Escape to cancel, with spoken selection details. Choose the recognition language there, and read the album's text again from album settings after adding a language.
- Search window titles, program names, notes and recognized image text. Organize captures into collections, add notes and limit history by count or age. Existing Recent captures import on first start.
- Browse albums as shelves of carousels, one large carousel, a grid or a timeline, grouped by date or collection. Ctrl+L shows the album over an open image.
- Add local or network folders as saved collections, including subfolders, while keeping images in their original locations. Background scans refresh changed images, retry unavailable folders and locked files, and retain accessible images with an availability warning.
- The Windows Screenshots folder appears as a live collection with Open, Copy, Show in File Explorer and Add to the library actions. It can be hidden in album settings and stays under its own heading when grouped by collection. (#6)
- The collection sidebar docks left or right and the photo carousel above or below the picture, each with its own sizing and collapse controls. Over the album's large carousel and shelves views the editor's photo carousel steps aside, so pictures never appear in two carousels at once. Collection filters apply to the album and editor carousel; preview styles include compact photo piles.

### Editing and output

- Marks on album captures stay editable: the encrypted album keeps them beside the untouched original, reopening brings them back, and copying, printing, saving or dragging from the album always gives the flattened result. Select several marks with Ctrl+click or Ctrl+A to move, duplicate (Ctrl+D), reorder (Ctrl+] and Ctrl+[) or delete them together; resize from the selection's grip and edit placed text or callouts with F2 or a double-click.
- Annotate with pen, highlighter, arrows, lines, outlined or filled shapes, text, numbered steps, speech-bubble callouts and opaque redaction that replaces every pixel it touches, at any zoom and after moves or crops. Without a mouse, Enter places the current tool's mark, Tab moves between marks, arrows move and Ctrl+arrows resize them, and screen readers can name, select, move and resize each mark. Crop, rotate, flip, resize, add a margin or trim a plain border from the Image menu or its keys (Ctrl+H and Ctrl+G rotate, Ctrl+Shift+H and Ctrl+Shift+G flip, Ctrl+I resizes, Ctrl+M adds a margin, Ctrl+T trims, Ctrl+Shift+O inserts a picture), with marks following the picture. Select and move annotations, undo and redo, and use Fit or 100% zoom. Exports keep the capture's exact pixel size.
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
- More keys: Ctrl+Q reads a QR code and Ctrl+Shift+Q turns QR reading on or off, Ctrl+, opens capture settings and Ctrl+Shift+, video settings, Ctrl+Shift+L the library settings, Alt+Shift+T the theme and Alt+Shift+L the language menu, and Ctrl+Delete discards a capture under review. Over the album, Ctrl+Shift+S saves the focused capture elsewhere, Ctrl+M moves it to a collection, F2 edits its note and Ctrl+E shows a Screenshots-folder file in Explorer; Ctrl+Shift+N starts a new collection.
- `killershot edit` trims, rotates, flips, resizes or frames a picture file with the editor's own commands and saves the result beside the original unless `--out` names a file.
- `killershot text` reads the text in a picture file or a selected region on this computer and can copy it; `text --languages` lists the recognition languages. `killershot recent --delete <n> --yes` removes a capture from the album, and `killershot settings --set name=value` changes any capture, recording or text recognition setting it lists. `killershot collections` lists, creates, renames and deletes collections, and `recent --move` and `recent --note` file a capture or set its note. `killershot recording pause|resume|stop|cancel` controls the running app's recording, and `killershot print` opens a picture in its print preview.
- Ctrl+Shift+P starts a scrolling capture, Ctrl+Shift+T reads the text in a dragged part of the open picture, Alt+1 to Alt+9 capture the saved regions in menu order, and Ctrl+Shift+X exits. On the command line, `killershot scroll` starts a scrolling capture, `killershot paste` saves the clipboard's picture with `--out` or opens it in the app, `killershot regions` lists and removes saved regions, `text <image> --rect` reads part of a picture, and `killershot exit` closes the app.
- `killershot qr` reads QR codes with JSON output and link warnings. `killershot --help` lists commands; `--cli` and `KillerShot.Cli.exe` remain supported, and portable copies run from their folder or full path.
- `KillerShot.exe --open` hands images to a running instance. Concurrent requests queue in order, confirm delivery and retain the correct album. Command-line captures refresh the open album, report failed saves and password limits, and cancel when selection closes. Completed recordings survive audio errors and report a warning.

### Desktop, accessibility and localization

- The print preview caption switches between the wordmark and the plain title when the theme changes while it is open, and its wordmark takes the main title bar's spacing so its brush strokes have room. The file picker's recent locations and places lists are named for screen readers.
- File dialogs cancel stale loads, bound large listings and identify incomplete results without blocking the interface.
- Black table headers match the content surface, and softer grain keeps dark surfaces from looking gray.
- About groups settings in two columns and can hide shortcut hints in menus and tooltips without disabling keyboard shortcuts. Hover tooltips clear large pointers while preserving custom placement.
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

### Look and family

- Menus and submenus taller than the screen scroll, so a long collection list reaches its last entry.
- The footer shows the capture count and the family status light beside the status line: green when things are fine, amber while a crop or text region waits on you, red when something failed. The collection dropdown lives in the toolbar only.
- Dialogs, the color picker and print preview are edged in the app border color and follow theme changes.
- Wordmarks and headings are raised with a subtle bevel in their own color, and wordmarks carry a drop shadow. The Dark theme's red accent is the brand red.
- Theme menu hover rows reach the right edge with the same inset as the left.
- Hovered menu and list items show their text in the theme accent, lightened or darkened only as far as needed to stay readable on the hover fill.
- KillerShot shares the family's theme engine, control styles and palettes with KillerShell, KillerScan and the KillerUI template, so its colors, grain and geometry match the rest of the family: outline buttons with an accent outline, title bar buttons that hover in the accent, a gray close button until hovered, family combo boxes, the family 1px window edge and wordmark emboss, and 98SE caption buttons, tabs and scroll bars. Hold Alt and drag anywhere in a window to move it.
- The window caption menu, language menu and control styles come from the KillerUI template.
- The theme and language menus open in the content pane's lower corner beside the sidebar, as in the rest of the family.
- The file picker is the family picker shared with the other Killer apps, with an image preview and shell icons in the icon view. Accept stays off while a folder loads or an accept is running, and a mistyped folder returns to the last folder that worked.
- The footer's Install button runs on the shared KillerUI template code, with exit checks, an all-users choice and a deferred restart.
