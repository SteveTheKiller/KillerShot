# Microsoft Store listing

`KillerShot-Store-listing.csv` contains listing copy for all 20 app languages, using the family's 70-field Store CSV format. Optional fields remain blank. The Store product ID is `XP8K4LFXRB8PHX`. `release.ps1` builds and signs `KillerShot-Store.exe`, publishes it to GitHub, and submits the versioned package URL through `Submit-StoreRelease.ps1`, including translated listing updates.

The copy describes the current app and keeps album encryption separate from exported files and clipboard storage. The Store package includes its .NET runtime and supports Windows 11 x64.

`StoreLogo.png` is an unchanged copy of `Resources/app-icon-master.png` at 1063 x 1063 pixels. The five screenshots are 2160 x 1350 PNGs rendered from the current app with the isolated demo fixtures used by `Shell/DemoMode.cs`:

1. `screenshots/01.png`: selected capture in the editor.
2. `screenshots/02.png`: grid grouped by collection.
3. `screenshots/03.png`: multiple carousels.
4. `screenshots/04.png`: timeline.
5. `screenshots/05.png`: search results.

The English screenshots are shared by all language columns. Refresh the copy and screenshots against the release build before the eventual first submission.
