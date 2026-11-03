# Safe Disk Cleaner: build spec

A Windows 10/11 desktop app, written in WinUI 3, that finds stale caches and
leftovers and clears them without ever touching installed tools, models or
personal files. It is meant for the owner and his friends: someone downloads a
zip from GitHub Releases, runs it, sees exactly what will go, and clicks Clean.

`reference/engine.py` is a working Python version of the engine, tested on one
Windows 11 PC on 3 Nov 2026 (it found and cleared about 1.5 GB on a machine
cleaned by hand earlier that day). Port its rules and its guard to C#. Read it
first; it is the source of truth for behaviour. Where this spec and the
reference disagree, this spec wins.

## Why safety is the whole product

During the manual cleanup this tool came from, `%LOCALAPPDATA%\Microsoft\WinGet\Packages`
was deleted because it looked like a download cache. It is where WinGet
installs portable tools. ffmpeg, yt-dlp, ripgrep, jq, Deno, uv and Poppler all
broke. The guard exists so that class of mistake cannot happen: a folder that
holds an installed program or a PATH entry is never deleted, whatever a rule
says.

## Repository layout

```
src/SafeDiskCleaner.Core/        net8.0-windows class library: rules, guard, scan, clean, log. No UI.
src/SafeDiskCleaner/             WinUI 3 app (Windows App SDK, unpackaged, self-contained)
tests/SafeDiskCleaner.Core.Tests xUnit tests for the guard and the delete routine
.github/workflows/build.yml      windows-latest: restore, build, test, publish, upload zip artifact
.github/workflows/release.yml    on tag v*: same build, attach zip to a GitHub Release
reference/engine.py              keep as documentation of the original engine
README.md, LICENSE (MIT), docs/
```

Use .NET 8 LTS and the latest stable `Microsoft.WindowsAppSDK` 1.x from NuGet
(check the current version; do not guess). Unpackaged:
`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`, `SelfContained=true`,
`RuntimeIdentifier=win-x64`, so the zip runs on a clean PC with nothing
installed.

## Engine (Core)

Port these from `engine.py`, keeping names close so the two can be compared:

- **Guard** (`Guard.Check(path)` returns null or a refusal reason). Same order
  as the reference: missing, top-level folder (`NEVER_EXACT`), outside
  `ALLOWED_ROOTS`, any junction or symlink between the allowed root and the
  target, protected path (target inside it or containing it), and any folder
  that contains a registry `InstallLocation` (HKCU, HKLM, WOW6432Node), a PATH
  directory (process, user and machine PATH), or the target folder of a link in
  `%LOCALAPPDATA%\Microsoft\WinGet\Links`. Check at scan time and again right
  before each delete.
- **User-protected paths**: also read `%APPDATA%\SafeDiskCleaner\protect.txt`
  (one path per line, `#` comments, environment variables expanded). The app
  has a "Protect a folder" button that appends to it. Create the file with a
  comment header on first run.
- **Delete**: write the recursive delete yourself. For every entry, if it is a
  reparse point that is a junction or symlink, delete the link only and never
  descend. Clear read-only attributes before deleting. Never delete cloud
  placeholder files (attributes `RecallOnDataAccess`, `RecallOnOpen` or
  `Offline`): skip them and report the folder as partly kept.
- **Sizes**: enumerate with `AttributesToSkip = ReparsePoint` and
  `IgnoreInaccessible = true`.
- **Running apps**: process names and image paths
  (`QueryFullProcessImageName` with `PROCESS_QUERY_LIMITED_INFORMATION`, since
  `Process.MainModule` throws for many processes). A rule tied to an app skips
  itself while that app runs and says so.
- **Freed space** is measured as the change in the drive's free bytes, never
  summed from estimates.
- **Log** every run to `%LOCALAPPDATA%\SafeDiskCleaner\runs.csv` (time, rule,
  path, bytes, result) plus a total row.

### Rules (port all from `build_rules()`)

