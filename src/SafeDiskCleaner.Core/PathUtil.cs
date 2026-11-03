namespace SafeDiskCleaner.Core;

/// <summary>Path helpers: nc/under/is_link/tree_size/mtime/idle_days/glob from reference/engine.py.</summary>
public static class PathUtil
{
    private static readonly char[] Wild = ['*', '?'];

    /// <summary>Size walk: never follows links or cloud placeholders, skips folders it cannot read.</summary>
    internal static readonly EnumerationOptions SizeOptions = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
    };

    /// <summary>One level, no links, hidden and system entries included.</summary>
    internal static readonly EnumerationOptions DirOptions = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
    };

    /// <summary>One level, everything (links included), for listing children.</summary>
    internal static readonly EnumerationOptions AllOptions = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
    };

    private static readonly EnumerationOptions GlobOptions = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    /// <summary>
    /// Normalises a path for comparison (nc in the reference): absolute, no \\?\ prefix,
    /// 8.3 short names expanded, no trailing separator except on a drive root.
    /// </summary>
    public static string Norm(string path)
    {
        string p = path.Trim().Trim('"').Trim();
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            p = @"\\" + p[8..];
        }
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            p = p[4..];
        }
        p = Path.GetFullPath(p);
        if (p.Contains('~'))
        {
            p = NativeMethods.TryGetLongPath(p) ?? p;
        }
        if (p.Length > 3)
        {
            p = p.TrimEnd('\\', '/');
        }
        return p;
    }

    /// <summary>Norm that returns null instead of throwing on a malformed path.</summary>
    public static string? TryNorm(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return Norm(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies inside it. Both must be normalised.</summary>
    internal static bool UnderNormalized(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string prefix = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>under(p, root) from the reference: p equals root or is inside it.</summary>
    public static bool Under(string path, string root)
    {
        string? p = TryNorm(path), r = TryNorm(root);
        return p is not null && r is not null && UnderNormalized(p, r);
    }

    public static bool SamePath(string a, string b)
    {
        string? x = TryNorm(a), y = TryNorm(b);
        return x is not null && y is not null && x.Equals(y, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>lexists: true for files, folders and links, even broken ones.</summary>
    public static bool Exists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>True for a junction or symbolic link (file or folder).</summary>
    public static bool IsLink(string path)
    {
        FileAttributes? a = TryGetAttributes(path);
        if (a is null || (a.Value & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }
        return IsLinkReparsePoint(path, a.Value);
    }

    /// <summary>For an entry known to be a reparse point: is it a junction or symlink (as opposed to a cloud file, dedup file and so on)?</summary>
    internal static bool IsLinkReparsePoint(string path, FileAttributes attributes)
    {
        try
        {
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Unreadable reparse data: the caller treats it as a link, which is the cautious answer.
            return true;
        }
    }

    /// <summary>Total bytes of a file or folder tree. Links and placeholders are not followed or counted.</summary>
    public static long TreeSize(string path)
    {
        FileAttributes? a = TryGetAttributes(path);
        if (a is null)
        {
            return 0;
        }
        if ((a.Value & FileAttributes.Directory) == 0)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
        if ((a.Value & FileAttributes.ReparsePoint) != 0)
        {
            return 0;
        }
        long total = 0;
        try
        {
            foreach (FileInfo f in new DirectoryInfo(path).EnumerateFiles("*", SizeOptions))
            {
                total += f.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Partial total is fine for an estimate; freed space is measured on the drive.
        }
        return total;
    }

    /// <summary>Size of a tree plus the newest write time of anything in it (the entry itself included).</summary>
    public static (long Size, DateTime NewestUtc) TreeStats(string path)
    {
        DateTime newest = MTimeUtc(path);
        FileAttributes? a = TryGetAttributes(path);
        if (a is null)
        {
            return (0, DateTime.UtcNow);
        }
        if ((a.Value & FileAttributes.Directory) == 0 || (a.Value & FileAttributes.ReparsePoint) != 0)
        {
            return (TreeSize(path), newest);
        }
        long total = 0;
        try
        {
            foreach (FileSystemInfo e in new DirectoryInfo(path).EnumerateFileSystemInfos("*", SizeOptions))
            {
                DateTime t = e.LastWriteTimeUtc;
                if (t > newest)
                {
                    newest = t;
                }
                if (e is FileInfo f)
                {
                    total += f.Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Treat an unreadable tree as recently used so it is never picked by an age rule.
            newest = DateTime.UtcNow;
        }
        return (total, newest);
    }

    /// <summary>Last write time of the entry itself; "now" when it cannot be read, so age rules leave it alone.</summary>
    public static DateTime MTimeUtc(string path)
    {
        try
        {
            File.GetAttributes(path);
            DateTime t = File.GetLastWriteTimeUtc(path);
            return t.Year <= 1601 ? DateTime.UtcNow : t;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return DateTime.UtcNow;
        }
    }

    public static double DaysSince(DateTime utc) => (DateTime.UtcNow - utc).TotalDays;

    /// <summary>Days since the path (or, with deep, anything inside it) last changed.</summary>
    public static double IdleDays(string path, bool deep = false) =>
        DaysSince(deep ? TreeStats(path).NewestUtc : MTimeUtc(path));

    /// <summary>
    /// A small glob for rule tables: '*' and '?' inside path segments only. Like Python's glob,
    /// a wildcard does not match names starting with '.' unless the pattern segment does.
    /// Returns full paths, sorted.
    /// </summary>
    public static List<string> Glob(string pattern)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return results;
        }
        string[] parts = pattern.Split('\\');
        int first = Array.FindIndex(parts, s => s.IndexOfAny(Wild) >= 0);
        if (first < 0)
        {
            if (Exists(pattern))
            {
                results.Add(pattern);
            }
            return results;
        }
        if (first == 0)
        {
            return results;
        }
        string baseDir = string.Join('\\', parts[..first]);
        if (baseDir.EndsWith(':'))
        {
            baseDir += "\\";
        }
        if (Directory.Exists(baseDir))
        {
            ExpandGlob(baseDir, parts, first, results);
        }
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    /// <summary>Glob(Path.Combine(parts)).</summary>
    public static List<string> G(params string[] parts) => Glob(Path.Combine(parts));

    private static void ExpandGlob(string dir, string[] parts, int i, List<string> results)
    {
        if (i == parts.Length)
        {
            results.Add(dir);
            return;
        }
        string seg = parts[i];
        bool last = i == parts.Length - 1;
        if (seg.Length == 0)
        {
            ExpandGlob(dir, parts, i + 1, results);
            return;
        }
        if (seg.IndexOfAny(Wild) < 0)
        {
            string next = Path.Combine(dir, seg);
            if (last ? Exists(next) : Directory.Exists(next))
            {
                ExpandGlob(next, parts, i + 1, results);
            }
            return;
        }
        List<string> entries;
        try
        {
            entries = (last
                ? Directory.EnumerateFileSystemEntries(dir, seg, GlobOptions)
                : Directory.EnumerateDirectories(dir, seg, GlobOptions)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }
        foreach (string e in entries)
        {
            string name = Path.GetFileName(e);
            if (name.StartsWith('.') && !seg.StartsWith('.'))
            {
                continue;
            }
            ExpandGlob(e, parts, i + 1, results);
        }
    }
}
