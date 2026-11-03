using System.Text;

namespace SafeDiskCleaner.Core;

/// <summary>
/// %APPDATA%\SafeDiskCleaner\protect.txt: one folder per line, '#' starts a comment,
/// environment variables are expanded. Anything listed is treated like a protected path.
/// </summary>
public static class UserProtection
{
    public static string FilePath => Path.Combine(KnownPaths.SettingsDir, "protect.txt");

    public const string Header =
        "# Safe Disk Cleaner: folders you want left alone.\r\n" +
        "#\r\n" +
        "# A folder listed here is never deleted, nor is anything inside it, nor any folder\r\n" +
        "# that contains it. One folder per line. Lines starting with # are ignored.\r\n" +
        "# Environment variables work, for example:\r\n" +
        "#   %USERPROFILE%\\Games\\Mods\r\n" +
        "#   %LOCALAPPDATA%\\SomeApp\\Cache\r\n" +
        "#\r\n" +
        "# The \"Protect a folder\" button in the app adds lines here for you.\r\n";

    /// <summary>Creates the file with its comment header if it does not exist yet. Returns its path.</summary>
    public static string EnsureFile(string? file = null)
    {
        file ??= FilePath;
        try
        {
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, Header, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return file;
    }

    /// <summary>The folders listed in the file, expanded. Missing file means none.</summary>
    public static List<string> Read(string? file = null)
    {
        file ??= FilePath;
        string[] lines;
        try
        {
            lines = File.Exists(file) ? File.ReadAllLines(file) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return Parse(lines);
    }

    public static List<string> Parse(IEnumerable<string> lines)
    {
        var list = new List<string>();
        foreach (string line in lines)
        {
            string s = line.Trim();
            if (s.Length == 0 || s.StartsWith('#'))
            {
                continue;
            }
            s = Environment.ExpandEnvironmentVariables(s.Trim('"', ' '));
            if (s.Length > 3)
            {
                list.Add(s);
            }
        }
        return list;
    }

    /// <summary>Appends a folder to the file (creating it first). Does nothing if it is already listed.</summary>
    public static void Add(string folder, string? file = null)
    {
        file = EnsureFile(file);
        string? wanted = PathUtil.TryNorm(folder) ?? throw new ArgumentException("Not a valid folder path.", nameof(folder));
        foreach (string existing in Read(file))
        {
            if (string.Equals(PathUtil.TryNorm(existing), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        string text = File.Exists(file) ? File.ReadAllText(file) : "";
        string prefix = text.Length > 0 && !text.EndsWith('\n') ? "\r\n" : "";
        File.AppendAllText(file, prefix + wanted + "\r\n", new UTF8Encoding(false));
    }
}
