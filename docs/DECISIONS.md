# Decisions

Why Safe Disk Cleaner cleans what it cleans, what it deliberately leaves alone, and where
the C# port differs from `reference/engine.py`. Written during the first build (Nov 2026).

## The test every new rule has to pass

A rule beyond the reference engine is added only if it passes all four:

1. **Common**: found on ordinary student and gaming PCs, not just developer machines.
2. **Big enough**: usually around 100 MB or more.
3. **Provably regenerable**: the owning app or Windows recreates it, with a source cited in a
   code comment next to the rule.
4. **Explainable**: one line a non-technical friend understands.

## Research pass

A short look at what established tools clean, to find gaps worth filling:

- **BleachBit** has cleaners for Chrome, Chromium, Brave, Edge, Firefox, Opera, Vivaldi,
  Discord, Slack, VS Code, Thunderbird, Windows Explorer (thumbnails, MRU lists), Windows
  Defender and others ([cleaner list](https://github.com/bleachbit/bleachbit/tree/master/cleaners)).
  Much of it is privacy cleaning (history, cookies, MRU lists), which is out of scope here.
- **Windows Storage Sense / Disk Cleanup** handle temporary files, the Recycle Bin, Delivery
  Optimization files, previous Windows installations (Windows.old), Windows Update cleanup,
  thumbnails and Defender files
  ([Storage Sense](https://support.microsoft.com/kb/5000679),
  [removing Windows.old](https://support.microsoft.com/en-US/Windows/Deployment/Install-Upgrade/delete-your-previous-version-of-windows)).
- **Microsoft PC Manager** "Deep cleanup" covers Windows Update leftovers, the Recycle Bin,
  browser and app caches, temp files and Prefetch. Reviewers noted its estimates can be far
  from what is actually freed, which is why this app measures freed space on the drive.
- **What eats space on ordinary PCs**: browser caches (Chrome, Edge, Brave, Opera, Firefox),
  Electron apps (Discord, Teams, Slack, Spotify), game launchers (Steam, Epic, Ubisoft),
  GPU driver installer leftovers (NVIDIA, AMD) and Windows Update downloads.

## Added

| Addition | Common | Size | Regenerable (source) | One line |
|---|---|---|---|---|
| Brave, Opera / Opera GX, Slack, Teams (new), Spotify, Cursor, Windsurf web and GPU caches | yes | 100 MB to 1 GB+ each | Chromium recreates its HTTP, code and GPU caches ([Chromium disk cache](https://www.chromium.org/developers/design-documents/network-stack/disk-cache/)); Microsoft documents clearing the new Teams cache ([Learn](https://learn.microsoft.com/troubleshoot/microsoftteams/teams-administration/clear-teams-cache)) | "Web and GPU cache folders; logins and settings stay." |
| Firefox disk cache (`cache2` in the Local profile folder) | yes | up to about 1 GB | Firefox keeps the cache apart from personal data and rebuilds it ([profiles](https://support.mozilla.org/kb/profiles-where-firefox-stores-user-data), [clear cache](https://support.mozilla.org/kb/how-clear-firefox-cache)) | "Firefox's web cache; pages load a bit slower once." |
| NVIDIA manual-install copies in `C:\NVIDIA\DisplayDriver\<version>` | yes, for anyone who installs drivers from nvidia.com | about 1 GB per version | Only used during setup; running the downloaded installer again recreates them ([TweakGuides](https://xjdhdr.gitlab.io/tweakguides/system_guides/NVFORCE/NVFORCE_4.xhtml)) | "Unpacked copies of NVIDIA drivers that are already installed." |
| "Windows storage settings" in the overflow menu | n/a | n/a | n/a | One click to Settings > System > Storage. |
| "Last cleaned 12 days ago, freed 1.5 GB" line | n/a | n/a | read from `runs.csv` | |

The app-cache additions use the same signature check as the reference (`IsChromiumCache`), so
only folders that look like a browser cache are ever picked: never settings, logins, offline
music or downloads. The NVIDIA addition uses the same version gate as the NVIDIA app rule:
only versions at or below the driver `nvidia-smi` reports as installed, only folders named
exactly like a version, and only after a day without changes.

## Rejected

| Idea | Why not |
|---|---|
| AMD driver leftovers (`C:\AMD`) | AMD community reports that recent chipset drivers keep their uninstaller in `C:\AMD`, so deleting it can leave a driver that cannot be uninstalled. There is no primary source for which subfolders are safe, and no AMD PC was available to check folder names or where the installed version is recorded. Worth revisiting with an AMD machine to test on. AMD's shader caches (`%LOCALAPPDATA%\AMD`) are protected. |
| Windows Update cleanup (WinSxS) by hand | Never by hand. Windows' own "Update Cleanup" handler does it (see Disk Cleanup parity). |
| NVIDIA, AMD and Steam shader caches | Games stutter while they rebuild. Protected and listed under "Kept on purpose". The DirectX one is an opt-in rule, as in Disk Cleanup. |
| Steam partial downloads (`steamapps\downloading`) | May be resumed; Steam manages them. |
| INetCache outside the `IE` buckets | `Content.Outlook` can hold the only copy of an attachment someone opened and edited. Only `INetCache\IE` and `INetCache\Low\IE` are cleared. |
| Prefetch | Windows manages it; clearing it slows the next app launches and boot. A classic "optimiser" myth. |
| Browser cookies, history, sessions, saved logins | Personal data; clearing it signs people out. |
| Chat history (Claude Code transcripts, Discord, and so on) | Personal data, and Claude Code already expires its own transcripts (spec). |
| Spotify song cache and offline downloads | Mixed with offline music; Spotify's own Settings > Storage > Clear cache is the safe route. Only Spotify's embedded browser cache is cleared. |
| WhatsApp desktop | No reliable source on its folder layout; media may sit next to caches. |
| Hibernation file, page file, System Restore points | System settings and safety nets, not caches. |
| Windows event logs (`.evtx`) | Needed to diagnose problems. Old text logs in `Windows\Logs` are cleared instead, as Disk Cleanup does. |
| Windows Installer cache, Package Cache | Deleting them breaks repair and uninstall. Protected. |
| NVIDIA `Installer2` under Program Files | Users report that deleting it breaks uninstalling NVIDIA software. |
| Microsoft Store cache (`wsreset`) | Small, and Windows has its own tool. |
| Language files by hand | Only Windows' own "Language Pack" handler removes them, so a language someone uses is never broken. |
| Disk Cleanup's "Downloads" and "User file versions" | They empty the Downloads folder and File History backups: personal files. Listed under "Kept on purpose". |
| System error minidump files | Needed to diagnose blue screens; the Minidump folder stays protected. Only the single full `MEMORY.DMP` is offered, opt-in. |
| Defender `Quarantine` and scan history | Quarantine holds files Defender may need to restore. Only `LocalCopy` and `Support` files idle 7+ days are cleared, as the spec says. |
| Battle.net, EA app, Riot caches | Outside the allowed roots (ProgramData) or no citable source on what is safe. |
| Epic `VaultCache` | Unreal Engine marketplace assets; developers only. |
| Teams classic | Retired by Microsoft. |
| Adobe media caches | Creators only, and the location is user-configurable. |
| Intel driver extraction (`C:\Intel`) | Rare now, and no version to gate on. |
| Old files in Downloads | Personal files. Storage Sense can do this if someone opts in. |
| Registry cleaning, "optimisers", startup manager, disk-usage tree map, duplicate finder, scheduling, auto-updates, telemetry | Out of scope by design: one window, one scan, one Clean button. |

## Disk Cleanup parity (spec update, Nov 2026)

The owner asked for the app to do everything Windows Disk Cleanup does as admin, and keep
clearing the app caches it never sees. `reference/engine.py` (commit d39bc01) and the
"Disk Cleanup parity" section of `docs/SPEC.md` are what the C# follows.

- **File-based categories are the app's own rules**, so the guard checks every path and sizes
  are real: thumbnail cache, the `INetCache\IE` buckets, user error reports, old text logs in
  `Windows\Logs` and `System32\LogFiles` (files only, folders stay, `MeasuredBoot` and
  `SystemRestore` skipped), `SystemTemp`, WDF and HiveUpload, Feedback Hub and diagnostic data
  archives, RetailDemo content, Defender's `LocalCopy` and `Support` files, old setup logs in
  `C:\Windows` and `*.CHK` files in `C:\FOUND.000` to `.009`. The new folders are allowed
  roots, so each root itself is never deleted.
- **Loose files in `C:\Windows`** pass only through `ALLOWED_FILES`, matched on the file name
  of a file directly in that folder. `C:\Windows\Minidump\x.dmp` and `C:\Windows\explorer.exe`
  never match. There is no bypass: these files still go through every other guard check.
- **Windows' own handlers** for what only Windows can remove safely (component store, setup
  leftovers, Windows.old, ESD files): the app clears every `StateFlags0777` value under
  `VolumeCaches`, sets it only on the ticked categories this PC has, runs
  `cleanmgr /sagerun:777` once at the end of Clean, and clears the values again in a
  `finally`. The runner sits behind an interface and is tested with a fake registry.
  Windows.old and ESD files are off by default (rollback and Reset this PC need them).
- **Old driver versions** (off by default): listed with the DISM API through the
  `Microsoft.Dism` NuGet package (MIT), because `pnputil /enum-drivers` prints English-only
  labels and would find nothing on other languages. Grouped by original INF plus provider; the
  newest version and any tie stay; packages a device uses now are skipped; deleted with
  `pnputil /delete-driver` and never `/force`, so Windows refuses anything still in use
  ("Windows kept it").
- **Opt-in, in "Your call"**: the DirectX shader cache (leaves the protected list) and the
  full blue-screen dump `C:\Windows\MEMORY.DMP` (admin). Minidumps stay protected.
- **Left out**, as the spec says: Disk Cleanup's Downloads and "User file versions"
  categories, both listed under Kept on purpose.
- **Admin rules** are blocked with "needs admin" when the app is not elevated, at scan time
  and again at clean time, so a clean never runs half a Windows rule.

## The look

The first build looked flat on a real PC. The window now uses `MicaBackdrop` with
`Kind = MicaKind.BaseAlt` (a stronger tint than the default, so cards stand out), the root grid
and the title bar are explicitly transparent so the backdrop shows through, and the cards use
the theme's `CardBackgroundFillColorDefaultBrush` and `CardStrokeColorDefaultBrush`
(the `Expander` cards use them by default; the details pane sets them in `CardStyle`). Light
and dark follow Windows, the accent colour comes from the system, and there is no Acrylic.

## Where the C# port differs from the reference

Every difference makes the app more careful, not less:

- **Temp folders**: the reference judged a folder in `%LOCALAPPDATA%\Temp` (and Windows Temp)
  by the folder's own timestamp. The port also requires that nothing inside it changed for 7
  days, so a long-running program's temp folder is left alone. The same walk gives the size,
  so the scan is no slower.
- **Codex CLI releases**: the reference treated anything other than `current`'s target as
  old. The port only does this when `current` really is a link; otherwise it cannot tell the
  live release apart and leaves them all.
- **Guard**: any reparse point between the allowed root and the target is refused, not only
  junctions and symlinks (a cloud or other special folder gets its own reason). 8.3 short
  names (`RUNNER~1`) are expanded before comparing. `protect.txt`, Pictures, Music, Videos,
  Saved Games, `Documents\My Games`, AMD's shader caches, the app's own folders and the
  folder it runs from are protected. A few more containers are never deleted as a whole
  (`AppData\LocalLow`, `.cache`, `.local`, `.config`, `LocalAppData\Packages`,
  `LocalAppData\Microsoft`, `Roaming\Microsoft`, `Program Files`).
- **Clean**: the guard is rebuilt right before cleaning (so something installed since the scan
  still counts), each path is checked again just before it is deleted, and a rule whose app
  was opened since the scan is skipped.
- **Delete**: written out by hand instead of `shutil.rmtree`: links are deleted, never
  followed; cloud placeholders (`RecallOnDataAccess`, `RecallOnOpen`, `Offline`) are never
  deleted or downloaded; other special reparse points are left in place.
- **Apps that are open**: the app still finds and sizes their caches so the row can say
  "Chrome is open, close it to clear this" next to a real size, but cannot select them.
- **External tools** (`nvidia-smi`, PowerShell, DISM) are started by full path under
  `C:\Windows`, never from PATH.
- **Log location**: `%LOCALAPPDATA%\SafeDiskCleaner\runs.csv` (the reference wrote next to the
  script).

## Versions

- **.NET 10 (LTS)**. The first build used .NET 8 as the spec asked, but .NET 8 support ends on
  10 November 2026, so the owner chose to move to .NET 10 before any release. The app is
  self-contained, so friends still need nothing installed.
- **Windows App SDK 2.6.5**, the current stable release on NuGet when this was built. The first
  build used 1.8.260921001 (the spec said "latest stable 1.x"); the owner chose to move to the
  2.x line. The app's code compiled unchanged against 2.6.5. Note that 2.6.5 was published the
  same day this was built; if it misbehaves on a real PC, 2.6.4 is the previous release.
- **Download size**: the zip is about 88 MB (about 215 MB unzipped), almost all of it the
  bundled .NET and Windows App SDK runtimes, which is what lets it run on a PC with nothing
  installed. The self-contained Windows App SDK copies its whole runtime, including Windows
  ML (`onnxruntime.dll`, `DirectML.dll`, about 40 MB) that this app never uses. Referencing
  only the WinUI, Foundation and related component packages was tried and did not drop
  them, so the app uses the standard metapackage. Trimming would need hand-deleting runtime
  files, which is not worth the risk of a broken install.

## Not verified

The engine was built and tested on Windows in CI (`windows-latest`), and the published app is
started once there as a smoke test. The window itself, Mica, dark mode, high DPI, the folder
picker, "Restart as admin" and an actual clean on a real, lived-in PC can only be checked on
a real Windows desktop.
