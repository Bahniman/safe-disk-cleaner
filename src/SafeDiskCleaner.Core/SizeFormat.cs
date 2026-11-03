using System.Globalization;

namespace SafeDiskCleaner.Core;

/// <summary>fmt() from the reference: "1.5 GB", "512 MB", "12 KB".</summary>
public static class SizeFormat
{
    private const double KB = 1024d, MB = 1024d * 1024, GB = 1024d * 1024 * 1024;

    public static string Format(long? bytes, CultureInfo? culture = null)
    {
        if (bytes is null)
        {
            return "?";
        }
        culture ??= CultureInfo.CurrentCulture;
        long n = Math.Max(0, bytes.Value);
        if (n >= GB)
        {
            return (n / GB).ToString("0.0", culture) + " GB";
        }
        if (n >= MB)
        {
            return (n / MB).ToString("0", culture) + " MB";
        }
        return (n / KB).ToString("0", culture) + " KB";
    }

    /// <summary>"416.9 GB" style, always one decimal in GB, for the drive header.</summary>
    public static string FormatGb(long bytes, CultureInfo? culture = null) =>
        (Math.Max(0, bytes) / GB).ToString("0.0", culture ?? CultureInfo.CurrentCulture) + " GB";
}
