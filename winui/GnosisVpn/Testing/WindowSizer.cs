using System;
using System.Runtime.InteropServices;

namespace GnosisVpn.Testing;

/// <summary>Lets a window be larger than the screen (screenshot harness on EC2's 800×600 display):
/// answers WM_GETMINMAXINFO with a large maximum track size, so AppWindow.Resize is not clamped and
/// popups (ContentDialog, Flyout) lay out against the real window size.</summary>
public static class WindowSizer
{
    const int WM_GETMINMAXINFO = 0x0024;
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, IntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    static SubclassProc? keep;

    public static void AllowLarge(IntPtr hwnd)
    {
        keep = (h, msg, wp, lp, id, data) =>
        {
            var r = DefSubclassProc(h, msg, wp, lp);
            if (msg == WM_GETMINMAXINFO)
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lp);
                mmi.MaxTrackSize = new POINT { X = 4096, Y = 4096 };
                mmi.MaxSize = new POINT { X = 4096, Y = 4096 };
                Marshal.StructureToPtr(mmi, lp, false);
            }
            return r;
        };
        SetWindowSubclass(hwnd, keep, (IntPtr)0x67766e, IntPtr.Zero);
    }
}
