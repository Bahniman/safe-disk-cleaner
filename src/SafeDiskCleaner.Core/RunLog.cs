using System.Globalization;
using System.Text;

namespace SafeDiskCleaner.Core;

public sealed record LogRow(DateTime When, string Rule, string Path, long Bytes, string Result);

/// <summary>
/// %LOCALAPPDATA%\SafeDiskCleaner\runs.csv: one row per item (time, rule, path, bytes, result)
/// plus a TOTAL row with the measured change in free space.
/// </summary>
public static class RunLog
{
    public const string Total = "TOTAL";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    public static string FilePath => System.IO.Path.Combine(KnownPaths.LogDir, "runs.csv");

    public static string ErrorLogPath => System.IO.Path.Combine(KnownPaths.LogDir, "error.log");

    public static void Append(IReadOnlyCollection<LogRow> rows, long freed, DateTime when, string? file = null)
    {
        file ??= FilePath;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        bool isNew = !File.Exists(file);
        var sb = new StringBuilder();
        if (isNew)
        {
            sb.Append("time,rule,path,bytes,result\r\n");
        }
        foreach (LogRow row in rows)
        {
            AppendRow(sb, row);
        }
        AppendRow(sb, new LogRow(when, Total, "drive free-space change", freed, "measured"));
        File.AppendAllText(file, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>When the last clean ran and what it freed, from the newest TOTAL row.</summary>
    public static (DateTime When, long Freed)? ReadLast(string? file = null)
    {
        file ??= FilePath;
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }
            string[] lines = File.ReadAllLines(file);
            for (int i = lines.Length - 1; i > 0; i--)
            {
                List<string> f = ParseLine(lines[i]);
                if (f.Count >= 4 && f[1] == Total &&
                    DateTime.TryParseExact(f[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime when) &&
                    long.TryParse(f[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long freed))
                {
                    return (when, freed);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    public static void WriteError(string text)
    {
        try
        {
            Directory.CreateDirectory(KnownPaths.LogDir);
            File.AppendAllText(ErrorLogPath, DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture) + "\r\n" + text + "\r\n\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void AppendRow(StringBuilder sb, LogRow row)
    {
        sb.Append(Field(row.When.ToString(TimeFormat, CultureInfo.InvariantCulture))).Append(',')
          .Append(Field(row.Rule)).Append(',')
          .Append(Field(row.Path)).Append(',')
          .Append(row.Bytes.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(Field(row.Result)).Append("\r\n");
    }

    /// <summary>CSV-quotes a text field, and defuses a leading = + @ so a spreadsheet never runs it as a formula.</summary>
    internal static string Field(string s)
    {
        if (s.Length > 0 && (s[0] == '=' || s[0] == '+' || s[0] == '@'))
        {
            s = "'" + s;
        }
        return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    internal static List<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cur.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cur.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(cur.ToString());
                cur.Clear();
            }
            else
            {
                cur.Append(c);
            }
        }
        fields.Add(cur.ToString());
        return fields;
    }
}
