using System.Text.RegularExpressions;
using static SafeDiskCleaner.Core.PathUtil;

namespace SafeDiskCleaner.Core;

/// <summary>build_rules() and the f_* finders from reference/engine.py, in the same order.</summary>
public static partial class RuleCatalog
{
    private static string Home => KnownPaths.Home;
    private static string Local => KnownPaths.Local;
    private static string PD => KnownPaths.ProgramData;
    private static string Win => KnownPaths.Windows;
    private static string Temp => KnownPaths.Temp;

    public static List<Rule> Build()
    {
        var rules = new List<Rule>
        {
            new("nvidia", RuleGroup.Leftovers, "NVIDIA driver downloads",
                "Copies of drivers that are already installed, plus GeForce NOW installer leftovers.", Nvidia),
            new("updaters", RuleGroup.Leftovers, "App update installers",
                "Installers apps downloaded for updates. They fetch a fresh one when needed.", Updaters),
            new("old-versions", RuleGroup.Leftovers, "Old app versions",
                "Previous versions Discord, Wand and similar apps keep after updating.", Squirrel),
            new("claude-versions", RuleGroup.Leftovers, "Old Claude Code versions",
                "Earlier Claude Code builds. The newest and any running one stay.", ClaudeVersions),
            new("codex", RuleGroup.Leftovers, "Old Codex runtimes",
                "Superseded Codex runtimes, an older CLI release and Codex's temp folder.", Codex),
            new("pkg-caches", RuleGroup.Leftovers, "Package download caches",
                "npm, pip, uv and Comfy Desktop download caches. They refill on the next install.", PkgCaches),
        };
        rules.AddRange(AppCaches.Apps.Select(AppCaches.AppRule));
        rules.AddRange(AppCaches.Launchers.Select(AppCaches.LauncherRule));
        rules.AddRange(
        [
            new("temp", RuleGroup.Leftovers, "Old temp files",
                "Temp files and crash dumps untouched for a week, plus old agent and LibreOffice scratch.", TempFiles),
            new("partial", RuleGroup.Leftovers, "Unfinished downloads",
                "Partial downloads (.crdownload, .part) left in Downloads or Desktop for 2+ days.", Partial),
            new("node-modules", RuleGroup.Leftovers, "Idle node_modules",
                "Dependencies of projects untouched for 30 days. npm install brings them back.", NodeModules),
            new("recycle", RuleGroup.YourCall, "Recycle Bin", "Empties it for good.", Recycle),
            new("win-temp", RuleGroup.Windows, "Windows temp and update leftovers",
                "Windows Temp, old Windows Update downloads and error reports.", WinTemp, admin: true),
            new("delivery-opt", RuleGroup.Windows, "Delivery Optimization cache",
                "Update pieces Windows keeps to share with other PCs.", DeliveryOpt, admin: true),
            new("dism", RuleGroup.Windows, "Windows component store",
                "Removes superseded Windows update files. Slow; run now and then.", ComponentStore, isChecked: false, admin: true),
        ]);
        return rules;
    }

    // ------------------------------------------------------------------ leftovers

    internal static IEnumerable<Item> Updaters(ScanContext ctx, Rule r)
    {
        foreach (string d in G(Local, "*-updater"))
        {
            if (Directory.Exists(d))
            {
                yield return new Item(d);
            }
        }
    }

