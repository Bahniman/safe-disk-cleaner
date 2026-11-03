using System.Runtime.InteropServices;
using System.Text;

namespace SafeDiskCleaner.Core;

internal static class NativeMethods
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);

    /// <summary>Expands 8.3 short names (RUNNER~1) so paths compare reliably. Null when it cannot.</summary>
    public static string? TryGetLongPath(string path)
    {
        try
        {
            var sb = new StringBuilder(1024);
            uint n = GetLongPathNameW(path, sb, (uint)sb.Capacity);
            if (n > sb.Capacity)
            {
                sb = new StringBuilder((int)n + 1);
                n = GetLongPathNameW(path, sb, (uint)sb.Capacity);
            }
            return n == 0 || n > sb.Capacity ? null : sb.ToString();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Full image path of a process via QueryFullProcessImageName with
    /// PROCESS_QUERY_LIMITED_INFORMATION (Process.MainModule throws for many processes).
    /// </summary>
    public static string? TryGetImagePath(int pid)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>Recycle Bin size and item count across all drives, or null if the query fails.</summary>
    public static (long Bytes, long Items)? QueryRecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : null;
    }

    /// <summary>Empties the Recycle Bin on all drives without confirmation, progress UI or sound.</summary>
    public static void EmptyRecycleBin()
    {
        const uint noConfirmation = 0x1, noProgressUi = 0x2, noSound = 0x4;
        int hr = SHEmptyRecycleBinW(IntPtr.Zero, null, noConfirmation | noProgressUi | noSound);
        // E_UNEXPECTED (0x8000FFFF) is what the shell returns when the bin is already empty.
        if (hr != 0 && hr != unchecked((int)0x8000FFFF))
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }
}