| Group | Default | Rules |
|---|---|---|
| Leftovers | on | NVIDIA driver copies (only versions at or below the installed driver from `nvidia-smi`), app `*-updater` installers, old Squirrel `app-*` versions and `.nupkg`s (Discord and similar), old Claude Code and Codex runtimes, npm/pip/uv/yarn caches, Comfy Desktop download cache, temp idle 7+ days, crash dumps, partial downloads 2+ days old, `node_modules` in projects idle 30+ days |
| App and browser caches | on | Chromium/Electron `Cache`, `Code Cache`, `GPUCache`, Dawn caches, `Service Worker\CacheStorage` and `ScriptCache`, recognised by what is inside them (see `_is_chromium_cache`), for the apps in `APPS`; launcher caches in `LAUNCHERS`. Never `OptGuideOnDeviceModel` |
| Leftovers (added) | on | Explorer thumbnail cache (`thumbcache_*.db`), Windows' internet cache (`INetCache\IE` and `Low\IE` buckets), user WER reports idle 7+ days |
| Your call | off | Recycle Bin (`SHQueryRecycleBinW`, `SHEmptyRecycleBinW`), DirectX shader cache (`%LOCALAPPDATA%\D3DSCache`), blue-screen memory dump (`%WINDIR%\*.dmp`, admin) |
| Windows | on, needs admin | Windows Temp 7+ days, `SoftwareDistribution\Download` 10+ days, WER report folders, Delivery Optimization cache (`Delete-DeliveryOptimizationCache -Force`), old Windows logs, Windows diagnostic leftovers, Windows Update cleanup through Disk Cleanup's own handlers. Off by default: old driver versions, Windows.old, Windows ESD files. See "Disk Cleanup parity" |
| Kept on purpose | shown, never cleaned | the `KEPT` list, so people see what is deliberately left alone, including the Downloads folder and File History versions that Disk Cleanup offers |

## Disk Cleanup parity

The owner asked for the app to be better than running Windows Disk Cleanup as
admin: do everything it does, and keep clearing the app caches it never sees.
Port `reference/engine.py` as it is now; it implements all of this.