    internal static IEnumerable<Item> Nvidia(ScanContext ctx, Rule r)
    {
        // NVIDIA app: installers in grd\<id>\<version>-....exe, unpacked copies in grd\post-processing\<id>
        string grd = Path.Combine(PD, "NVIDIA Corporation", "NVIDIA App", "UpdateFramework", "ota-artifacts", "grd");
        string pp = Path.Combine(grd, "post-processing");
        if (Directory.Exists(grd))
        {
            VersionKey? inst = ctx.NvidiaDriver;
            foreach (string d in G(grd, "*"))
            {
                if (!Directory.Exists(d) || SamePath(d, pp))
                {
                    continue;
                }
                string? exe = G(d, "*.exe").FirstOrDefault();
                VersionKey? ver = null;
                if (exe is not null)
                {
                    string name = Path.GetFileName(exe);
                    Match m = LeadingVersion().Match(name);
                    if (m.Success && char.IsDigit(name[0]))
                    {
                        ver = VersionKey.Parse(m.Value);
                    }
                }
                if (inst is null || ver is null)
                {
                    r.Skipped.Add(new Skip(d, "could not compare with the installed driver"));
                }
                else if (ver <= inst)
                {
                    yield return new Item(d, $"Driver {ver} installer (installed: {inst})");
                    string unpacked = Path.Combine(pp, Path.GetFileName(d));
                    if (Directory.Exists(unpacked))
                    {
                        yield return new Item(unpacked, "  its unpacked copy");
                    }
                }
                else
                {
                    r.Skipped.Add(new Skip(d, "newer than the installed driver, may be a pending update"));
                }
            }
            foreach (string d in G(pp, "*"))
            {
                if (Directory.Exists(d) && !Directory.Exists(Path.Combine(grd, Path.GetFileName(d))) && IdleDays(d) > 7)
                {
                    yield return new Item(d, "Unpacked driver with no installer left");
                }
            }
        }

        // Added in the C# port: a driver downloaded from nvidia.com unpacks itself to
        // C:\NVIDIA\DisplayDriver\<version> and leaves the copy behind. The files are only used
        // during setup (https://xjdhdr.gitlab.io/tweakguides/system_guides/NVFORCE/NVFORCE_4.xhtml);
        // running the downloaded installer again recreates them. Same version gate as above.
        string manual = Path.Combine(KnownPaths.SystemDrive, "NVIDIA", "DisplayDriver");
        if (Directory.Exists(manual))
        {
            VersionKey? inst = ctx.NvidiaDriver;
            foreach (string d in G(manual, "*"))
            {
                string name = Path.GetFileName(d);
                if (!Directory.Exists(d) || !VersionName().IsMatch(name))
                {
                    continue;
                }
                VersionKey ver = VersionKey.Parse(name);
                if (inst is null)
                {
                    r.Skipped.Add(new Skip(d, "could not compare with the installed driver"));
                }
                else if (ver > inst)
                {
                    r.Skipped.Add(new Skip(d, "newer than the installed driver, may be a pending update"));
                }
                else if (IdleDays(d) > 1)
                {
                    yield return new Item(d, $"Unpacked driver {ver} from a manual install (installed: {inst})");
                }
            }
        }

        foreach (string n in new[] { "GeforceNowInstallerFiles", "GeForceNOW_tempNew" })
        {
            string d = Path.Combine(Local, "NVIDIA Corporation", n);
            if (Directory.Exists(d) && IdleDays(d) > 3)
            {
                yield return new Item(d);
            }
        }
    }

    /// <summary>Old app-x.y.z versions and update packages kept by Squirrel apps (Discord, Wand...).</summary>
    internal static IEnumerable<Item> Squirrel(ScanContext ctx, Rule r)
    {
        foreach (string upd in G(Local, "*", "Update.exe"))
        {
            string root = Path.GetDirectoryName(upd)!;
            string rootName = Path.GetFileName(root);
            List<string> apps = G(root, "app-*").Where(Directory.Exists)
                .OrderBy(d => VersionKey.Parse(Path.GetFileName(d))).ToList();
            List<string> old = apps.Take(Math.Max(0, apps.Count - 1)).ToList();
            List<string> pkgs = G(root, "packages", "*.nupkg").Where(p => IdleDays(p) > 3).ToList();
            if (old.Count == 0 && pkgs.Count == 0)
            {
                continue;
            }
            if (ctx.Running(dirs: [root]))
            {
                r.Skipped.Add(new Skip(root, $"{rootName} is running"));
                continue;
            }
            foreach (string d in old)
            {
                yield return new Item(d, $"{rootName} {Path.GetFileName(d)} (current: {Path.GetFileName(apps[^1])})");
            }
            foreach (string p in pkgs)
            {
                yield return new Item(p, $"{rootName} update package {Path.GetFileName(p)}");
            }
        }
    }

    /// <summary>Version-named entries in <paramref name="baseDir"/> except the newest and <paramref name="keepExtra"/>.</summary>
    internal static List<string> OldVersions(string baseDir, ICollection<string>? keepExtra = null)
    {
        List<string> vers = G(baseDir, "*").Where(d => VersionName().IsMatch(Path.GetFileName(d))).ToList();
        if (vers.Count < 2)
        {
            return [];
        }
        string newest = vers.MaxBy(d => VersionKey.Parse(Path.GetFileName(d)))!;
        return vers.Where(d => d != newest && !(keepExtra?.Contains(Path.GetFileName(d)) ?? false)).ToList();
    }

