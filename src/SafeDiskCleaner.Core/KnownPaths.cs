namespace SafeDiskCleaner.Core;

/// <summary>
/// The base folders the engine works with, read from the environment of whoever runs the app.
/// Mirrors HOME, LOCAL, ROAMING, PD, WIN and TEMP in reference/engine.py.
/// </summary>
public static class KnownPaths
{
    public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static readonly string Local = Env("LOCALAPPDATA") ?? Path.Combine(Home, "AppData", "Local");

    public static readonly string Roaming = Env("APPDATA") ?? Path.Combine(Home, "AppData", "Roaming");

    public static readonly string LocalLow = Path.Combine(Home, "AppData", "LocalLow");

    public static readonly string ProgramData = Env("PROGRAMDATA") ?? @"C:\ProgramData";

    public static readonly string Windows = Env("SystemRoot")
        ?? NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
        ?? @"C:\Windows";

    /// <summary>The user's temp folder. Like the reference, this is LOCALAPPDATA\Temp, not %TEMP%.</summary>
    public static readonly string Temp = Path.Combine(Local, "Temp");

    /// <summary>The system drive root, for example "C:\".</summary>
    public static readonly string SystemDrive = Path.GetPathRoot(Windows) is { Length: > 0 } root ? root : @"C:\";

    /// <summary>C:\Temp on most PCs.</summary>
    public static readonly string DriveTemp = Path.Combine(SystemDrive, "Temp");

    public static readonly string ProgramFiles = Env("ProgramW6432") ?? Env("ProgramFiles") ?? Path.Combine(SystemDrive, "Program Files");

    /// <summary>Where protect.txt lives (%APPDATA%\SafeDiskCleaner).</summary>
    public static readonly string SettingsDir = Path.Combine(Roaming, "SafeDiskCleaner");

    /// <summary>Where runs.csv and error.log live (%LOCALAPPDATA%\SafeDiskCleaner).</summary>
    public static readonly string LogDir = Path.Combine(Local, "SafeDiskCleaner");

    public static string Downloads => Path.Combine(Home, "Downloads");
    public static string Documents => Path.Combine(Home, "Documents");
    public static string Desktop => Path.Combine(Home, "Desktop");

    /// <summary>
    /// Expands the placeholders used by the rule tables: {H} home, {L} local app data,
    /// {R} roaming app data, {P} ProgramData, {W} Windows, {D} system drive (no trailing slash).
    /// </summary>
    public static string Expand(string pattern) => pattern
        .Replace("{H}", Home, StringComparison.Ordinal)
        .Replace("{L}", Local, StringComparison.Ordinal)
        .Replace("{R}", Roaming, StringComparison.Ordinal)
        .Replace("{P}", ProgramData, StringComparison.Ordinal)
        .Replace("{W}", Windows, StringComparison.Ordinal)
        .Replace("{D}", SystemDrive.TrimEnd('\\'), StringComparison.Ordinal);

    private static string? Env(string name) => NonEmpty(Environment.GetEnvironmentVariable(name));

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
