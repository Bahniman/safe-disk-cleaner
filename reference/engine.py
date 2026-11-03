"""Reference engine for Safe Disk Cleaner (Python, tested on one Windows 11 PC).

Covers what Windows Disk Cleanup does (its system-file mode included), plus
the app caches it never sees.

The WinUI app in src/ ports these rules and the guard to C#. CLI:
    python cleaner.py scan [--json]
    python cleaner.py clean [--tier safe|ask|admin|all] [--rules id,id] [--dry-run]
    python cleaner.py ui [--snapshot out.png]

Every delete goes through guard(). It refuses protected paths, anything reached
through a junction or symlink, and any folder that holds an installed program
or a PATH entry (deleting the WinGet Packages folder once broke
eight installed command-line tools).
Stdlib only; Pillow is used for --snapshot when present.
"""
import csv
import ctypes
import fnmatch
import glob
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import threading
import time
import traceback
import winreg
from concurrent.futures import ThreadPoolExecutor
from ctypes import wintypes

HERE = os.path.dirname(os.path.abspath(__file__))
LOG_CSV = os.path.join(HERE, "runs.csv")
ERR_LOG = os.path.join(HERE, "error.log")

HOME = os.path.expanduser("~")
LOCAL = os.environ.get("LOCALAPPDATA", os.path.join(HOME, "AppData", "Local"))
ROAMING = os.environ.get("APPDATA", os.path.join(HOME, "AppData", "Roaming"))
PD = os.environ.get("PROGRAMDATA", r"C:\ProgramData")
WIN = os.environ.get("SystemRoot", r"C:\Windows")
TEMP = os.path.join(LOCAL, "Temp")
DAY = 86400
NOWIN = 0x08000000  # CREATE_NO_WINDOW, so pythonw never flashes a console


def nc(p):
    return os.path.normcase(os.path.abspath(p))


def under(p, root):
    p, root = nc(p), nc(root)
    return p == root or p.startswith(root.rstrip("\\") + "\\")


def is_link(p):
    try:
        return os.path.islink(p) or os.path.isjunction(p)
    except OSError:
        return False


def tree_size(p):
    try:
        st = os.lstat(p)
    except OSError:
        return 0
    if not stat.S_ISDIR(st.st_mode):
        return st.st_size
    total, stack = 0, [p]
    while stack:
        d = stack.pop()
        try:
            with os.scandir(d) as it:
                for e in it:
                    try:
                        if e.is_symlink() or e.is_junction():
                            continue
                        if e.is_dir(follow_symlinks=False):
                            stack.append(e.path)
                        else:
                            total += e.stat(follow_symlinks=False).st_size
                    except OSError:
                        pass
        except OSError:
            pass
    return total


def mtime(p):
    try:
        return os.lstat(p).st_mtime
    except OSError:
        return time.time()


def idle_days(p, deep=False):
    """Days since p (or, with deep, anything inside it) last changed."""
    newest = mtime(p)
    if deep and os.path.isdir(p):
        for root, dirs, files in os.walk(p):
            dirs[:] = [d for d in dirs if not is_link(os.path.join(root, d))]
            for f in files:
                newest = max(newest, mtime(os.path.join(root, f)))
    return (time.time() - newest) / DAY


def vkey(name):
    return tuple(int(x) for x in re.findall(r"\d+", name)) or (0,)


def fmt(n):
    if n is None:
        return "?"
    if n >= 1 << 30:
        return f"{n / (1 << 30):.1f} GB"
    if n >= 1 << 20:
        return f"{n / (1 << 20):.0f} MB"
    return f"{n / 1024:.0f} KB"


def is_admin():
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def free_bytes(drive="C:\\"):
    return shutil.disk_usage(drive).free


# ---------------------------------------------------------------- processes