Disk Cleanup's categories live under
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches`, one
subkey each. The app covers every category a stock Windows 11 PC lists, in
one of three ways.

1. **Its own rules, with the guard and real sizes**, for file-based
   categories:
   - Temporary Files, which also covers `Windows\Logs`, `System32\LogFiles`,
     `SystemTemp`, `ProgramData\Microsoft\WDF` and `HiveUpload`. Delete files
     idle 7+ days and leave every folder in place. Skip the `MeasuredBoot`
     and `SystemRestore` subfolders.
   - Thumbnail Cache and Internet Cache Files.
   - Windows Error Reporting Files.
   - Delivery Optimization Files and Recycle Bin.
   - Windows Defender (`LocalCopy` and `Support`, files idle 7+ days; never
     `Quarantine`).
   - Feedback Hub Archive log files, Diagnostic Data Viewer database files and
     RetailDemo Offline Content.
   - Setup Log Files (`setup*.log` and the other names in `C:\Windows`, idle
     30+ days) and Old ChkDsk Files (`C:\FOUND.000` to `.009`, `*.CHK` only).
   - D3D Shader Cache (opt-in) and System error memory dump files (opt-in).
2. **Windows' own handler**, for what only Windows can remove safely:
   - Update Cleanup (the component store), Windows Upgrade Log Files,
     Temporary Setup Files, Upgrade Discarded Files, Windows Reset Log Files,
     Language Pack, Downloaded Program Files, Temporary Sync Files, Offline
     Pages Files, Active Setup Temp Folders and Content Indexer Cleaner. All
     of these are on by default.
   - Previous Installations (Windows.old, off by default: it ends the 10-day
     rollback) and Windows ESD installation files (off by default: Reset this
     PC needs them). Show each row only when its folder exists.

   How to run them:
   1. Delete any `StateFlags0777` value under every VolumeCaches subkey, so a
      crashed earlier run cannot widen this one.
   2. Set `StateFlags0777` (DWORD 2) only on the ticked categories that exist.
   3. Run `%WINDIR%\System32\cleanmgr.exe /sagerun:777` and wait. Allow up to
      two hours.
   4. Delete the values again in a `finally`.

   Use `RegistryView.Registry64`. All ticked handler categories run in one
   cleanmgr pass at the end of Clean. Their size is unknown until the run,
   so show "Size shows after the run", and enable the Clean button whenever
   any row is ticked, even at 0 bytes.
3. **Deliberately left out**:
   - DownloadsFolder (it empties the user's Downloads) and User file
     versions (File History backups). List both under Kept on purpose.
   - System error minidump files, which stay protected.

**Old driver versions** match Disk Cleanup's "Device driver packages". The
rule is off by default because a removed version can't be rolled back to.

1. List third-party packages language-neutrally with the DISM API
   (`DismGetDrivers`, for example through the `Microsoft.Dism` NuGet package;
   check its current version). The reference parses `pnputil /enum-drivers`,
   whose labels are English only.
2. Group the packages by original INF name plus provider. Keep the newest
   version, and any package that ties with it.
3. Skip any package a device uses now. `InfPath` under each
   `HKLM\SYSTEM\CurrentControlSet\Control\Class\{guid}\NNNN` subkey gives
   the INFs in use.
4. Size each package from `DriverStore\FileRepository\<Active>`, where
   `Active` comes from `HKLM\DRIVERS\DriverDatabase\DriverInfFiles\oemNN.inf`.
   That is the live database; fall back to `HKLM\SYSTEM\DriverDatabase`,
   which is a stale copy.
5. Delete with `pnputil /delete-driver oemNN.inf` and **no** `/force`, so
   Windows itself refuses a package that is still in use. Report that as
   "Windows kept it".

On the reference PC the rule found 50 older packages, 739 MB in total, mostly
superseded Wi-Fi driver versions. None of them was in use.

**Guard changes.**
- Add the folders above to `ALLOWED_ROOTS`. Each one is automatically in
  `NEVER_EXACT`, so a root itself is never deleted.
- Loose files in `C:\Windows` pass only through `ALLOWED_FILES`: a name
  pattern matched against the file name, for a file directly in that folder.
  Never use a glob on the full path: `C:\Windows\*.dmp` would match
  `C:\Windows\Minidump\x.dmp`.
- `D3DSCache` leaves `PROTECTED`. The NVIDIA shader caches stay.

**Tests to add.**
- `ALLOWED_FILES` allows `C:\Windows\MEMORY.DMP` and `C:\Windows\setupact.log`.
  It refuses `C:\Windows\Minidump\x.dmp`, `C:\Windows\System32\x.dmp`,
  `C:\Windows\Logs\setup.log` and `C:\Windows\explorer.exe`.
- The guard refuses `C:\Windows\Logs` itself, `C:\Windows\System32`, and
  `C:\Windows\System32\LogFiles\..\config`.
- The handler runner clears stale flags first. Test it behind an interface
  with a fake registry.
- Driver grouping keeps the newest version and every tie, and skips in-use
  INFs. Use a canned package list.

**UI.** Under the title, add "Covers what Windows Disk Cleanup does, plus app
caches it never sees." The Windows group's subtitle reads "What Disk Cleanup's
system-file mode clears." The Clean dialog adds "Windows' own Disk Cleanup runs
too and can take 10+ minutes" when a handler row is ticked.

Add, if cheap and clearly safe: Brave, Opera, Slack, Teams (new), Spotify,
Cursor and Windsurf cache roots, using the same signature check. Do not add
chat-history deletion: it is personal data, and Claude Code already expires
its own transcripts.

## Value: small, safe, useful

The owner's words: make sure it is safe, it is good, it actually works, it is
actually valuable for others, and do not make it too complex.

Before adding anything beyond the reference rules, spend a short research pass
online: what BleachBit, Windows Storage Sense / Disk Cleanup and Microsoft PC
Manager clean, and which cache locations most often eat space on ordinary
student and gaming PCs (browsers, Discord, Teams, Spotify, Steam, NVIDIA/AMD
driver leftovers, Windows Update). Add a rule only if it passes all four:

1. common on ordinary PCs, not just developer machines,
2. usually big enough to matter (roughly 100 MB or more),
3. provably regenerable: the owning app or Windows recreates it, with a source
   you can cite in a code comment,
4. explainable in one line a non-technical friend understands.

Record every candidate you rejected and why in `docs/DECISIONS.md` (for
example: Steam partial downloads may be resumed; registry cleaning has no
measurable benefit). Windows.old and the DirectX shader cache are no longer
rejections: see "Disk Cleanup parity". Small, cheap features worth considering:

- an "Open Windows storage settings" link (`ms-settings:storagesense`) for what
  Windows cleans better itself (Windows.old, previous updates),
- AMD driver installer leftovers (`C:\AMD`) alongside NVIDIA, gated on the
  installed version the same way,
- a "Last cleaned 12 days ago, freed 1.5 GB" line from the log.

Say no to: registry cleaning, "optimisers", startup managers, a disk-usage tree
map, scheduling, auto-updates and telemetry. One window, one scan, one Clean
button.

## App (WinUI 3)

It should look like a first-party Windows 11 app, not a port.

- Mica backdrop, extended custom title bar with app icon and title, light and
  dark following the system theme. Fluent defaults; accent colour from the
  system.
- Header: app name, a two-line promise ("Covers what Windows Disk Cleanup does,
  plus app caches it never sees." / "Installed tools, models and your files are
  never touched."), and drive usage for the
  system drive as a `ProgressBar` plus "416.9 GB free of 932.5 GB". A drive
  picker is optional.
- An `InfoBar` shows admin state: informational when standard, with a "Restart
  as admin" button (`ProcessStartInfo` with `Verb = "runas"`,
  `UseShellExecute = true`). The manifest stays `asInvoker`.
- Body: one card per group (an `Expander` or a styled `ListView` group), each
  row a `CheckBox` with title, a muted one-line description (for app caches,
  "7 cache folders"), and a right-aligned size. A rule skipped because its app
  is open shows greyed with the reason ("Chrome is open, close it to clear
  this"). Group headers show the group total and a tri-state checkbox.
- Selecting a row opens a details pane on the right (or below on narrow
  windows) listing every folder with its size, and any skipped paths with the
  refusal reason from the guard.
- Footer `CommandBar`: Rescan, Protect a folder, and an accent button "Clean
  selected · 1.5 GB".
- Clean asks first in a `ContentDialog` ("Delete 214 items, about 1.5 GB? This
  can't be undone."), then shows progress, then an `InfoBar` with "Freed 1.5 GB
  (measured on the drive)" and a link that opens the log.
- Scanning runs off the UI thread with a `ProgressBar`. A scan takes about 5
  seconds on the reference PC; keep it there.
- Segoe Fluent Icons for group glyphs, `AutomationProperties.Name` on
  interactive controls, full keyboard use, minimum window 900x600, sensible at
  1280x800 and on a 4K screen.
- App icon: a simple original glyph (a broom or a disk with a check) as an
  `.ico`. No third-party logos.

## Tests (must run in CI)

Port these guard cases using temp folders the test creates, not real user
folders: refuse a folder containing a registered install location (use a fake
PATH entry by setting the process PATH in the test), refuse anything inside a
protected path, refuse a path reached through a junction (create one with
`cmd /c mklink /J`), refuse top-level folders, refuse outside the allowed
roots, allow a plain cache folder. Delete test: a folder containing a junction
to a sentinel folder is deleted and the sentinel file survives. A cloud
placeholder test is optional.

## Build and CI

You are in a Linux cloud container and cannot build WinUI there. Build and test
through GitHub Actions on `windows-latest`: push, read the run with
`gh run watch` / `gh run view --log-failed`, fix, repeat until green. Publish a
self-contained win-x64 folder, zip it as `SafeDiskCleaner-win-x64.zip`, and
upload it as an artifact on every run. `release.yml` attaches the same zip to a
Release when a `v*` tag is pushed.

## README (for friends)

Plain language: what it clears, what it never touches (with the WinGet story in
two sentences), how to download from Releases, the SmartScreen note (unsigned:
More info, Run anyway), why admin is optional, how to protect a folder, where
the log is, how to uninstall (delete the folder and `%APPDATA%\SafeDiskCleaner`).
Leave a placeholder for a screenshot; the owner will add one from his PC.
MIT licence and a short "no warranty, review the list before you click Clean"
note.

## Boundaries

- The repository is private. Do not change its visibility, and do not create a
  public release. The owner decides that after trying the build.
- No personal data in code, docs or commit messages: no usernames, home paths
  of a real person, or details of anyone's machine. Use environment variables
  and generic examples.
- Work on a branch and open a pull request to `main` when CI is green.

## Done means

CI green on the PR (build, tests, publish), the zip artifact downloads and
contains `SafeDiskCleaner.exe`, README complete, and a PR description listing
what was ported, what was added, and anything not verified (the UI cannot be
seen from the cloud; the owner will run it locally).
