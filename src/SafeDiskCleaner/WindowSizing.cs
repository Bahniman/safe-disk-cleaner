using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace SafeDiskCleaner;

/// <summary>Initial size, centring and a DPI-aware minimum size (WM_GETMINMAXINFO) for the main window.</summary>
internal static class WindowSizing
{
    private const uint WmGetMinMaxInfo = 0x0024;

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr idSubclass, UIntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr idSubclass, UIntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    // Kept in a field so the delegate is not collected while Windows still calls it.
    private static SubclassProc? s_proc;

    public static double Scale(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>Stops the window from being resized below the given size in effective (96 DPI) pixels.</summary>
    public static void SetMinimumSize(IntPtr hwnd, int widthDip, int heightDip)
    {
        s_proc = (hWnd, msg, wParam, lParam, id, refData) =>
        {
            IntPtr result = DefSubclassProc(hWnd, msg, wParam, lParam);
            if (msg == WmGetMinMaxInfo && lParam != IntPtr.Zero)
            {
                double scale = Scale(hWnd);
                MINMAXINFO info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                info.ptMinTrackSize.X = (int)Math.Round(widthDip * scale);
                info.ptMinTrackSize.Y = (int)Math.Round(heightDip * scale);
                Marshal.StructureToPtr(info, lParam, false);
            }
            return result;
        };
        SetWindowSubclass(hwnd, s_proc, (UIntPtr)1, UIntPtr.Zero);
    }

    /// <summary>Sizes the window to the given effective size (shrunk to fit the screen) and centres it.</summary>
    public static void ResizeAndCenter(AppWindow window, IntPtr hwnd, int widthDip, int heightDip)
    {
        double scale = Scale(hwnd);
        int w = (int)Math.Round(widthDip * scale);
        int h = (int)Math.Round(heightDip * scale);
        DisplayArea? area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary);
        if (area is not null)
        {
            RectInt32 work = area.WorkArea;
            w = Math.Min(w, (int)(work.Width * 0.95));
            h = Math.Min(h, (int)(work.Height * 0.95));
            window.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
        }
        else
        {
            window.Resize(new SizeInt32(w, h));
        }
    }
}