def running_images():
    """Full image paths of running processes, lower-cased."""
    psapi, k32 = ctypes.WinDLL("psapi"), ctypes.WinDLL("kernel32")
    k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    k32.OpenProcess.restype = wintypes.HANDLE
    k32.CloseHandle.argtypes = [wintypes.HANDLE]
    k32.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD,
                                               wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    arr = (wintypes.DWORD * 8192)()
    needed = wintypes.DWORD()
    psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(needed))
    out = set()
    for pid in arr[: needed.value // 4]:
        h = k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not h:
            continue
        buf, size = ctypes.create_unicode_buffer(1024), wintypes.DWORD(1024)
        if k32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
            out.add(buf.value.lower())
        k32.CloseHandle(h)
    return out


# ---------------------------------------------------------------- the guard

PROTECTED_PATTERNS = [
    # local AI models and runtimes
    r"{L}\Claude-Data\*\vm_bundles", r"{H}\.ollama", r"{H}\.cache\huggingface", r"{H}\.lmstudio",
    r"{L}\ms-playwright", r"{R}\Python",
    # agent tools' own state
    r"{H}\.claude\skills", r"{H}\.claude\hooks", r"{H}\.claude\projects\*\memory",
    r"{H}\.codex\skills", r"{H}\.codex\plugins", r"{H}\Documents\Codex",
    # synced and personal folders
    r"{H}\CrossDevice", r"{H}\OneDrive", r"{H}\OneDrive *", r"{H}\.ssh", r"{H}\.gnupg",
    # installed tools and installer databases
    r"{L}\Microsoft\WinGet", r"{L}\Programs", r"{L}\Package Cache", r"{P}\Package Cache", r"{W}\Installer",
    # crash dumps, NVIDIA shader caches, Chrome's on-device AI model. The DirectX
    # shader cache is an opt-in rule instead, as in Disk Cleanup.
    r"{W}\Minidump", r"{W}\LiveKernelReports", r"{L}\NVIDIA", r"{H}\AppData\LocalLow\NVIDIA",
    r"{L}\Google\Chrome\User Data\OptGuideOnDeviceModel",
]

FOUND_DIRS = [rf"C:\FOUND.{i:03d}" for i in range(10)]  # old ChkDsk fragments

ALLOWED_ROOTS = [HOME, os.path.join(PD, "NVIDIA Corporation"), r"C:\Temp",
                 os.path.join(WIN, "Temp"), os.path.join(WIN, "SoftwareDistribution", "Download"),
                 os.path.join(PD, "Microsoft", "Windows", "WER"),
                 # folders Windows Disk Cleanup also clears
                 os.path.join(WIN, "Logs"), os.path.join(WIN, "System32", "LogFiles"), os.path.join(WIN, "SystemTemp"),
                 os.path.join(PD, "Microsoft", "WDF"), os.path.join(PD, "Microsoft", "Windows", "HiveUpload"),
                 os.path.join(PD, "Microsoft", "Windows Defender", "LocalCopy"),
                 os.path.join(PD, "Microsoft", "Windows Defender", "Support"),
                 os.path.join(PD, "Microsoft", "Diagnosis", "FeedbackArchive"),
                 os.path.join(PD, "Microsoft", "Diagnosis", "EventTranscript"),
                 os.path.join(PD, "Microsoft", "Windows", "RetailDemo")] + FOUND_DIRS

# loose files directly inside the Windows folder that Disk Cleanup also clears, never in its subfolders
ALLOWED_FILES = [(WIN, ("*.dmp", "setup*.log", "setup*.old", "setuplog.txt", "winnt32.log"))]

NEVER_EXACT = [HOME, LOCAL, ROAMING, TEMP, os.path.join(HOME, "AppData"), os.path.join(HOME, "Downloads"),
               os.path.join(HOME, "Documents"), os.path.join(HOME, "Desktop"), os.path.join(HOME, "source"),
               PD, WIN, r"C:\Temp", r"C:\\"] + ALLOWED_ROOTS


def _expand(pat):
    return pat.format(H=HOME, L=LOCAL, R=ROAMING, P=PD, W=WIN)


def _allowed_file(path):
    """True for a file name ALLOWED_FILES lists, sitting directly in its folder."""
    d, name = os.path.split(nc(path))
    return any(d == nc(base) and any(fnmatch.fnmatch(name, p) for p in pats) for base, pats in ALLOWED_FILES)


def _reg_values(hive, key, name):
    out = []
    try:
        with winreg.OpenKey(hive, key) as k:
            out.append(winreg.QueryValueEx(k, name)[0])
    except OSError:
        pass
    return out


def _install_locations():
    locs = []
    for hive, key in ((winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                      (winreg.HKEY_LOCAL_MACHINE, r"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                      (winreg.HKEY_LOCAL_MACHINE, r"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")):
        try:
            with winreg.OpenKey(hive, key) as k:
                for i in range(winreg.QueryInfoKey(k)[0]):
                    try:
                        sub = winreg.EnumKey(k, i)
                        loc = _reg_values(hive, key + "\\" + sub, "InstallLocation")
                        if loc and loc[0] and len(loc[0].strip('"\\ ')) > 3:
                            locs.append(os.path.expandvars(loc[0].strip('" ')))
                    except OSError:
                        pass
        except OSError:
            pass
    return locs


def _path_dirs():
    raw = os.environ.get("PATH", "")
    raw += ";" + ";".join(_reg_values(winreg.HKEY_CURRENT_USER, "Environment", "Path"))
    raw += ";" + ";".join(_reg_values(winreg.HKEY_LOCAL_MACHINE,
                                      r"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "Path"))
    return [os.path.expandvars(p.strip('" ')) for p in raw.split(";") if len(p.strip('" ')) > 3]


def _link_targets(folder):
    out = []
    for f in glob.glob(os.path.join(folder, "*")):
        try:
            if os.path.islink(f):
                out.append(os.path.dirname(os.path.realpath(f)))
        except OSError:
            pass
    return out


class Guard:
    def __init__(self):
        self.protected = [p for pat in PROTECTED_PATTERNS for p in glob.glob(_expand(pat))]
        self.protected += [_expand(pat) for pat in PROTECTED_PATTERNS if "*" not in pat]
        # folders an installed program or a PATH entry lives in
        self.in_use = _install_locations() + _path_dirs() + _link_targets(os.path.join(LOCAL, "Microsoft", "WinGet", "Links"))
        self.in_use = [p for p in self.in_use if not any(nc(p) == nc(r) for r in NEVER_EXACT)]

    def check(self, path):
        """Return None when path may be deleted, else the reason it may not."""
        if not os.path.lexists(path):
            return "gone already"
        if any(nc(path) == nc(r) for r in NEVER_EXACT):
            return "top-level folder"
        root = next((r for r in ALLOWED_ROOTS if under(path, r)), None)
        if root is None and os.path.isfile(path) and _allowed_file(path):
            root = os.path.dirname(path)
        if root is None:
            return "outside the folders this tool may touch"
        # no junction or symlink anywhere between the allowed root and the target
        cur = nc(path)
        while nc(cur) != nc(root):
            if is_link(cur):
                return "reached through a junction or link"
            cur = os.path.dirname(cur)
        for p in self.protected:
            if under(path, p) or under(p, path):
                return f"protected ({p})"
        for p in self.in_use:
            if under(p, path):
                return f"holds an installed program or PATH entry ({p})"
        return None


# ---------------------------------------------------------------- rules

class Item:
    """A path to delete, or a path-less action. handler names a Disk Cleanup category run by Windows itself."""
    def __init__(self, path=None, label=None, action=None, size=None, handler=None):
        self.path, self.action, self.handler = path, action, handler
        self.label = label or path
        self.size = size
        self.result = None


class Rule:
    def __init__(self, rid, tier, title, why, find, checked=None, admin=False):
        self.id, self.tier, self.title, self.why, self.find = rid, tier, title, why, find
        self.checked = (tier != "ask") if checked is None else checked
        self.admin = admin
        self.items, self.skipped = [], []

    @property
    def total(self):
        return sum(i.size or 0 for i in self.items)


class Ctx:
    def __init__(self):
        self.images = running_images()
        self.names = {os.path.basename(p) for p in self.images}

    def running(self, names=(), dirs=()):
        if any(n.lower() in self.names for n in names):
            return True
        return any(under(img, d) for img in self.images for d in dirs if os.path.isdir(d))


def g(*parts):
    return glob.glob(os.path.join(*parts))


def f_updaters(ctx, r):
    for d in g(LOCAL, "*-updater"):
        if os.path.isdir(d):
            yield Item(d)


def _driver_version():
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=driver_version", "--format=csv,noheader"],
                             capture_output=True, text=True, timeout=20, creationflags=NOWIN).stdout
        return vkey(out.strip().splitlines()[0])
    except Exception:
        return None


def f_nvidia(ctx, r):
    grd = os.path.join(PD, "NVIDIA Corporation", "NVIDIA App", "UpdateFramework", "ota-artifacts", "grd")
    pp = os.path.join(grd, "post-processing")
    if os.path.isdir(grd):
        inst = _driver_version()
        for d in g(grd, "*"):
            if not os.path.isdir(d) or nc(d) == nc(pp):
                continue
            exe = next(iter(g(d, "*.exe")), None)
            ver = vkey(re.match(r"[\d.]+", os.path.basename(exe)).group(0)) if exe and re.match(r"\d", os.path.basename(exe)) else None
            if inst is None or ver is None:
                r.skipped.append((d, "could not compare with the installed driver"))
            elif ver <= inst:
                yield Item(d, f"Driver {'.'.join(map(str, ver))} installer (installed: {'.'.join(map(str, inst))})")
                if os.path.isdir(os.path.join(pp, os.path.basename(d))):
                    yield Item(os.path.join(pp, os.path.basename(d)), "  its unpacked copy")
            else:
                r.skipped.append((d, "newer than the installed driver, may be a pending update"))
        for d in g(pp, "*"):
            if os.path.isdir(d) and not os.path.isdir(os.path.join(grd, os.path.basename(d))) and idle_days(d) > 7:
                yield Item(d, "Unpacked driver with no installer left")
    for n in ("GeforceNowInstallerFiles", "GeForceNOW_tempNew"):
        d = os.path.join(LOCAL, "NVIDIA Corporation", n)
        if os.path.isdir(d) and idle_days(d) > 3:
            yield Item(d)


def f_squirrel(ctx, r):
    """Old app-x.y.z versions and update packages kept by Squirrel apps (Discord, Wand...)."""
    for upd in g(LOCAL, "*", "Update.exe"):
        root = os.path.dirname(upd)
        apps = sorted((d for d in g(root, "app-*") if os.path.isdir(d)), key=lambda d: vkey(os.path.basename(d)))
        old = apps[:-1]
        pkgs = [p for p in g(root, "packages", "*.nupkg") if idle_days(p) > 3]
        if not old and not pkgs:
            continue
        if ctx.running(dirs=[root]):
            r.skipped.append((root, f"{os.path.basename(root)} is running"))
            continue
        for d in old:
            yield Item(d, f"{os.path.basename(root)} {os.path.basename(d)} (current: {os.path.basename(apps[-1])})")
        for p in pkgs:
            yield Item(p, f"{os.path.basename(root)} update package {os.path.basename(p)}")


def _old_versions(base, keep_extra=()):
    vers = [d for d in g(base, "*") if re.fullmatch(r"\d+(\.\d+)+", os.path.basename(d))]
    if len(vers) < 2:
        return []
    newest = max(vers, key=lambda d: vkey(os.path.basename(d)))
    return [d for d in vers if d != newest and os.path.basename(d) not in keep_extra]


def f_claude_versions(ctx, r):
    for base in g(LOCAL, "Claude-Data", "*", "claude-code") + g(LOCAL, "Claude-Data", "*", "claude-code-vm"):
        sdk = ""
        try:
            sdk = open(os.path.join(base, ".sdk-version"), encoding="utf-8").read().strip()
        except OSError:
            pass
        for d in _old_versions(base, keep_extra=(sdk,)):
            if ctx.running(dirs=[d]):
                r.skipped.append((d, "this version is running"))
            else:
                yield Item(d, f"Claude Code {os.path.basename(d)} (desktop app copy)")
    cli = os.path.join(HOME, ".local", "share", "claude", "versions")
    for f in _old_versions(cli):
        if idle_days(f) > 7:
            yield Item(f, f"Claude CLI {os.path.basename(f)}")


def f_codex(ctx, r):
    cr = os.path.join(HOME, ".cache", "codex-runtimes")
    for d in g(cr, "*"):
        n = os.path.basename(d)
        if ".previous-" in n or (n.startswith("codex-runtime-install-") and idle_days(d) > 1):
            yield Item(d)
    rel = os.path.join(HOME, ".codex", "packages", "standalone", "releases")
    cur = os.path.join(HOME, ".codex", "packages", "standalone", "current")
    if os.path.lexists(cur):
        live = nc(os.path.realpath(cur))
        for d in g(rel, "*"):
            if nc(d) != live:
                yield Item(d, f"Codex CLI {os.path.basename(d)}")
    tmp = os.path.join(HOME, ".codex", ".tmp")
    if os.path.isdir(tmp):
        if ctx.running(names=["codex.exe"]):
            r.skipped.append((tmp, "Codex is running"))
        else:
            yield Item(tmp)


def f_pkg_caches(ctx, r):
    for p in (os.path.join(LOCAL, "npm-cache", "_cacache"), os.path.join(LOCAL, "npm-cache", "_logs"),
              os.path.join(LOCAL, "pip", "Cache"), os.path.join(LOCAL, "Yarn", "Cache")):
        if os.path.isdir(p):
            yield Item(p)
    for d in g(LOCAL, "npm-cache", "_npx", "*"):
        if idle_days(d) > 14:
            yield Item(d)
    uv = os.path.join(LOCAL, "uv", "cache")
    if os.path.isdir(uv):
        if ctx.running(names=["uv.exe"]):
            r.skipped.append((uv, "uv is running"))
        else:
            yield Item(uv)
    comfy = os.path.join(LOCAL, "Comfy-Desktop", "ComfyUI-Cache", "download-cache")
    if os.path.isdir(comfy) and idle_days(comfy) > 3:
        yield Item(comfy, "Comfy Desktop download cache")


# Chromium/Electron cache folders, recognised by what is inside them
def _is_chromium_cache(d):
    n = os.path.basename(d).lower()
    try:
        kids = {x.lower() for x in os.listdir(d)}
    except OSError:
        return False
    if n == "cache":
        return "cache_data" in kids or "index" in kids
    if n == "code cache":
        return bool(kids & {"js", "wasm", "webui_js"})
    if n in ("gpucache", "dawncache", "dawngraphitecache", "dawnwebgpucache", "grshadercache", "shadercache"):
        return "index" in kids or "data_0" in kids
    return False


def _cache_dirs(root, depth=5):
    out, stack = [], [(root, 0)]
    while stack:
        d, lvl = stack.pop()
        try:
            entries = [e for e in os.scandir(d) if e.is_dir(follow_symlinks=False) and not e.is_junction()]
        except OSError:
            continue
        for e in entries:
            low = e.name.lower()
            if low == "optguideondevicemodel":
                continue
            if _is_chromium_cache(e.path):
                out.append(e.path)
            elif low == "service worker":
                out += [p for p in (os.path.join(e.path, "CacheStorage"), os.path.join(e.path, "ScriptCache")) if os.path.isdir(p)]
            elif lvl < depth:
                stack.append((e.path, lvl + 1))
    return out


APPS = [  # label, user-data roots, process names, install dirs
    ("Claude app", [r"{L}\Claude-Data\*", r"{R}\Claude"], ["claude.exe"], []),
    ("Chrome", [r"{L}\Google\Chrome\User Data"], ["chrome.exe"], []),
    ("Chrome Beta", [r"{L}\Google\Chrome Beta\User Data"], ["chrome.exe"], []),
    ("Edge", [r"{L}\Microsoft\Edge\User Data"], ["msedge.exe"], []),
    ("Gemini app", [r"{R}\gemini"], ["gemini.exe", "geminiapplauncher.exe"], []),
    ("Notion", [r"{R}\Notion"], ["notion.exe"], [r"{L}\Programs\Notion"]),
    ("Discord", [r"{R}\discord"], ["discord.exe"], [r"{L}\Discord"]),
    ("Antigravity", [r"{R}\Antigravity"], ["antigravity.exe"], [r"{L}\Programs\antigravity"]),
    ("VS Code", [r"{R}\Code"], ["code.exe"], [r"{L}\Programs\Microsoft VS Code"]),
    ("Obsidian", [r"{R}\obsidian"], ["obsidian.exe"], [r"{L}\Programs\Obsidian"]),
    ("Wand", [r"{R}\WeMod", r"{R}\Wand"], ["wand.exe", "wemod.exe"], [r"{L}\Wand"]),
    ("Codex app", [r"{L}\Packages\OpenAI.Codex_*\LocalCache\Roaming\Codex", r"{R}\Codex"], ["codex.exe"], []),
    ("Comfy Desktop", [r"{R}\Comfy Desktop"], [], [r"{L}\Programs\Comfy Desktop"]),
    ("Open Design", [r"{R}\Open Design"], [], [r"{L}\Programs\Open Design"]),
    ("Power BI", [r"{L}\Microsoft\Power BI Desktop\WebView2"], ["pbidesktop.exe"], []),
]

LAUNCHERS = [  # label, cache folders, process names
    ("Steam web cache", [r"{L}\Steam\htmlcache"], ["steam.exe", "steamwebhelper.exe"]),
    ("Ubisoft Connect cache", [r"{L}\Ubisoft Game Launcher\cache"], ["upc.exe", "ubisoftconnect.exe", "uplaywebcore.exe"]),
    ("Epic launcher web cache", [r"{L}\EpicGamesLauncher\Saved\webcache*"], ["epicgameslauncher.exe"]),
    ("Zoom installer downloads", [r"{R}\Zoom\ZoomDownload"], ["zoom.exe"]),
]


def make_app_finder(label, roots, names, dirs):
    def find(ctx, r):
        paths = [p for pat in roots for p in glob.glob(_expand(pat)) if os.path.isdir(p)]
        if not paths:
            return
        if ctx.running(names=names, dirs=[_expand(d) for d in dirs]):
            r.skipped.append((label, f"{label} is open, close it to clear this"))
            return
        for root in paths:
            for d in _cache_dirs(root):
                yield Item(d)
    return find


def make_launcher_finder(label, pats, names):
    def find(ctx, r):
        paths = [p for pat in pats for p in glob.glob(_expand(pat)) if os.path.isdir(p)]
        if paths and ctx.running(names=names):
            r.skipped.append((label, f"{label.split()[0]} is open"))
            return
        for p in paths:
            yield Item(p)
    return find


def f_temp(ctx, r):
    for e in g(TEMP, "*"):
        if os.path.basename(e).lower() != "claude" and idle_days(e) > 7:
            yield Item(e)
    for d in g(TEMP, "claude", "*", "*"):
        if os.path.isdir(d) and idle_days(d, deep=True) > 7:
            yield Item(d, f"old agent session scratch {os.path.basename(d)[:8]}")
    for d in g(r"C:\Temp", "lo_*"):
        if idle_days(d) > 1:
            yield Item(d, f"LibreOffice scratch profile {os.path.basename(d)}")
    for f in g(LOCAL, "CrashDumps", "*"):
        if idle_days(f) > 1:
            yield Item(f)
    for d in g(LOCAL, "Microsoft", "Windows", "WER", "*", "*"):
        if idle_days(d, deep=True) > 7:
            yield Item(d, f"error report {os.path.basename(d)[:40]}")


def f_thumbnails(ctx, r):
    for f in g(LOCAL, "Microsoft", "Windows", "Explorer", "thumbcache_*.db"):
        yield Item(f, f"Explorer {os.path.basename(f)}")


def f_inetcache(ctx, r):
    base = os.path.join(LOCAL, "Microsoft", "Windows", "INetCache")
    for d in g(base, "IE", "*") + g(base, "Low", "IE", "*"):
        if os.path.isdir(d):
            yield Item(d, "Internet cache " + os.path.relpath(d, base))

def _walk_dirs(roots, skip_names=(".git", "node_modules", "AppData", ".venv", "venv")):
    for root in roots:
        stack = [root]
        while stack:
            d = stack.pop()
            try:
                for e in os.scandir(d):
                    if e.is_dir(follow_symlinks=False) and not e.is_junction():
                        yield e
                        if e.name not in skip_names:
                            stack.append(e.path)
            except OSError:
                pass


PROJECT_ROOTS = [os.path.join(HOME, n) for n in ("Downloads", "Documents", "Desktop", "source")]


def f_node_modules(ctx, r):
    for e in _walk_dirs(PROJECT_ROOTS):
        if e.name != "node_modules":
            continue
        parent = os.path.dirname(e.path)
        if under(e.path, os.path.join(HOME, "Documents", "Codex")):
            continue
        if os.path.basename(os.path.dirname(parent)) == "node_modules" or not os.path.exists(os.path.join(parent, "package.json")):
            continue
        stamps = [mtime(e.path), mtime(os.path.join(e.path, ".package-lock.json"))]
        try:
            stamps += [x.stat().st_mtime for x in os.scandir(parent) if x.is_file()]
        except OSError:
            pass
        if (time.time() - max(stamps)) / DAY > 30:
            yield Item(e.path, os.path.relpath(e.path, HOME))


def f_partial(ctx, r):
    ends = (".crdownload", ".part", ".partial", ".opdownload")
    for root in (os.path.join(HOME, "Downloads"), os.path.join(HOME, "Desktop")):
        for dp, dirs, files in os.walk(root):
            dirs[:] = [d for d in dirs if not is_link(os.path.join(dp, d)) and d != "node_modules"]
            for f in files:
                p = os.path.join(dp, f)
                if f.lower().endswith(ends) and idle_days(p) > 2:
                    yield Item(p, os.path.relpath(p, HOME))


class _SHQUERYRBINFO(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("i64Size", ctypes.c_int64), ("i64NumItems", ctypes.c_int64)]


def f_recycle(ctx, r):
    info = _SHQUERYRBINFO(ctypes.sizeof(_SHQUERYRBINFO), 0, 0)
    if ctypes.windll.shell32.SHQueryRecycleBinW(None, ctypes.byref(info)) == 0 and info.i64NumItems:
        def empty():
            ctypes.windll.shell32.SHEmptyRecycleBinW(None, None, 0x7)  # no confirm, progress or sound
        yield Item(label=f"Recycle Bin ({info.i64NumItems} items)", action=empty, size=info.i64Size)


def f_win_temp(ctx, r):
    for base, days in ((os.path.join(WIN, "Temp"), 7), (os.path.join(WIN, "SoftwareDistribution", "Download"), 10),
                       (os.path.join(PD, "Microsoft", "Windows", "WER", "ReportArchive"), 7),
                       (os.path.join(PD, "Microsoft", "Windows", "WER", "ReportQueue"), 7)):
        for e in g(base, "*"):
            if idle_days(e) > days:
                yield Item(e)


def _ps(cmd):
    subprocess.run(["powershell", "-NoProfile", "-Command", cmd], capture_output=True, timeout=1800, creationflags=NOWIN)


def f_delivery_opt(ctx, r):
    d = os.path.join(WIN, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows",
                     "DeliveryOptimization", "Cache")
    size = tree_size(d)
    if size:
        yield Item(label="Delivery Optimization cache", size=size,
                   action=lambda: _ps("Delete-DeliveryOptimizationCache -Force"))


def _old_files(base, days, skip_dirs=()):
    """Files under base untouched for `days`. Folders stay, so the services that write them still find them."""
    for dp, dirs, files in os.walk(base):
        dirs[:] = [d for d in dirs if not is_link(os.path.join(dp, d)) and d.lower() not in skip_dirs]
        for f in files:
            p = os.path.join(dp, f)
            if idle_days(p) > days:
                yield p


def f_win_logs(ctx, r):
    # measured-boot logs back TPM health checks and SystemRestore logs explain restore points: kept to be safe
    for base in (os.path.join(WIN, "Logs"), os.path.join(WIN, "System32", "LogFiles")):
        for p in _old_files(base, 7, skip_dirs=("measuredboot", "systemrestore")):
            yield Item(p, os.path.relpath(p, WIN))
    for base in (os.path.join(WIN, "SystemTemp"), os.path.join(PD, "Microsoft", "WDF"),
                 os.path.join(PD, "Microsoft", "Windows", "HiveUpload")):
        for e in g(base, "*"):
            if idle_days(e, deep=True) > 7:
                yield Item(e)


def f_win_diag(ctx, r):
    for base in (os.path.join(PD, "Microsoft", "Diagnosis", "FeedbackArchive"),
                 os.path.join(PD, "Microsoft", "Diagnosis", "EventTranscript"),
                 os.path.join(PD, "Microsoft", "Windows", "RetailDemo")):
        for e in g(base, "*"):
            yield Item(e)
    for base in (os.path.join(PD, "Microsoft", "Windows Defender", "LocalCopy"),
                 os.path.join(PD, "Microsoft", "Windows Defender", "Support")):
        for p in _old_files(base, 7):
            yield Item(p, "Defender " + os.path.relpath(p, os.path.dirname(base)))
    for pat in ("setup*.log", "setup*.old", "setuplog.txt", "winnt32.log"):
        for p in g(WIN, pat):
            if os.path.isfile(p) and idle_days(p) > 30:
                yield Item(p)
    for d in FOUND_DIRS:
        for f in g(d, "*.CHK"):
            yield Item(f, "old ChkDsk fragment " + os.path.relpath(f, "C:\\"))


def f_shader_cache(ctx, r):
    for e in g(LOCAL, "D3DSCache", "*"):
        yield Item(e)


def f_memory_dump(ctx, r):
    for p in g(WIN, "*.dmp"):
        if not is_admin():
            r.skipped.append((p, "needs admin"))
        else:
            yield Item(p, f"{os.path.basename(p)} from {time.strftime('%d %b %Y', time.localtime(mtime(p)))}")


# old driver packages, the way Disk Cleanup's "Device driver packages" does it
def _driver_packages():
    """pnputil's driver list as dicts. Its labels are English, so other languages find nothing (safe)."""
    out = subprocess.run(["pnputil", "/enum-drivers"], capture_output=True, text=True, timeout=120,
                         creationflags=NOWIN).stdout
    pkgs, cur = [], {}
    for line in out.splitlines() + [""]:
        if ":" in line:
            k, v = line.split(":", 1)
            cur[k.strip()] = v.strip()
        elif not line.strip() and cur:
            pkgs.append(cur)
            cur = {}
    return [p for p in pkgs if p.get("Published Name", "").lower().startswith("oem")]


def _infs_in_use():
    """oemNN.inf names a device is installed with right now."""
    key, used = r"SYSTEM\CurrentControlSet\Control\Class", set()
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, key) as k:
            for i in range(winreg.QueryInfoKey(k)[0]):
                cls = winreg.EnumKey(k, i)
                try:
                    with winreg.OpenKey(k, cls) as ck:
                        for j in range(winreg.QueryInfoKey(ck)[0]):
                            sub = winreg.EnumKey(ck, j)
                            used.update(v.lower() for v in _reg_values(winreg.HKEY_LOCAL_MACHINE, rf"{key}\{cls}\{sub}", "InfPath"))
                except OSError:
                    pass
    except OSError:
        pass
    return used


def _delete_driver(oem):
    # no /force: Windows itself refuses a package a device still uses
    res = subprocess.run(["pnputil", "/delete-driver", oem], capture_output=True, text=True, timeout=300,
                         creationflags=NOWIN)
    if res.returncode:
        raise RuntimeError("Windows kept it (still in use)")


def f_old_drivers(ctx, r):
    pkgs = _driver_packages()
    if not pkgs:
        r.skipped.append(("driver list", "pnputil gave no list"))
        return
    used, store = _infs_in_use(), os.path.join(WIN, "System32", "DriverStore", "FileRepository")
    groups = {}
    for p in pkgs:
        p["ver"] = vkey((p.get("Driver Version") or "0").split()[-1])
        groups.setdefault((p.get("Original Name", "").lower(), p.get("Provider Name", "")), []).append(p)
    for (name, _), ps in groups.items():
        newest = max(p["ver"] for p in ps)
        for p in ps:
            oem, ver = p["Published Name"], ".".join(map(str, p["ver"]))
            if p["ver"] >= newest:  # the newest, and any package that ties with it, stay
                continue
            if oem.lower() in used:
                r.skipped.append((f"{name} {ver}", "a device still uses it"))
                continue
            # the live database is the DRIVERS hive; SYSTEM\DriverDatabase holds an older copy
            pkg = [v for db in (r"DRIVERS\DriverDatabase", r"SYSTEM\DriverDatabase")
                   for v in _reg_values(winreg.HKEY_LOCAL_MACHINE, rf"{db}\DriverInfFiles\{oem}", "Active") if v]
            size = tree_size(os.path.join(store, pkg[0])) if pkg else None
            yield Item(label=f"{name} {ver} ({oem}; keeps {'.'.join(map(str, newest))})", size=size,
                       action=lambda oem=oem: _delete_driver(oem))


# Windows' own Disk Cleanup handlers, for what only Windows can remove safely (component store, Windows.old)
VOLUME_CACHES = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches"
SAGE = 777  # the cleanmgr /sagerun slot this app uses; its flags are removed after every run

WIN_HANDLERS = [  # category, what it is
    ("Update Cleanup", "Superseded Windows Update files"),
    ("Windows Upgrade Log Files", "Logs from past Windows upgrades"),
    ("Temporary Setup Files", "Files Windows setup left behind"),
    ("Upgrade Discarded Files", "Files a past upgrade set aside"),
    ("Windows Reset Log Files", "Logs from past resets"),
    ("Language Pack", "Language resources no account here uses"),
    ("Downloaded Program Files", "Old ActiveX controls and Java applets"),
    ("Temporary Sync Files", "Leftover sync scratch files"),
    ("Offline Pages Files", "Old offline web pages"),
    ("Active Setup Temp Folders", "Old component setup scratch"),
    ("Content Indexer Cleaner", "Old search indexer files"),
]


def _handler_exists(name):
    try:
        winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, VOLUME_CACHES + "\\" + name).Close()
        return True
    except OSError:
        return False


def run_handlers(names):
    """Run Windows' own Disk Cleanup for exactly these categories, then remove the flags again."""
    flag, acc = f"StateFlags{SAGE:04d}", winreg.KEY_SET_VALUE | winreg.KEY_WOW64_64KEY

    def set_flag(cat, on):
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, VOLUME_CACHES + "\\" + cat, 0, acc) as k:
                if on:
                    winreg.SetValueEx(k, flag, 0, winreg.REG_DWORD, 2)
                else:
                    winreg.DeleteValue(k, flag)
        except FileNotFoundError:
            pass
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, VOLUME_CACHES) as k:
        cats = [winreg.EnumKey(k, i) for i in range(winreg.QueryInfoKey(k)[0])]
    for c in cats:  # a crashed earlier run must not widen this one: the Downloads folder is a category too
        set_flag(c, False)
    try:
        for n in names:
            set_flag(n, True)
        subprocess.run([os.path.join(WIN, "System32", "cleanmgr.exe"), f"/sagerun:{SAGE}"], timeout=7200)
    finally:
        for n in names:
            set_flag(n, False)


def f_win_update(ctx, r):
    for name, what in WIN_HANDLERS:
        if _handler_exists(name):
            yield Item(label=f"{what} ({name})", handler=name)


def f_win_old(ctx, r):
    d = r"C:\Windows.old"
    if os.path.isdir(d) and _handler_exists("Previous Installations"):
        yield Item(label="Windows.old, your previous Windows", size=tree_size(d), handler="Previous Installations")


def f_win_esd(ctx, r):
    ds = [d for d in (r"C:\ESD", r"C:\$Windows.~WS") if os.path.isdir(d)]
    if ds and _handler_exists("Windows ESD installation files"):
        yield Item(label="Windows installation files (ESD)", size=sum(tree_size(d) for d in ds),
                   handler="Windows ESD installation files")


def build_rules():
    rules = [
        Rule("nvidia", "safe", "NVIDIA driver downloads", "Copies of drivers that are already installed, plus GeForce NOW installer leftovers.", f_nvidia),
        Rule("updaters", "safe", "App update installers", "Installers apps downloaded for updates. They fetch a fresh one when needed.", f_updaters),
        Rule("old-versions", "safe", "Old app versions", "Previous versions Discord, Wand and similar apps keep after updating.", f_squirrel),
        Rule("claude-versions", "safe", "Old Claude Code versions", "Earlier Claude Code builds. The newest and any running one stay.", f_claude_versions),
        Rule("codex", "safe", "Old Codex runtimes", "Superseded Codex runtimes, an older CLI release and Codex's temp folder.", f_codex),
        Rule("pkg-caches", "safe", "Package download caches", "npm, pip, uv and Comfy Desktop download caches. They refill on the next install.", f_pkg_caches),
    ]
    for label, roots, names, dirs in APPS:
        rules.append(Rule("cache-" + label.lower().replace(" ", "-"), "apps", label, "Web and GPU cache folders",
                          make_app_finder(label, roots, names, dirs)))
    for label, pats, names in LAUNCHERS:
        rules.append(Rule("launcher-" + label.split()[0].lower(), "apps", label,
                          "Launcher cache, rebuilt on next start", make_launcher_finder(label, pats, names)))
    rules += [
        Rule("temp", "safe", "Old temp files", "Temp files, crash dumps and error reports untouched for a week, plus old agent and LibreOffice scratch.", f_temp),
        Rule("thumbnails", "safe", "Thumbnail cache", "Picture previews Explorer saved. It redraws them as you browse.", f_thumbnails),
        Rule("inet-cache", "safe", "Internet cache files", "Windows' own web cache, used by older apps and Office.", f_inetcache),
        Rule("partial", "safe", "Unfinished downloads", "Partial downloads (.crdownload, .part) left in Downloads or Desktop for 2+ days.", f_partial),
        Rule("node-modules", "safe", "Idle node_modules", "Dependencies of projects untouched for 30 days. npm install brings them back.", f_node_modules),
        Rule("recycle", "ask", "Recycle Bin", "Empties it for good.", f_recycle),
        Rule("shader-cache", "ask", "DirectX shader cache", "Games rebuild it, but stutter for a while after.", f_shader_cache),
        Rule("memory-dump", "ask", "Blue-screen memory dump", "Full crash dump. Keep it while you are still fixing crashes.", f_memory_dump),
        Rule("win-temp", "admin", "Windows temp and update leftovers", "Windows Temp, old Windows Update downloads and error reports.", f_win_temp, admin=True),
        Rule("delivery-opt", "admin", "Delivery Optimization cache", "Update pieces Windows keeps to share with other PCs.", f_delivery_opt, admin=True),
        Rule("win-logs", "admin", "Old Windows log files", "Logs Windows wrote more than a week ago, plus its system temp folder. Folders stay.", f_win_logs, admin=True),
        Rule("win-diag", "admin", "Windows diagnostic leftovers", "Feedback Hub archives, old setup logs, Defender's non-critical files, ChkDsk fragments.", f_win_diag, admin=True),
        Rule("win-update", "admin", "Windows Update cleanup", "Superseded update files and setup leftovers, removed by Windows' own Disk Cleanup. Size shows after the run.", f_win_update, admin=True),
        Rule("old-drivers", "admin", "Old driver versions", "Older copies of drivers a newer version replaced. You can't roll those drivers back after.", f_old_drivers, checked=False, admin=True),
        Rule("win-old", "admin", "Previous Windows installation", "Lets you go back to your old Windows for 10 days after an upgrade.", f_win_old, checked=False, admin=True),
        Rule("win-esd", "admin", "Windows installation files", "Needed for Reset this PC without a download. Keep unless space is desperate.", f_win_esd, checked=False, admin=True),
    ]
    return rules


KEPT = [
    ("Claude desktop VM (vm_bundles)", "Claude's local VM runs on it"),
    ("Ollama, LM Studio and Hugging Face models", "Re-downloading costs hours"),
    ("WinGet tools, Programs, PATH folders", "Installed tools such as ffmpeg and uv"),
    ("NVIDIA shader caches", "Games stutter while they rebuild. The DirectX one is under Your call"),
    ("Chrome on-device AI model", "Chrome features use it"),
    ("Windows minidumps", "Needed to diagnose blue screens"),
    ("Your Downloads folder", "Windows Disk Cleanup offers to empty it. This app never will"),
    ("File History versions", "Your backups. Disk Cleanup lists them; this app leaves them"),
    ("OneDrive, CrossDevice, .ssh", "Synced or personal files"),
]


# ---------------------------------------------------------------- scan and clean

def scan(admin=None, progress=None):
    admin = is_admin() if admin is None else admin
    ctx, guard, rules = Ctx(), Guard(), build_rules()
    for i, r in enumerate(rules):
        if progress:
            progress(i, len(rules), r.title)
        if r.admin and not admin:
            r.skipped.append((r.title, "needs admin"))
            continue
        try:
            found = list(r.find(ctx, r))
        except Exception as ex:
            r.skipped.append((r.title, f"scan error: {ex}"))
            continue
        for it in found:
            if it.path:
                why = guard.check(it.path)
                if why:
                    r.skipped.append((it.path, why))
                    continue
            r.items.append(it)
        with ThreadPoolExecutor(8) as ex:
            sizes = list(ex.map(lambda it: it.size if it.path is None else tree_size(it.path), r.items))
        for it, s in zip(r.items, sizes):
            it.size = s
        r.items = [it for it in r.items if it.path is None or it.size]
    return rules, guard


def _rm(path):
    def onexc(func, p, exc):
        try:
            os.chmod(p, stat.S_IWRITE)
            func(p)
        except OSError:
            pass
    if os.path.isdir(path) and not is_link(path):
        shutil.rmtree(path, onexc=onexc)
    else:
        try:
            os.chmod(path, stat.S_IWRITE)
        except OSError:
            pass
        os.remove(path)


def clean(rules, guard, dry_run=False, progress=None):
    """Delete the items of the given rules. Returns bytes freed (measured on the drive)."""
    before = free_bytes()
    rows, items = [], [(r, it) for r in rules for it in r.items]
    handlers = [(r, it) for r, it in items if it.handler]
    for n, (r, it) in enumerate(items):
        if it.handler:
            continue  # Windows runs these together below, in one Disk Cleanup pass
        if progress:
            progress(n, len(items), it.label)
        if dry_run:
            it.result = "dry run"
        elif it.action:
            try:
                it.action()
                it.result = "done"
            except Exception as ex:
                it.result = f"failed: {ex}"
        else:
            why = guard.check(it.path)  # re-check right before deleting
            if why:
                it.result = f"refused: {why}"
            else:
                try:
                    _rm(it.path)
                except OSError:
                    pass
                left = tree_size(it.path) if os.path.lexists(it.path) else 0
                it.result = "deleted" if not left else f"partly in use, {fmt(left)} left"
        rows.append([time.strftime("%Y-%m-%d %H:%M:%S"), r.id, it.path or it.label, it.size or 0, it.result])
    if handlers:
        if progress:
            progress(len(items) - 1, len(items), "Windows Disk Cleanup is running (Update Cleanup can take 10+ minutes)")
        res = "dry run"
        if not dry_run:
            try:
                run_handlers([it.handler for _, it in handlers])
                res = "done by Windows"
            except Exception as ex:
                res = f"failed: {ex}"
        for r, it in handlers:
            it.result = res
            rows.append([time.strftime("%Y-%m-%d %H:%M:%S"), r.id, it.label, it.size or 0, res])
    freed = free_bytes() - before
    if not dry_run and rows:
        new = not os.path.exists(LOG_CSV)
        with open(LOG_CSV, "a", newline="", encoding="utf-8") as fh:
            w = csv.writer(fh)
            if new:
                w.writerow(["time", "rule", "path", "bytes", "result"])
            w.writerows(rows)
            w.writerow([time.strftime("%Y-%m-%d %H:%M:%S"), "TOTAL", "drive free-space change", freed, "measured"])
    return freed


# ---------------------------------------------------------------- CLI

def cli(argv):
    cmd = argv[0] if argv else "ui"
    if cmd == "ui":
        snap = argv[argv.index("--snapshot") + 1] if "--snapshot" in argv else None
        return run_ui(snap)
    rules, guard = scan()
    if cmd == "scan":
        if "--json" in argv:
            print(json.dumps({"admin": is_admin(), "free": free_bytes(), "rules": [
                {"id": r.id, "tier": r.tier, "title": r.title, "checked": r.checked, "bytes": r.total,
                 "items": [{"path": i.path, "label": i.label, "bytes": i.size} for i in r.items],
                 "skipped": [{"what": a, "why": b} for a, b in r.skipped]} for r in rules]}, indent=1))
        else:
            for r in rules:
                if r.items or r.skipped:
                    mark = "x" if r.checked else " "
                    print(f"[{mark}] {r.tier:5s} {fmt(r.total):>9s}  {r.title}")
                    for a, b in r.skipped:
                        print(f"            skipped: {b}")
            print(f"\nFree on C: {fmt(free_bytes())}. Pre-checked total: {fmt(sum(r.total for r in rules if r.checked))}")
        return 0
    if cmd == "clean":
        tier = argv[argv.index("--tier") + 1] if "--tier" in argv else None
        ids = argv[argv.index("--rules") + 1].split(",") if "--rules" in argv else None
        # a tier takes its ticked rules; "ask" rules start unticked, so that tier takes them all.
        # Off-by-default Windows rules (old drivers, Windows.old, ESD) run only when named with --rules.
        pick = [r for r in rules if (ids and r.id in ids) or (not ids and (r.checked or r.tier == "ask") and (
            tier == "all" or r.tier == tier or (tier is None and r.checked)))]
        freed = clean(pick, guard, dry_run="--dry-run" in argv)
        for r in pick:
            for it in r.items:
                print(f"{fmt(it.size):>9s}  {it.result:28s} {it.label}")
        print(f"\nDrive free space changed by {fmt(freed)}")
        return 0
    print(__doc__)
    return 2


# ---------------------------------------------------------------- window

def run_ui(snapshot=None):
    import tkinter as tk
    from tkinter import ttk, messagebox
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except Exception:
        pass
    admin = is_admin()
    root = tk.Tk()
    root.title("Disk Cleaner" + ("  (admin)" if admin else ""))
    k = root.winfo_fpixels("1i") / 96

    def px(n):
        return int(n * k)

    BG, CARD, INK, MUTED, LINE = "#F5F3EE", "#FFFFFF", "#1F2428", "#6A7178", "#E3DED4"
    ACC, ACC_SOFT, GOOD, WARN = "#2F5D7C", "#E3ECF2", "#2E7A57", "#9A5A26"
    FONT, HEAD = ("Segoe UI Variable Text", 10), ("Segoe UI Variable Display", 20, "bold")
    root.configure(bg=BG)
    root.geometry(f"{px(1080)}x{px(760)}")
    root.minsize(px(820), px(560))
    try:
        root.iconbitmap(os.path.join(WIN, "System32", "cleanmgr.exe"))
    except Exception:
        pass

    st = ttk.Style(root)
    st.theme_use("clam")
    st.configure(".", font=FONT, background=BG, foreground=INK)
    st.configure("Card.TFrame", background=CARD)
    st.configure("T.Treeview", background=CARD, fieldbackground=CARD, foreground=INK, rowheight=px(30),
                 borderwidth=0, font=FONT)
    st.configure("T.Treeview.Heading", background=BG, foreground=MUTED, relief="flat",
                 font=("Segoe UI Variable Text", 9, "bold"), padding=(px(8), px(6)))
    st.map("T.Treeview", background=[("selected", ACC_SOFT)], foreground=[("selected", INK)])
    st.layout("T.Treeview", [("Treeview.treearea", {"sticky": "nswe"})])
    st.configure("Go.TButton", background=ACC, foreground="#FFFFFF", borderwidth=0, padding=(px(18), px(9)),
                 font=("Segoe UI Variable Text", 10, "bold"))
    st.map("Go.TButton", background=[("disabled", "#9DB2C2"), ("active", "#264E69")])
    st.configure("Quiet.TButton", background=CARD, foreground=INK, borderwidth=1, bordercolor=LINE,
                 padding=(px(14), px(8)))
    st.map("Quiet.TButton", background=[("active", ACC_SOFT)])
    st.configure("Bar.Horizontal.TProgressbar", troughcolor=LINE, background=ACC, borderwidth=0, thickness=px(4))

    # header
    head = tk.Frame(root, bg=BG)
    head.pack(fill="x", padx=px(24), pady=(px(20), px(10)))
    left = tk.Frame(head, bg=BG)
    left.pack(side="left", fill="x", expand=True)
    tk.Label(left, text="Disk Cleaner", font=HEAD, bg=BG, fg=INK).pack(anchor="w")
    tk.Label(left, text="Covers what Windows Disk Cleanup does, plus app caches it never sees.",
             font=FONT, bg=BG, fg=MUTED).pack(anchor="w", pady=(px(2), 0))
    tk.Label(left, text="Installed tools, models and your files are never touched.",
             font=FONT, bg=BG, fg=MUTED).pack(anchor="w")
    right = tk.Frame(head, bg=BG)
    right.pack(side="right", anchor="n")
    badge = tk.Label(right, text=" ADMIN " if admin else " STANDARD ", font=("Segoe UI Variable Text", 8, "bold"),
                     bg=GOOD if admin else LINE, fg="#FFFFFF" if admin else MUTED, padx=px(6), pady=px(2))
    badge.pack(anchor="e")
    drive_txt = tk.Label(right, font=("Segoe UI Variable Text", 10, "bold"), bg=BG, fg=INK)
    drive_txt.pack(anchor="e", pady=(px(8), px(4)))
    meter = tk.Canvas(right, width=px(260), height=px(8), bg=LINE, highlightthickness=0)
    meter.pack(anchor="e")

    def draw_drive():
        u = shutil.disk_usage("C:\\")
        drive_txt.config(text=f"C:  {fmt(u.free)} free of {fmt(u.total)}")
        meter.delete("all")
        meter.create_rectangle(0, 0, px(260) * u.used / u.total, px(8), fill=ACC, width=0)

    draw_drive()

    # list
    card = tk.Frame(root, bg=CARD, highlightbackground=LINE, highlightthickness=1)
    card.pack(fill="both", expand=True, padx=px(24))
    tree = ttk.Treeview(card, style="T.Treeview", columns=("pick", "size", "why"), selectmode="browse")
    tree.heading("#0", text="ITEM", anchor="w")
    tree.heading("pick", text="")
    tree.heading("size", text="SIZE", anchor="e")
    tree.heading("why", text="WHAT IT IS", anchor="w")
    tree.column("#0", width=px(300), anchor="w")
    tree.column("pick", width=px(44), stretch=False, anchor="center")
    tree.column("size", width=px(96), stretch=False, anchor="e")
    tree.column("why", width=px(560), anchor="w")
    tree.tag_configure("tier", font=("Segoe UI Variable Text", 10, "bold"), background="#FAF8F4")
    tree.tag_configure("muted", foreground=MUTED)
    tree.tag_configure("warn", foreground=WARN)
    sb = ttk.Scrollbar(card, orient="vertical", command=tree.yview)
    tree.configure(yscrollcommand=sb.set)
    sb.pack(side="right", fill="y")
    tree.pack(fill="both", expand=True, padx=px(4), pady=px(4))

    detail = tk.Text(root, height=7, bg=BG, fg=MUTED, relief="flat", font=("Cascadia Mono", 9), wrap="none",
                     highlightthickness=0, padx=px(2))
    detail.pack(fill="x", padx=px(24), pady=(px(10), 0))

    foot = tk.Frame(root, bg=BG)
    foot.pack(fill="x", padx=px(24), pady=(px(8), px(18)))
    status = tk.Label(foot, text="", font=FONT, bg=BG, fg=MUTED, anchor="w")
    status.pack(side="left", fill="x", expand=True)
    bar = ttk.Progressbar(foot, style="Bar.Horizontal.TProgressbar", length=px(140), mode="determinate")
    go = ttk.Button(foot, text="Clean selected", style="Go.TButton")
    go.pack(side="right")
    rescan = ttk.Button(foot, text="Rescan", style="Quiet.TButton")
    rescan.pack(side="right", padx=(0, px(8)))
    if not admin:
        def elevate():
            exe = sys.executable.replace("python.exe", "pythonw.exe")
            if ctypes.windll.shell32.ShellExecuteW(None, "runas", exe, f'"{os.path.abspath(__file__)}" ui', None, 1) > 32:
                root.destroy()
        ttk.Button(foot, text="Restart as admin", style="Quiet.TButton", command=elevate).pack(side="right", padx=(0, px(8)))

    state = {"rules": [], "guard": None, "checked": {}, "rows": {}, "busy": False}
    TIERS = [("safe", "Leftovers", "Nothing uses these any more"),
             ("apps", "App and browser caches", "Logins and settings stay. An open app is skipped."),
             ("ask", "Your call", "Unchecked until you tick them"),
             ("admin", "Windows", "What Disk Cleanup's system-file mode clears")]

    def selected_total():
        return sum(r.total for r in state["rules"] if state["checked"].get(r.id) and r.items)

    def refresh_go():
        t = selected_total()
        some = any(state["checked"].get(r.id) and r.items for r in state["rules"])
        go.config(text=f"Clean selected  ·  {fmt(t)}" if t else "Clean selected")
        go.state(["!disabled"] if some and not state["busy"] else ["disabled"])

    def populate():
        tree.delete(*tree.get_children())
        state["rows"].clear()
        for tier, name, blurb in TIERS:
            rs = [r for r in state["rules"] if r.tier == tier and (r.items or r.skipped)]
            if not rs:
                continue
            total = sum(r.total for r in rs)
            if tier == "admin" and not admin:
                blurb = "Restart as admin to scan these"
            tid = tree.insert("", "end", text=name, values=("", fmt(total) if total else "", blurb), open=True, tags=("tier",))
            state["rows"][tid] = ("tier", tier)
            for r in sorted(rs, key=lambda r: -r.total):
                if r.items:
                    box = "☑" if state["checked"].get(r.id) else "☐"
                    has_unknown = any(i.size is None for i in r.items)
                    size = fmt(r.total) + (" +" if has_unknown and r.total else "") if r.total else ("—" if has_unknown else "")
                    note = f"{len(r.items)} cache folders" if tier == "apps" else r.why
                    note += f"  ({len(r.skipped)} skipped)" if r.skipped else ""
                    iid = tree.insert(tid, "end", text=r.title, values=(box, size, note))
                else:
                    iid = tree.insert(tid, "end", text=r.title, values=("", "", r.skipped[0][1]), tags=("muted",))
                state["rows"][iid] = ("rule", r)
        kid = tree.insert("", "end", text="Kept on purpose", values=("", "", ""), open=False, tags=("tier",))
        state["rows"][kid] = ("tier", "kept")
        for name, why in KEPT:
            tree.insert(kid, "end", text=name, values=("", "", why), tags=("muted",))
        refresh_go()

    def show_detail(_=None):
        sel = tree.focus()
        kind = state["rows"].get(sel)
        detail.config(state="normal")
        detail.delete("1.0", "end")
        if not kind or kind[0] != "rule":
            detail.insert("end", "Select a row to see exactly which folders it covers.\n"
                                 "Tick or untick with the box column or the space bar. "
                                 "Every run is logged to runs.csv.")
        if kind and kind[0] == "rule":
            r = kind[1]
            for it in sorted(r.items, key=lambda i: -(i.size or 0)):
                res = f"  [{it.result}]" if it.result else ""
                detail.insert("end", f"{fmt(it.size):>9}  {it.label}{res}\n")
            for a, b in r.skipped:
                detail.insert("end", f"  skipped  {a}: {b}\n")
        detail.config(state="disabled")

    def toggle(iid):
        kind = state["rows"].get(iid)
        if not kind or state["busy"]:
            return
        if kind[0] == "rule" and kind[1].items:
            r = kind[1]
            state["checked"][r.id] = not state["checked"].get(r.id)
            tree.set(iid, "pick", "☑" if state["checked"][r.id] else "☐")
        elif kind[0] == "tier" and kind[1] != "kept":
            kids = [c for c in tree.get_children(iid) if state["rows"][c][0] == "rule" and state["rows"][c][1].items]
            on = not all(state["checked"].get(state["rows"][c][1].id) for c in kids)
            for c in kids:
                state["checked"][state["rows"][c][1].id] = on
                tree.set(c, "pick", "☑" if on else "☐")
        refresh_go()

    def on_click(ev):
        iid = tree.identify_row(ev.y)
        if iid and tree.identify_column(ev.x) == "#1":
            toggle(iid)
            return "break"

    tree.bind("<Button-1>", on_click)
    tree.bind("<space>", lambda e: toggle(tree.focus()))
    tree.bind("<<TreeviewSelect>>", show_detail)

    def busy(on, text=""):
        state["busy"] = on
        status.config(text=text)
        if on:
            bar.pack(side="right", padx=(0, px(12)))
            rescan.state(["disabled"])
        else:
            bar.pack_forget()
            rescan.state(["!disabled"])
        refresh_go()

    def prog(i, n, label):
        root.after(0, lambda: (bar.config(maximum=max(n, 1), value=i), status.config(text=label[:90])))

    def do_scan(after=None):
        busy(True, "Scanning…")

        def work():
            try:
                rules, guard = scan(admin, progress=prog)
                err = None
            except Exception:
                rules, guard, err = [], None, traceback.format_exc()

            def done():
                if err:
                    open(ERR_LOG, "a", encoding="utf-8").write(err)
                    busy(False, "Scan failed, see error.log")
                    return
                old = state["checked"]
                state.update(rules=rules, guard=guard)
                state["checked"] = {r.id: old.get(r.id, r.checked) for r in rules}
                populate()
                show_detail()
                draw_drive()
                busy(False, after or f"Found {fmt(sum(r.total for r in rules))}. Pre-checked: {fmt(selected_total())}.")
                if snapshot:
                    root.after(700, take_snapshot)
            root.after(0, done)
        threading.Thread(target=work, daemon=True).start()

    def do_clean():
        pick = [r for r in state["rules"] if state["checked"].get(r.id) and r.items]
        n = sum(len(r.items) for r in pick)
        win = any(it.handler for r in pick for it in r.items)
        note = "\n\nWindows' own Disk Cleanup runs too and can take 10+ minutes." if win else ""
        if not pick or not messagebox.askyesno(
                "Clean selected", f"Delete {n} items, about {fmt(selected_total())}?{note}\n\nThis can't be undone.", parent=root):
            return
        busy(True, "Cleaning…")

        def work():
            freed = clean(pick, state["guard"], progress=prog)
            refused = [it for r in pick for it in r.items if it.result and it.result.startswith(("refused", "failed", "partly"))]
            msg = f"Freed {fmt(freed)} (measured on the drive)." + (f" {len(refused)} items skipped or in use." if refused else "")
            root.after(0, lambda: do_scan(after=msg))
        threading.Thread(target=work, daemon=True).start()

    def take_snapshot():
        try:
            from PIL import ImageGrab
            root.attributes("-topmost", True)
            if os.environ.get("CLEANER_SNAP_END"):  # snapshot the bottom of the list
                tree.yview_moveto(1)
            root.update()
            x, y, w, h = root.winfo_rootx(), root.winfo_rooty(), root.winfo_width(), root.winfo_height()
            ImageGrab.grab(bbox=(x, y, x + w, y + h), all_screens=True).save(snapshot)
        finally:
            root.destroy()

    go.config(command=do_clean)
    rescan.config(command=do_scan)
    root.after(100, do_scan)
    root.mainloop()
    return 0


if __name__ == "__main__":
    try:
        sys.exit(cli(sys.argv[1:]))
    except SystemExit:
        raise
    except Exception:
        with open(ERR_LOG, "a", encoding="utf-8") as fh:
            fh.write(time.strftime("%Y-%m-%d %H:%M:%S\n") + traceback.format_exc() + "\n")
        raise
