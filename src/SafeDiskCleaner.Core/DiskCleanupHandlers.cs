using Microsoft.Win32;

namespace SafeDiskCleaner.Core;

/// <summary>The Disk Cleanup category keys under HKLM\...\Explorer\VolumeCaches, behind an interface so tests can fake them.</summary>
public interface IVolumeCaches
{
    /// <summary>Every category subkey name.</summary>
    IReadOnlyList<string> Categories();

    bool Exists(string category);

    void SetFlag(string category, string valueName, int value);

    void DeleteFlag(string category, string valueName);
}

/// <summary>
/// Runs Windows' own Disk Cleanup handlers for what only Windows can remove safely (the component
/// store, Windows.old, setup leftovers), through cleanmgr /sagerun with a slot this app owns.
/// </summary>
public sealed class DiskCleanupHandlers
{
    /// <summary>The cleanmgr /sagerun slot this app uses. Its flags are removed before and after every run.</summary>
    public const int Sage = 777;

    public static readonly string FlagName = $"StateFlags{Sage:0000}";

    /// <summary>Handler categories behind the "Windows Update cleanup" row: category name, what it is.</summary>
    public static readonly (string Name, string What)[] UpdateHandlers =
    [
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
    ];

    public const string PreviousInstallations = "Previous Installations";
    public const string EsdFiles = "Windows ESD installation files";

    private readonly IVolumeCaches _keys;
    private readonly Action _runCleanmgr;

    public DiskCleanupHandlers(IVolumeCaches keys, Action runCleanmgr)
    {
        _keys = keys;
        _runCleanmgr = runCleanmgr;
    }

    /// <summary>The handlers on this PC, with the real registry and cleanmgr.</summary>
    public static DiskCleanupHandlers ForThisPc() => new(new WindowsVolumeCaches(), RunCleanmgr);

    public bool Exists(string category) => _keys.Exists(category);

    /// <summary>
    /// Runs Disk Cleanup for exactly these categories. First every category's flag in this
    /// app's slot is deleted, so a crashed earlier run cannot widen this one (the Downloads
    /// folder is a category too); then only the ticked categories that exist are flagged;
    /// and the flags are deleted again afterwards, even if cleanmgr fails.
    /// </summary>
    /// <returns>The categories that were actually run.</returns>
    public IReadOnlyList<string> Run(IEnumerable<string> categories)
    {
        IReadOnlyList<string> all = _keys.Categories();
        foreach (string c in all)
        {
            _keys.DeleteFlag(c, FlagName);
        }
        List<string> wanted = categories.Distinct(StringComparer.OrdinalIgnoreCase).Where(_keys.Exists).ToList();
        if (wanted.Count == 0)
        {
            return wanted;
        }
        try
        {
            foreach (string c in wanted)
            {
                _keys.SetFlag(c, FlagName, 2);
            }
            _runCleanmgr();
        }
        finally
        {
            foreach (string c in all.Concat(wanted).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _keys.DeleteFlag(c, FlagName);
            }
        }
        return wanted;
    }

    private static void RunCleanmgr()
    {
        // Like the reference, the exit code is not treated as failure: cleanmgr does not document it.
        // Freed space is measured on the drive either way.
        Tools.RunHidden(Tools.System32("cleanmgr.exe"), $"/sagerun:{Sage}", TimeSpan.FromHours(2));
    }
}

/// <summary>The real VolumeCaches keys, in the 64-bit registry view.</summary>
public sealed class WindowsVolumeCaches : IVolumeCaches
{
    public const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    public IReadOnlyList<string> Categories()
    {
        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? k = hklm.OpenSubKey(KeyPath);
            return k?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    public bool Exists(string category)
    {
        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? k = hklm.OpenSubKey(KeyPath + "\\" + category);
            return k is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public void SetFlag(string category, string valueName, int value)
    {
        using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? k = hklm.OpenSubKey(KeyPath + "\\" + category, writable: true);
        k?.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    public void DeleteFlag(string category, string valueName)
    {
        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? k = hklm.OpenSubKey(KeyPath + "\\" + category, writable: true);
            k?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Not admin: nothing of ours can be there either, since setting it needs admin too.
        }
    }
}
