# Mass Effect Archipelago Launcher

One download for playing the Mass Effect Legendary Edition trilogy in an [Archipelago](https://archipelago.gg)
multiworld. The launcher fetches each game's world, game bridge and mod from that game's own project, installs them
with backups, writes your player options, and starts the game with its Archipelago client.

![Deploy page](docs/deploy.png)

| Game | Project |
| --- | --- |
| Mass Effect 1 | [ME1-APWorld](https://github.com/Mass-Effect-LE-Archipelago/ME1-APWorld) |
| Mass Effect 2 | [ME2-APWorld](https://github.com/Mass-Effect-LE-Archipelago/ME2-APWorld) |
| Mass Effect 3 | [ME3-AP-World](https://github.com/Mass-Effect-LE-Archipelago/ME3-AP-World) |

A launcher built from a fork of this repository uses its owner's fork of a game (same repository name) when that fork
has a release, and the project above otherwise.

This is an unofficial fan project. It is not affiliated with or endorsed by BioWare or Electronic Arts, and it contains
no game assets. The art and effects are drawn in code. You need your own copy of Mass Effect Legendary Edition (PC).

## Use it

1. Install [Archipelago](https://github.com/ArchipelagoMW/Archipelago/releases) 0.6.8 or newer.
2. Download `Mass-Effect-Archipelago-Launcher.exe` from [Releases](../../releases)
   and run it. Nothing else needs to be installed: the launcher is self-contained.
3. Pick a campaign on the left.
   - **Deploy**: downloads the game's newest release (checked against its SHA-256) and installs it. Anything replaced is
     backed up, and **Uninstall** puts every file back. When a newer release comes out, the button says **Update**.
   - **Folders**: the Legendary Edition and Archipelago folders are detected; change them here if needed.
   - **Options**: pick a preset or set each option, then **Save to Players**. Hover an option to read about it.
   - **Intel**: how this game starts (save imports and so on) and the release notes.
4. **Launch** starts the game and, if ticked, its Archipelago client.

Mass Effect 1 also needs ME3Tweaks Mod Manager once, for its ASI Manager (the ASI loader and LE1 Autoload Enabler).
ME2 and ME3 need nothing else: the launcher adds its own ASI loader when no other loader is present.

Downloads are kept in `%LOCALAPPDATA%\MassEffectAPSetup\downloads`; install records and backups in
`%LOCALAPPDATA%\MassEffectAPSetup\<game>` (the same place Mass Effect AP Setup used, so earlier installs are recognised).

## For game releases

The launcher reads the newest release of each game project through the GitHub API and takes, in order:

1. a release asset whose name contains `payload` and ends in `.zip`, laid out like this:
   ```
   <world>.apworld
   ASI/<bridge>.asi            (plus any other bridge files, e.g. LE1APBridge.checks.tsv)
   <ModFolder>/moddesc.ini
   <ModFolder>/DLC_MOD_<...>/  (and BASEGAME/ for ME1)
   bink2w64.dll                (ME2/ME3: the AP ASI Loader)
   options.json                (optional: the world's option schema, so Options matches that world version)
   ```
2. otherwise every `.apworld`, `.zip`, `.asi` and `.dll` asset and an `options*.json` schema (for example
   `options-me1.json`; setup apps are skipped), assembled by file and folder name.

SHA-256 sums come from a `SHA256SUMS` asset or a `SHA-256 of <file>: <hash>` line in the notes. Files without a sum are
still installed and the log says so.

## Build

Needs the .NET 10 SDK and [LegendaryExplorer](https://github.com/ME3Tweaks/LegendaryExplorer) (for the LE1/DLC file
tables) checked out at `vendor/LegendaryExplorer` (tested with commit `bd5ec55`):

```
git clone https://github.com/ME3Tweaks/LegendaryExplorer.git vendor/LegendaryExplorer
git -C vendor/LegendaryExplorer checkout bd5ec55
dotnet build src/MassEffectAPLauncher -c Release
python tests/test_launcher.py --rad-bink "<...>\Game\ME2\Binaries\Win64\bink2w64_original.dll"
python tools/publish.py          # dist/Mass Effect Archipelago Launcher.exe + SHA256SUMS (records this repo, from git)
```

The tests download each game's release and install it into scratch folders, never into a real game. The command
line the tests use is listed at the top of `src/MassEffectAPLauncher/Program.cs`. `--screenshot <png> <game> <page>`
renders the window for docs.

## Credits

- **Original launcher:** [ME-Archipelago-Launcher](https://github.com/Mass-Effect-LE-Archipelago/ME-Archipelago-Launcher)
  by Jerrod (Unowa001).
- **Modified by Tyler Kessler** (November 2026): the launcher uses its owner's fork of a game when that has releases,
  and reads each release's option schema. Every change is in the git history.
- [ME3Tweaks](https://me3tweaks.com): LegendaryExplorerCore (GPL-3.0), used for the LE1 and DLC file tables.
- [Archipelago](https://archipelago.gg): the multiworld framework the launcher installs games for.

## License

GPL-3.0 (the launcher links LegendaryExplorerCore, which is GPL-3.0). See [LICENSE](LICENSE).