    internal static IEnumerable<Item> ClaudeVersions(ScanContext ctx, Rule r)
    {
        foreach (string b in G(Local, "Claude-Data", "*", "claude-code").Concat(G(Local, "Claude-Data", "*", "claude-code-vm")))
        {
            string sdk = "";
            try
            {
                string f = Path.Combine(b, ".sdk-version");
                if (File.Exists(f))
                {
                    sdk = File.ReadAllText(f).Trim();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            foreach (string d in OldVersions(b, [sdk]))
            {
                if (ctx.Running(dirs: [d]))
                {
                    r.Skipped.Add(new Skip(d, "this version is running"));
                }
                else
                {
                    yield return new Item(d, $"Claude Code {Path.GetFileName(d)} (desktop app copy)");
                }
            }
        }
        string cli = Path.Combine(Home, ".local", "share", "claude", "versions");
        foreach (string f in OldVersions(cli))
        {
            if (IdleDays(f) > 7)
            {
                yield return new Item(f, $"Claude CLI {Path.GetFileName(f)}");
            }
        }
    }

    internal static IEnumerable<Item> Codex(ScanContext ctx, Rule r)
    {
        string cr = Path.Combine(Home, ".cache", "codex-runtimes");
        foreach (string d in G(cr, "*"))
        {
            string n = Path.GetFileName(d);
            if (n.Contains(".previous-", StringComparison.Ordinal) ||
                (n.StartsWith("codex-runtime-install-", StringComparison.Ordinal) && IdleDays(d) > 1))
            {
                yield return new Item(d);
            }
        }
        string rel = Path.Combine(Home, ".codex", "packages", "standalone", "releases");
        string cur = Path.Combine(Home, ".codex", "packages", "standalone", "current");
        // Stricter than the reference: only when "current" is a link can the live release be told apart.
        if (Exists(cur) && IsLink(cur) && ResolveFinal(cur) is string live)
        {
            foreach (string d in G(rel, "*"))
            {
                if (!SamePath(d, live))
                {
                    yield return new Item(d, $"Codex CLI {Path.GetFileName(d)}");
                }
            }
        }
        string tmp = Path.Combine(Home, ".codex", ".tmp");
        if (Directory.Exists(tmp))
        {
            if (ctx.Running(names: ["codex.exe"]))
            {
                r.Skipped.Add(new Skip(tmp, "Codex is running"));
            }
            else
            {
                yield return new Item(tmp);
            }
        }
    }

    internal static IEnumerable<Item> PkgCaches(ScanContext ctx, Rule r)
    {
        foreach (string p in new[]
        {
            Path.Combine(Local, "npm-cache", "_cacache"), Path.Combine(Local, "npm-cache", "_logs"),
            Path.Combine(Local, "pip", "Cache"), Path.Combine(Local, "Yarn", "Cache"),
        })
        {
            if (Directory.Exists(p))
            {
                yield return new Item(p);
            }
        }
        foreach (string d in G(Local, "npm-cache", "_npx", "*"))
        {
            if (IdleDays(d) > 14)
            {
                yield return new Item(d);
            }
        }
        string uv = Path.Combine(Local, "uv", "cache");
        if (Directory.Exists(uv))
        {
            if (ctx.Running(names: ["uv.exe"]))
            {
                r.Skipped.Add(new Skip(uv, "uv is running"));
            }
            else
            {
                yield return new Item(uv);
            }
        }
        string comfy = Path.Combine(Local, "Comfy-Desktop", "ComfyUI-Cache", "download-cache");
        if (Directory.Exists(comfy) && IdleDays(comfy) > 3)
        {
            yield return new Item(comfy, "Comfy Desktop download cache");
        }
    }

    internal static IEnumerable<Item> TempFiles(ScanContext ctx, Rule r)
    {
        foreach (string e in G(Temp, "*"))
        {
            if (Path.GetFileName(e).Equals("claude", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // Stricter than the reference: a folder counts as idle only when nothing inside it
            // changed for 7 days either (one walk gives both the age and the size).
            if (IdleDays(e) <= 7)
            {
                continue;
            }
            (long size, DateTime newest) = TreeStats(e);
            if (DaysSince(newest) > 7)
            {
                yield return new Item(e, size: size);
            }
        }
        foreach (string d in G(Temp, "claude", "*", "*"))
        {
            if (Directory.Exists(d) && IdleDays(d, deep: true) > 7)
            {
                string n = Path.GetFileName(d);
                yield return new Item(d, $"old agent session scratch {n[..Math.Min(8, n.Length)]}");
            }
        }
        foreach (string d in G(KnownPaths.DriveTemp, "lo_*"))
        {
            if (IdleDays(d) > 1)
            {
                yield return new Item(d, $"LibreOffice scratch profile {Path.GetFileName(d)}");
            }
        }
        foreach (string f in G(Local, "CrashDumps", "*"))
        {
            if (IdleDays(f) > 1)
            {
                yield return new Item(f);
            }
        }
    }

    private static readonly string[] WalkSkip = [".git", "node_modules", "AppData", ".venv", "venv"];

    /// <summary>Every folder below the roots, never through links, not descending into <paramref name="skipNames"/>.</summary>
    internal static IEnumerable<DirectoryInfo> WalkDirs(IEnumerable<string> roots, string[]? skipNames = null)
    {
        skipNames ??= WalkSkip;
        foreach (string root in roots)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string d = stack.Pop();
                List<DirectoryInfo> subs;
                try
                {
                    subs = new DirectoryInfo(d).EnumerateDirectories("*", DirOptions).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (DirectoryInfo e in subs)
                {
                    yield return e;
                    if (!skipNames.Contains(e.Name, StringComparer.Ordinal))
                    {
                        stack.Push(e.FullName);
                    }
                }
            }
        }
    }

    internal static string[] ProjectRoots =>
        new[] { "Downloads", "Documents", "Desktop", "source" }.Select(n => Path.Combine(Home, n)).ToArray();

    internal static IEnumerable<Item> NodeModules(ScanContext ctx, Rule r)
    {
        string codexDocs = Path.Combine(Home, "Documents", "Codex");
        foreach (DirectoryInfo e in WalkDirs(ProjectRoots))
        {
            if (e.Name != "node_modules" || e.Parent is null)
            {
                continue;
            }
            string parent = e.Parent.FullName;
            if (Under(e.FullName, codexDocs))
            {
                continue;
            }
            if (Path.GetFileName(Path.GetDirectoryName(parent)) == "node_modules" || !File.Exists(Path.Combine(parent, "package.json")))
            {
                continue;
            }
            var stamps = new List<DateTime> { MTimeUtc(e.FullName), MTimeUtc(Path.Combine(e.FullName, ".package-lock.json")) };
            try
            {
                stamps.AddRange(new DirectoryInfo(parent).EnumerateFiles("*", DirOptions).Select(f => f.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stamps.Add(DateTime.UtcNow);
            }
            if (DaysSince(stamps.Max()) > 30)
            {
                yield return new Item(e.FullName, Path.GetRelativePath(Home, e.FullName));
            }
        }
    }

    private static readonly string[] PartialEnds = [".crdownload", ".part", ".partial", ".opdownload"];

    internal static IEnumerable<Item> Partial(ScanContext ctx, Rule r)
    {
        foreach (string root in new[] { KnownPaths.Downloads, KnownPaths.Desktop })
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string d = stack.Pop();
                List<FileSystemInfo> entries;
                try
                {
                    entries = new DirectoryInfo(d).EnumerateFileSystemInfos("*", DirOptions).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (FileSystemInfo e in entries)
                {
                    if (e is DirectoryInfo)
                    {
                        if (e.Name != "node_modules")
                        {
                            stack.Push(e.FullName);
                        }
                    }
                    else if (PartialEnds.Any(x => e.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase)) && IdleDays(e.FullName) > 2)
                    {
                        yield return new Item(e.FullName, Path.GetRelativePath(Home, e.FullName));
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ your call

    internal static IEnumerable<Item> Recycle(ScanContext ctx, Rule r)
    {
        if (NativeMethods.QueryRecycleBin() is (long bytes, long items) && items > 0)
        {
            yield return new Item($"Recycle Bin ({items} items)", NativeMethods.EmptyRecycleBin, bytes);
        }
    }

    // ------------------------------------------------------------------ windows (admin)

    internal static IEnumerable<Item> WinTemp(ScanContext ctx, Rule r)
    {
        foreach ((string baseDir, int days, bool deep) in new[]
        {
            (Path.Combine(Win, "Temp"), 7, true),
            (Path.Combine(Win, "SoftwareDistribution", "Download"), 10, false),
            (Path.Combine(PD, "Microsoft", "Windows", "WER", "ReportArchive"), 7, false),
            (Path.Combine(PD, "Microsoft", "Windows", "WER", "ReportQueue"), 7, false),
        })
        {
            foreach (string e in G(baseDir, "*"))
            {
                if (IdleDays(e) > days && (!deep || IdleDays(e, deep: true) > days))
                {
                    yield return new Item(e);
                }
            }
        }
    }

    internal static IEnumerable<Item> DeliveryOpt(ScanContext ctx, Rule r)
    {
        string d = Path.Combine(Win, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows",
            "DeliveryOptimization", "Cache");
        long size = TreeSize(d);
        if (size > 0)
        {
            yield return new Item("Delivery Optimization cache",
                () => Tools.PowerShell("Delete-DeliveryOptimizationCache -Force", TimeSpan.FromMinutes(30)), size);
        }
    }

    internal static IEnumerable<Item> ComponentStore(ScanContext ctx, Rule r)
    {
        yield return new Item("Windows component store cleanup (DISM, takes 10+ minutes)", () =>
        {
            (int code, string output) = Tools.RunHidden(Tools.System32("dism.exe"),
                "/Online /Cleanup-Image /StartComponentCleanup", TimeSpan.FromMinutes(60));
            if (code != 0)
            {
                throw new InvalidOperationException($"DISM exited with code {code}.");
            }
        }, null);
    }

    // ------------------------------------------------------------------ helpers

    private static string? ResolveFinal(string link)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(link) ? new DirectoryInfo(link) : new FileInfo(link);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[\d.]+")]
    private static partial Regex LeadingVersion();

    [GeneratedRegex(@"^\d+(\.\d+)+$")]
    private static partial Regex VersionName();
}
