# Safe Disk Cleaner

A small Windows app that does what Windows Disk Cleanup does, plus the app and browser
caches Disk Cleanup never sees, without ever touching your installed programs, AI models or
your own files. You see exactly what will go before anything is deleted.

<!-- Screenshot: replace the line below with ![Safe Disk Cleaner](docs/screenshot.png) once added. -->
*Screenshot coming soon.*

## What it clears

**Leftovers**, ticked by default:

- Copies of NVIDIA drivers that are already installed (from the NVIDIA app, or the
  `C:\NVIDIA` folder a manual install leaves behind). Newer, not-yet-installed drivers stay.
- Update installers apps downloaded and no longer need, and old app versions that Discord
  and similar apps keep after updating.
- Temp files nobody has touched for a week, app crash dumps, old error reports, and
  unfinished downloads (`.crdownload`, `.part`) older than two days.
- Explorer's thumbnail cache and Windows' own internet cache.
- Download caches of npm, pip, uv and Yarn, and the `node_modules` folders of projects
  nobody has opened for 30 days (`npm install` brings them back).

**App and browser caches**, ticked by default: the web and graphics caches of Chrome, Edge,
Brave, Opera, Firefox, Discord, Teams, Slack, Spotify, VS Code and similar apps, plus the
Steam, Epic and Ubisoft launcher caches. Your logins, settings, history and downloads stay.
If an app is open it is skipped, with a note saying so.

**Your call**, not ticked: emptying the Recycle Bin, the DirectX shader cache (games stutter
for a while after), and the big blue-screen memory dump (`MEMORY.DMP`, admin).

**Windows**, only when run as admin, the same things Disk Cleanup's "Clean up system files"
mode clears: Windows' temp folder, old Windows Update downloads, error reports, the Delivery
Optimization cache, old Windows log files, diagnostic leftovers (Feedback Hub archives, old
setup logs, Defender's non-critical files, ChkDsk fragments), and Windows Update cleanup,
which Windows' own Disk Cleanup does for the app and can take 10+ minutes. Not ticked unless
you choose: old driver versions, your previous Windows installation (Windows.old) and the
Windows installation files that Reset this PC uses.

## What it never touches

Installed programs and command-line tools, anything on your PATH, local AI models (Ollama,
LM Studio, Hugging Face), NVIDIA and AMD game shader caches, Windows minidumps, OneDrive and
other synced folders, your Downloads folder and File History backups (Disk Cleanup offers
both; this app never touches them), your Documents, Pictures, Music, Videos and game saves,
and browser logins and history. The list
at the bottom of the app, "Kept on purpose", shows this too.

Why so strict? This tool started after a manual cleanup deleted
`%LOCALAPPDATA%\Microsoft\WinGet\Packages` because it looked like a download cache. It is
actually where WinGet installs tools, so ffmpeg, yt-dlp and several others stopped working.

So before anything is deleted, a safety check makes sure the folder is not a protected
place, does not hold an installed program or a PATH folder, and is not reached through a
shortcut-like link. It runs during the scan and again right before each delete.

## Download and run

1. Open the [Releases](../../releases) page and download `SafeDiskCleaner-win-x64.zip`.
2. Unzip it anywhere you like, for example into your Documents or Desktop.
3. Open the `SafeDiskCleaner` folder and double-click `SafeDiskCleaner.exe`.

There is nothing to install, and it works on Windows 10 and 11 without anything else.

**The blue "Windows protected your PC" box.** The app is not code-signed (signing costs
money every year), so Windows SmartScreen warns about it the first time. Click
**More info**, then **Run anyway**. If you'd rather check first, the source code is all
here.

The app scans when it opens (a few seconds). Tick or untick what you want, click a row to
see every folder it covers, then click **Clean selected**. It asks once more before
deleting, then tells you how much space was freed, measured on the drive itself.

## Do I need admin?

No. Everything works without admin except the **Windows** group, which needs admin to read
Windows' own folders. If you want that too, click **Restart as admin** at the top and say
yes to the Windows prompt.

## Keeping a folder safe

Click **Protect a folder** and pick a folder. It, everything inside it, and any folder that
contains it are never deleted.

The list lives in a plain text file you can edit yourself:
`%APPDATA%\SafeDiskCleaner\protect.txt`. Put one folder per line. Lines starting with `#`
are ignored, and things like `%USERPROFILE%` work. You can open it from the `...` menu at
the bottom right.

```text
# folders Safe Disk Cleaner must leave alone
D:\Games\Mods
%LOCALAPPDATA%\SomeApp\Cache
```

## Where is the log?

Every clean is logged to `%LOCALAPPDATA%\SafeDiskCleaner\runs.csv`: the time, the rule,
each path, its size and what happened ("deleted", "refused: ...", "partly in use"), plus a
total row with the space freed. It opens in Excel or Notepad. The result bar after a clean
has an **Open the log** link, and so does the `...` menu. If something goes wrong, details
go to `error.log` in the same folder.

## Uninstall

1. Delete the `SafeDiskCleaner` folder you unzipped.
2. Delete `%APPDATA%\SafeDiskCleaner` (your protect list).
3. If you want the log gone too, delete `%LOCALAPPDATA%\SafeDiskCleaner`.

Nothing else is left behind: no registry entries, no services, no scheduled tasks.

## No warranty

Safe Disk Cleaner is free and open source under the [MIT licence](LICENSE). It is careful
by design, but it comes with no warranty. Look over the list before you click
**Clean selected**, especially anything you don't recognise.

---

## For developers

Requirements: Windows 10 or 11, the .NET 8 SDK, and Visual Studio 2022 (with the "WinUI
application development" workload) if you want the designer. The Windows App SDK comes from
NuGet.

```bash
dotnet test tests/SafeDiskCleaner.Core.Tests
```

```bash
dotnet publish src/SafeDiskCleaner/SafeDiskCleaner.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o publish/SafeDiskCleaner
```

| Path | What |
|---|---|
| `src/SafeDiskCleaner.Core` | Rules, the safety guard, scan, delete and log. No UI. |
| `src/SafeDiskCleaner` | The WinUI 3 app (unpackaged, self-contained). |
| `tests/SafeDiskCleaner.Core.Tests` | xUnit tests for the guard and the delete routine. |
| `reference/engine.py` | The original Python engine the C# was ported from. |
| `docs/SPEC.md` | The build spec. |
| `docs/DECISIONS.md` | Which rules were added or rejected, and why. |

Every push builds, tests, publishes and smoke-tests the app on GitHub Actions
(`.github/workflows/build.yml`) and uploads `SafeDiskCleaner-win-x64.zip` as an artifact.
Pushing a `v*` tag runs `release.yml`, which attaches the same zip to a **draft** release
for the owner to review and publish.
