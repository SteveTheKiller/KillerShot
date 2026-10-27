# Microsoft Store listing

`KillerShot-Store-listing.csv` contains local listing copy for all 20 app languages, using the family's 70-field Store CSV format. Optional fields remain blank. KillerShot 1.0.0 was released on 2026-10-27. These files prepare the listing only; they do not register a Store product, submit a listing, or change packaging and release automation.

The copy describes the current app and keeps album encryption separate from exported files and clipboard storage. The listed platform is Windows 11 x64. Packaging and any runtime prerequisite must be confirmed before the first Store submission.

`StoreLogo.png` is an unchanged copy of `Resources/app-icon-master.png` at 1063 x 1063 pixels. The five screenshots are 2160 x 1350 PNGs rendered from the current app with the isolated demo fixtures used by `Shell/DemoMode.cs`:

1. `screenshots/01.png`: selected capture in the editor.
2. `screenshots/02.png`: grid grouped by collection.
3. `screenshots/03.png`: multiple carousels.
4. `screenshots/04.png`: timeline.
5. `screenshots/05.png`: search results.

The English screenshots are shared by all language columns. Refresh the copy and screenshots against the release build before the eventual first submission.
