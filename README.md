# McNeff Helper

A Windows companion for **We Steal From Old People: The Game**, with save editors, a build reference, and live gameplay switches.

**[Download McNeff Helper.exe](https://github.com/gohanthe5th/McNeff-Helper/raw/refs/heads/main/download/McNeff%20Helper.exe)**

Requires 64-bit Windows, .NET Framework 4.8, and the supported Steam game version **0.1.3, build 26629900**.

## Using the app

Download the EXE and open it. Choose Save 1, 2, or 3 in the save picker.

Close the game before editing bricks, unlocking recipes, or adding builds. Save edits create backups automatically. Start the game to use the live switches, and keep McNeff Helper open while those switches are on.

### Bricks

Set Grey Blocks to a chosen count, up to 9,999,999. Set an individual colored brick or all 100 color and shape variants, up to 999 each.

### Project Manager

Browse 981 stock recipe layouts, including 940 available recipes and 41 marked unavailable. Search names, filter unlock status, and sort by rarity.

Each recipe shows a build preview, its effect, rarity, safe income, materials, and brick sub-stats. Sub-stats use an owned build's saved bricks when a copy is available.

Unlocked recipes stay checked. Check locked recipes individually or use Select All, then unlock the checked recipes together. Quick Unlock works on the highlighted recipe when the queue is empty.

Add Missing Bricks fills a recipe's shortages. Add Build queues a completed build at the lair playmat. Finish placing or selling that pending build before adding another.

### Live Cheats

Turn unlimited health and stamina on or off. Set the old-people multiplier from 1× to 1,000×, choose whether to override spawn caps, and click Apply.

The cap override bypasses the ambient old-person group-size, world population, and on-field presence caps. Native placement rules and growth timing still apply. A filled bench may take up to its next 50-second growth step to adopt the new target.

Enemy Types contains the No enemies master switch and separate switches for Fans, YouTubers, Lawyers, Internet Investigators, and Elites. Switching the master off restores the individual choices. These switches block new hostile spawns.

Keep enemy suppression off for required tutorial encounters and when collecting elite drops.

## Build from source

The project uses Windows Forms and the C# compiler included with .NET Framework. Build it on 64-bit Windows from PowerShell.

```powershell
.\Build-Trainer.ps1
```

The output is `bin\McNeff Helper.exe`. To choose a different destination, pass `-OutputPath`.

All required artwork and catalog inputs are included in this repository. The game installation is required when running save editing and live features.

## Verification

The verification scripts exercise save edits on copies of local saves and test live switches in an isolated process that maps the supported game library. Full-heist gameplay verification is still pending.

With the supported game installed, the save-editor checks can be run from the repository root.

```powershell
.\tests\Test-ProjectManager.ps1 -Executable '.\bin\McNeff Helper.exe'
.\tests\Test-InventoryUI.ps1 -Executable '.\bin\McNeff Helper.exe' -RenderPath '.\bin\inventory-preview.png'
```

`tests\Test-Trainer.ps1` uses `tests\NativeImageFixture.cs` compiled as a separate `WeStealFromOldPeople.exe` fixture. Close the real game before running that fixture. The script checks patch restoration, switch combinations, Apply, and the cap override.

The downloadable EXE's SHA-256 checksum is in [download/SHA256SUMS.txt](download/SHA256SUMS.txt).

## Artwork and game data

The included game artwork, icons, recipe data, and font were extracted from the supported game installation. See [NOTICE.md](NOTICE.md) for attribution.
