using Microsoft.Dism;
using Microsoft.Win32;

namespace SafeDiskCleaner.Core;

/// <summary>A third-party driver package in the driver store.</summary>
/// <param name="PublishedName">oemNN.inf</param>
/// <param name="OriginalName">The INF's own name, for example netwtw10.inf.</param>
public sealed record DriverPackage(string PublishedName, string OriginalName, string Provider, VersionKey Version);

/// <summary>Windows said no to deleting a driver package (it is still in use). Reported as "Windows kept it".</summary>
public sealed class KeptByWindowsException(string message) : Exception(message);

/// <summary>
/// Old driver versions, the way Disk Cleanup's "Device driver packages" handles them: older copies
/// of a driver that a newer version of the same INF from the same provider replaced.
/// </summary>
public static class DriverCleanup
{
    public const string KeptByWindows = "Windows kept it (still in use)";

    /// <summary>
    /// Groups packages by original INF name plus provider and picks every package older than the
    /// newest one in its group. The newest, and any package that ties with it, stay. Packages a
    /// device uses now are never picked.
    /// </summary>
    public static (List<(DriverPackage Package, VersionKey Newest)> Old, List<(DriverPackage Package, string Why)> Skipped)
        SelectOld(IEnumerable<DriverPackage> packages, IReadOnlySet<string> infsInUse)
    {
        var old = new List<(DriverPackage, VersionKey)>();
        var skipped = new List<(DriverPackage, string)>();
        foreach (var group in packages.GroupBy(p => (p.OriginalName.ToLowerInvariant(), p.Provider), p => p))
        {
            VersionKey newest = group.Max(p => p.Version)!;
            foreach (DriverPackage p in group.OrderBy(p => p.PublishedName, StringComparer.OrdinalIgnoreCase))
            {
                if (p.Version >= newest)
                {
                    continue;
                }
                if (infsInUse.Contains(p.PublishedName))
                {
                    skipped.Add((p, "a device still uses it"));
                    continue;
                }
                old.Add((p, newest));
            }
        }
        return (old, skipped);
    }

    /// <summary>Third-party driver packages, listed language-neutrally with the DISM API. Needs admin.</summary>
    public static List<DriverPackage> ListThirdParty()
    {
        var list = new List<DriverPackage>();
        DismApi.Initialize(DismLogLevel.LogErrors);
        try
        {
            using DismSession session = DismApi.OpenOnlineSession();
            foreach (DismDriverPackage d in DismApi.GetDrivers(session, allDrivers: false))
            {
                if (d.InBox || !d.PublishedName.StartsWith("oem", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                list.Add(new DriverPackage(d.PublishedName, Path.GetFileName(d.OriginalFileName), d.ProviderName ?? "",
                    VersionKey.Parse(d.Version?.ToString())));
            }
        }
        finally
        {
            DismApi.Shutdown();
        }
        return list;
    }

    /// <summary>oemNN.inf names a device is installed with right now (InfPath under each device class).</summary>
    public static HashSet<string> InfsInUse()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string classes = @"SYSTEM\CurrentControlSet\Control\Class";
        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? k = hklm.OpenSubKey(classes);
            if (k is null)
            {
                return used;
            }
            foreach (string cls in k.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? ck = k.OpenSubKey(cls);
                    if (ck is null)
                    {
                        continue;
                    }
                    foreach (string sub in ck.GetSubKeyNames())
                    {
                        try
                        {
                            using RegistryKey? dk = ck.OpenSubKey(sub);
                            if (dk?.GetValue("InfPath") is string inf && inf.Length > 0)
                            {
                                used.Add(inf);
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        return used;
    }

    /// <summary>
    /// Bytes the package takes in DriverStore\FileRepository, or null if unknown. The folder name is
    /// the "Active" value in the live driver database (the DRIVERS hive); SYSTEM\DriverDatabase is a
    /// stale copy, used only as a fallback.
    /// </summary>
    public static long? PackageSize(string publishedName)
    {
        foreach (string db in new[] { @"DRIVERS\DriverDatabase", @"SYSTEM\DriverDatabase" })
        {
            try
            {
                using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey? k = hklm.OpenSubKey($@"{db}\DriverInfFiles\{publishedName}");
                string? active = k?.GetValue("Active") switch
                {
                    string s => s,
                    string[] arr => arr.FirstOrDefault(),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(active))
                {
                    return PathUtil.TreeSize(Path.Combine(KnownPaths.Windows, "System32", "DriverStore", "FileRepository", active));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }
        return null;
    }

    /// <summary>pnputil /delete-driver without /force, so Windows itself refuses a package that is still in use.</summary>
    public static void Delete(string publishedName)
    {
        if (!publishedName.StartsWith("oem", StringComparison.OrdinalIgnoreCase) || !publishedName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) ||
            publishedName.IndexOfAny([' ', '"', '\\', '/']) >= 0)
        {
            throw new ArgumentException("Not a third-party driver package name.", nameof(publishedName));
        }
        (int code, _) = Tools.RunHidden(Tools.System32("pnputil.exe"), $"/delete-driver {publishedName}", TimeSpan.FromMinutes(5));
        if (code != 0)
        {
            throw new KeptByWindowsException(KeptByWindows);
        }
    }
}
