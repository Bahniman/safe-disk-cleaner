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
| Your call | off | Recycle Bin (`SHQueryRecycleBinW`, `SHEmptyRecycleBinW`) |
| Windows | on, needs admin | Windows Temp 7+ days, `SoftwareDistribution\Download` 10+ days, WER report folders, Delivery Optimization cache (`Delete-DeliveryOptimizationCache -Force`); DISM `/StartComponentCleanup` off by default |
| Kept on purpose | shown, never cleaned | the `KEPT` list, so people see what is deliberately left alone |

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
example: Windows.old needs TrustedInstaller ownership, so point to Windows
Settings instead; Steam partial downloads may be resumed; DirectX shader caches
make games stutter). Small, cheap features worth considering:

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
- Header: app name, a one-line promise ("Clears caches and leftovers. Installed
  tools, models and your files are never touched."), and drive usage for the
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
