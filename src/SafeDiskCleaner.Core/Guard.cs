using Microsoft.Win32;

namespace SafeDiskCleaner.Core;

/// <summary>
/// Every delete goes through <see cref="Check"/>. It refuses protected paths, top-level folders,
/// anything outside the allowed roots, anything reached through a junction or symlink, and any
/// folder that holds an installed program, a PATH entry or a WinGet link target.
/// Deleting %LOCALAPPDATA%\Microsoft\WinGet\Packages once broke ffmpeg, yt-dlp, ripgrep, jq,
/// Deno, uv and Poppler; this class exists so that class of mistake cannot happen.
/// </summary>
public sealed class Guard
{
    public const string GoneAlready = "gone already";
    public const string TopLevel = "top-level folder";
    public const string Outside = "outside the folders this tool may touch";
    public const string ThroughLink = "reached through a junction or link";
    public const string ThroughSpecial = "reached through a cloud or special folder";
    public const string InvalidPath = "not a valid path";

    private readonly string[] _allowed;
    private readonly string[] _never;
    private readonly string[] _protected;
    private readonly string[] _inUse;
    private readonly (string Dir, System.Text.RegularExpressions.Regex[] Names)[] _allowedFiles;

    public Guard(GuardConfig config)
    {
        _allowed = NormAll(config.AllowedRoots);
        _allowedFiles = config.AllowedFiles
            .Select(f => (Dir: PathUtil.TryNorm(f.Dir), f.NamePatterns))
            .Where(f => f.Dir is not null)
            .Select(f => (f.Dir!, f.NamePatterns.Select(NamePattern).ToArray()))
            .ToArray();
        _never = NormAll(config.NeverExact);
        _protected = NormAll(config.Protected);
        // folders an installed program or a PATH entry lives in, minus the top-level folders
        _inUse = NormAll(config.InUse)
            .Where(p => !_never.Any(r => r.Equals(p, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    /// <summary>The guard for this PC: reference rules plus registry, PATH, WinGet links and protect.txt.</summary>
    public static Guard CreateDefault() => new(GuardConfig.FromSystem());

    public IReadOnlyList<string> AllowedRoots => _allowed;

    public IReadOnlyList<string> ProtectedPaths => _protected;

    public IReadOnlyList<string> InUsePaths => _inUse;

    /// <summary>Returns null when <paramref name="path"/> may be deleted, else the reason it may not.</summary>
    public string? Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return InvalidPath;
        }
        if (!PathUtil.Exists(path))
        {
            return GoneAlready;
        }
        string? p = PathUtil.TryNorm(path);
        if (p is null)
        {
            return InvalidPath;
        }
        if (_never.Any(r => r.Equals(p, StringComparison.OrdinalIgnoreCase)))
        {
            return TopLevel;
        }
        string? root = _allowed.FirstOrDefault(r => PathUtil.UnderNormalized(p, r));
        if (root is null && IsAllowedFile(p) && PathUtil.TryGetAttributes(p) is FileAttributes fa &&
            (fa & FileAttributes.Directory) == 0)
        {
            // a loose file Disk Cleanup also clears, directly inside its folder (C:\Windows\MEMORY.DMP)
            root = Path.GetDirectoryName(p);
        }
        if (root is null)
        {
            return Outside;
        }

        // no junction or symlink anywhere between the allowed root and the target (target included)
        string? cur = p;
        while (cur is not null && !cur.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            FileAttributes? a = PathUtil.TryGetAttributes(cur);
            if (a is null)
            {
                return GoneAlready;
            }
            if ((a.Value & FileAttributes.ReparsePoint) != 0)
            {
                return PathUtil.IsLinkReparsePoint(cur, a.Value) ? ThroughLink : ThroughSpecial;
            }
            cur = Path.GetDirectoryName(cur);
        }
        if (cur is null)
        {
            return Outside;
        }

        foreach (string prot in _protected)
        {
            if (PathUtil.UnderNormalized(p, prot) || PathUtil.UnderNormalized(prot, p))
            {
                return $"protected ({prot})";
            }
        }
        foreach (string used in _inUse)
        {
            if (PathUtil.UnderNormalized(used, p))
            {
                return $"holds an installed program or PATH entry ({used})";
            }
        }
        return null;
    }

    /// <summary>
    /// True for a file name that ALLOWED_FILES lists, sitting directly in its folder. The pattern
    /// is matched against the file name only, never the full path, so C:\Windows\*.dmp does not
    /// reach C:\Windows\Minidump\x.dmp.
    /// </summary>
    public bool IsAllowedFile(string path)
    {
        string? p = PathUtil.TryNorm(path);
        if (p is null || Path.GetDirectoryName(p) is not string dir)
        {
            return false;
        }
        string name = Path.GetFileName(p);
        return _allowedFiles.Any(f => f.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase) && f.Names.Any(rx => rx.IsMatch(name)));
    }

    private static System.Text.RegularExpressions.Regex NamePattern(string pattern) => new(
        "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string[] NormAll(IEnumerable<string> paths) => paths
        .Select(PathUtil.TryNorm)
        .Where(p => p is not null)
        .Select(p => p!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

/// <summary>The lists a <see cref="Guard"/> works from. Tests build their own from temp folders.</summary>
public sealed class GuardConfig
{
    public required IReadOnlyList<string> AllowedRoots { get; init; }

    /// <summary>Folders that are never deleted themselves (NEVER_EXACT), though things inside them may be.</summary>
    public required IReadOnlyList<string> NeverExact { get; init; }

    /// <summary>Never deleted, nor anything inside them, nor any folder containing them.</summary>
    public required IReadOnlyList<string> Protected { get; init; }

    /// <summary>Install locations, PATH folders and WinGet link targets: no folder containing one is deleted.</summary>
    public required IReadOnlyList<string> InUse { get; init; }

    /// <summary>ALLOWED_FILES: loose files allowed directly inside a folder, by file-name pattern.</summary>
    public IReadOnlyList<(string Dir, string[] NamePatterns)> AllowedFiles { get; init; } = [];

    public static GuardConfig FromSystem(string? protectFile = null)
    {
        var protectedPaths = GuardDefaults.ProtectedPaths();
        protectedPaths.AddRange(UserProtection.Read(protectFile));
        var inUse = new List<string>();
        inUse.AddRange(GuardSources.InstallLocations());
        inUse.AddRange(GuardSources.PathDirs());
        inUse.AddRange(GuardSources.WinGetLinkTargets());
        return new GuardConfig
        {
            AllowedRoots = GuardDefaults.AllowedRoots(),
            AllowedFiles = GuardDefaults.AllowedFiles(),
            NeverExact = GuardDefaults.NeverExact(),
            Protected = protectedPaths,
            InUse = inUse,
        };
    }
}

/// <summary>PROTECTED_PATTERNS, ALLOWED_ROOTS and NEVER_EXACT from the reference.</summary>
public static class GuardDefaults
{
    public static readonly string[] ProtectedPatterns =
    [
        // local AI models and runtimes
        @"{L}\Claude-Data\*\vm_bundles", @"{H}\.ollama", @"{H}\.cache\huggingface", @"{H}\.lmstudio",
        @"{L}\ms-playwright", @"{R}\Python",
        // agent tools' own state
        @"{H}\.claude\skills", @"{H}\.claude\hooks", @"{H}\.claude\projects\*\memory",
        @"{H}\.codex\skills", @"{H}\.codex\plugins", @"{H}\Documents\Codex",
        // synced and personal folders
        @"{H}\CrossDevice", @"{H}\OneDrive", @"{H}\OneDrive *", @"{H}\.ssh", @"{H}\.gnupg",
        // installed tools and installer databases
        @"{L}\Microsoft\WinGet", @"{L}\Programs", @"{L}\Package Cache", @"{P}\Package Cache", @"{W}\Installer",
        // crash dumps, NVIDIA shader caches, Chrome's on-device AI model. The DirectX shader
        // cache is an opt-in rule instead, as in Disk Cleanup.
        @"{W}\Minidump", @"{W}\LiveKernelReports", @"{L}\NVIDIA", @"{H}\AppData\LocalLow\NVIDIA",
        @"{L}\Google\Chrome\User Data\OptGuideOnDeviceModel",
        // added in the C# port: AMD's shader caches (same reason as NVIDIA's), personal media
        // and game saves, and this app's own settings and log
        @"{L}\AMD", @"{H}\Pictures", @"{H}\Music", @"{H}\Videos", @"{H}\Saved Games",
        @"{H}\Documents\My Games", @"{H}\Favorites", @"{H}\Contacts",
        @"{R}\SafeDiskCleaner", @"{L}\SafeDiskCleaner",
    ];

    public static List<string> ProtectedPaths()
    {
        var list = new List<string>();
        foreach (string pat in ProtectedPatterns)
        {
            string expanded = KnownPaths.Expand(pat);
            if (pat.Contains('*'))
            {
                list.AddRange(PathUtil.Glob(expanded));
            }
            else
            {
                list.Add(expanded);
            }
        }
        // the real (possibly redirected) media folders, and the folder this app runs from
        foreach (var f in new[] { Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos })
        {
            string path = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(path))
            {
                list.Add(path);
            }
        }
        list.Add(AppContext.BaseDirectory);
        return list;
    }

    /// <summary>C:\FOUND.000 to C:\FOUND.009: fragments an old ChkDsk run saved.</summary>
    public static List<string> FoundDirs() =>
        Enumerable.Range(0, 10).Select(i => Path.Combine(KnownPaths.SystemDrive, $"FOUND.{i:000}")).ToList();

    public static List<string> AllowedRoots()
    {
        string w = KnownPaths.Windows, pd = KnownPaths.ProgramData;
        var roots = new List<string>
        {
            KnownPaths.Home,
            Path.Combine(pd, "NVIDIA Corporation"),
            KnownPaths.DriveTemp,
            Path.Combine(w, "Temp"),
            Path.Combine(w, "SoftwareDistribution", "Download"),
            Path.Combine(pd, "Microsoft", "Windows", "WER"),
            // added in the C# port: where a manually downloaded NVIDIA driver unpacks itself
            Path.Combine(KnownPaths.SystemDrive, "NVIDIA"),
            // folders Windows Disk Cleanup also clears
            Path.Combine(w, "Logs"),
            Path.Combine(w, "System32", "LogFiles"),
            Path.Combine(w, "SystemTemp"),
            Path.Combine(pd, "Microsoft", "WDF"),
            Path.Combine(pd, "Microsoft", "Windows", "HiveUpload"),
            Path.Combine(pd, "Microsoft", "Windows Defender", "LocalCopy"),
            Path.Combine(pd, "Microsoft", "Windows Defender", "Support"),
            Path.Combine(pd, "Microsoft", "Diagnosis", "FeedbackArchive"),
            Path.Combine(pd, "Microsoft", "Diagnosis", "EventTranscript"),
            Path.Combine(pd, "Microsoft", "Windows", "RetailDemo"),
        };
        roots.AddRange(FoundDirs());
        return roots;
    }

    /// <summary>Loose files directly inside the Windows folder that Disk Cleanup also clears, never in its subfolders.</summary>
    public static readonly string[] WindowsFilePatterns = ["*.dmp", "setup*.log", "setup*.old", "setuplog.txt", "winnt32.log"];

    public static List<(string Dir, string[] NamePatterns)> AllowedFiles() => [(KnownPaths.Windows, WindowsFilePatterns)];

    public static List<string> NeverExact()
    {
        string h = KnownPaths.Home;
        var list = new List<string>
        {
            h, KnownPaths.Local, KnownPaths.Roaming, KnownPaths.Temp, Path.Combine(h, "AppData"),
            KnownPaths.Downloads, KnownPaths.Documents, KnownPaths.Desktop, Path.Combine(h, "source"),
            KnownPaths.ProgramData, KnownPaths.Windows, KnownPaths.DriveTemp, KnownPaths.SystemDrive,
            // added in the C# port: more containers that must never go as a whole
            KnownPaths.LocalLow, Path.Combine(h, ".cache"), Path.Combine(h, ".local"), Path.Combine(h, ".config"),
            Path.Combine(KnownPaths.Local, "Packages"), Path.Combine(KnownPaths.Local, "Microsoft"),
            Path.Combine(KnownPaths.Roaming, "Microsoft"), Path.Combine(KnownPaths.SystemDrive, "NVIDIA", "DisplayDriver"),
            KnownPaths.ProgramFiles,
        };
        list.AddRange(AllowedRoots());
        return list;
    }
}

/// <summary>Where the "in use" folders come from: the registry, PATH and WinGet's Links folder.</summary>
public static class GuardSources
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallKeyWow = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>InstallLocation of every uninstall entry in HKCU, HKLM and HKLM\WOW6432Node.</summary>
    public static List<string> InstallLocations()
    {
        var locs = new List<string>();
        foreach ((RegistryHive hive, string key) in new[]
        {
            (RegistryHive.CurrentUser, UninstallKey),
            (RegistryHive.LocalMachine, UninstallKey),
            (RegistryHive.LocalMachine, UninstallKeyWow),
        })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using RegistryKey? k = baseKey.OpenSubKey(key);
                if (k is null)
                {
                    continue;
                }
                foreach (string sub in k.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? s = k.OpenSubKey(sub);
                        if (s?.GetValue("InstallLocation") is string loc && loc.Trim('"', '\\', ' ').Length > 3)
                        {
                            locs.Add(Environment.ExpandEnvironmentVariables(loc.Trim('"', ' ')));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }
        return locs;
    }

    /// <summary>Every folder on the process, user and machine PATH.</summary>
    public static List<string> PathDirs()
    {
        var raw = new List<string> { Environment.GetEnvironmentVariable("PATH") ?? "" };
        raw.Add(ReadValue(RegistryHive.CurrentUser, "Environment", "Path"));
        raw.Add(ReadValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "Path"));
        return raw
            .SelectMany(r => r.Split(';'))
            .Select(p => p.Trim('"', ' '))
            .Where(p => p.Length > 3)
            .Select(Environment.ExpandEnvironmentVariables)
            .ToList();
    }

    /// <summary>Folders that the links in WinGet's Links folder point into.</summary>
    public static List<string> WinGetLinkTargets() =>
        LinkTargets(Path.Combine(KnownPaths.Local, "Microsoft", "WinGet", "Links"));

    /// <summary>For each symbolic link directly inside <paramref name="folder"/>, the folder holding its final target.</summary>
    public static List<string> LinkTargets(string folder)
    {
        var outList = new List<string>();
        if (!Directory.Exists(folder))
        {
            return outList;
        }
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(folder, "*", PathUtil.AllOptions).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return outList;
        }
        foreach (string f in entries)
        {
            try
            {
                var info = new FileInfo(f);
                if (info.LinkTarget is not string raw)
                {
                    continue;
                }
                string target;
                try
                {
                    target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? raw;
                }
                catch (IOException)
                {
                    target = raw;
                }
                if (!Path.IsPathRooted(target))
                {
                    target = Path.GetFullPath(Path.Combine(folder, target));
                }
                string? dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir))
                {
                    outList.Add(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
        return outList;
    }

    private static string ReadValue(RegistryHive hive, string key, string name)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? k = baseKey.OpenSubKey(key);
            return k?.GetValue(name) as string ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "";
        }
    }
}
