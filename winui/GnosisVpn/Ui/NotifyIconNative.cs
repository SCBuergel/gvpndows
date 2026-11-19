using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GnosisVpn.Ui;

/// <summary>The notification-area (tray) icon on Shell_NotifyIcon directly: a hidden window receives
/// its clicks; right click shows the native Windows menu (TrackPopupMenuEx). Delegates are held for
/// the lifetime of the process, so no callback can reach a collected delegate.</summary>
public sealed class NotifyIconNative : IDisposable
{
    const uint WM_TRAY = 0x8000 + 1, WM_NULL = 0, WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80, NOTIFYICON_VERSION_4 = 4;
    const uint MF_STRING = 0, MF_SEPARATOR = 0x800, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2, TPM_BOTTOMALIGN = 0x20, TPM_RIGHTALIGN = 8, IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS { public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; }
    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA d);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClass(ref WNDCLASS c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string s);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr m, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr m, uint flags, int x, int y, IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr m);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? n);

    static readonly WndProc procKeep = StaticProc;          // never collected
    static readonly Dictionary<IntPtr, NotifyIconNative> byHwnd = new();
    static bool classRegistered;
    readonly IntPtr hwnd;
    readonly uint taskbarCreated;
    IntPtr hIcon;
    string tip = "Gnosis VPN", iconPath = "";
    bool added, menuOpen;
    long lastSelect;

    /// <summary>Left click (or keyboard select) with the click position in screen pixels.</summary>
    public Action<POINT>? LeftClick;
    /// <summary>Builds the menu at right click: (label, action) pairs; a null label is a separator.</summary>
    public Func<List<(string? label, Action? act)>>? Menu;

    public NotifyIconNative()
    {
        var inst = GetModuleHandle(null);
        if (!classRegistered)
        {
            var wc = new WNDCLASS { lpfnWndProc = procKeep, hInstance = inst, lpszClassName = "GnosisVpnTrayWindow" };
            RegisterClass(ref wc); classRegistered = true;
        }
        hwnd = CreateWindowEx(0, "GnosisVpnTrayWindow", "Gnosis VPN tray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        byHwnd[hwnd] = this;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    NOTIFYICONDATA Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = hwnd, uID = 1, uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
        uCallbackMessage = WM_TRAY, hIcon = hIcon, szTip = tip, szInfo = "", szInfoTitle = "", uVersion = NOTIFYICON_VERSION_4,
    };

    public void Set(string icoPath, string tooltip)
    {
        tip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (icoPath != iconPath)
        {
            var old = hIcon;
            hIcon = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, GetSystemMetrics(49), GetSystemMetrics(50), LR_LOADFROMFILE);   // SM_CXSMICON, SM_CYSMICON
            iconPath = icoPath;
            if (old != IntPtr.Zero) DestroyIcon(old);
        }
        var d = Data();
        if (!added) { added = Shell_NotifyIcon(NIM_ADD, ref d); if (added) Shell_NotifyIcon(NIM_SETVERSION, ref d); }
        else Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    static IntPtr StaticProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        if (byHwnd.TryGetValue(h, out var me))
        {
            if (msg == WM_TRAY)
            {
                uint ev = (uint)(lp.ToInt64() & 0xFFFF);
                var pt = new POINT { X = (short)(wp.ToInt64() & 0xFFFF), Y = (short)((wp.ToInt64() >> 16) & 0xFFFF) };
                // Version 4 sends NIN_SELECT after the mouse-up and WM_CONTEXTMENU after the right
                // mouse-up: act only on those, once per click (Enter can send NIN_KEYSELECT twice).
                if (ev is NIN_SELECT or NIN_KEYSELECT)
                {
                    var now = Environment.TickCount64;
                    if (now - me.lastSelect >= 300) { me.lastSelect = now; me.LeftClick?.Invoke(pt); }
                }
                else if (ev == WM_CONTEXTMENU) me.ShowMenu(pt);
                return IntPtr.Zero;
            }
            if (msg == me.taskbarCreated) { me.added = false; me.Set(me.iconPath, me.tip); return IntPtr.Zero; }   // Explorer restarted
        }
        return DefWindowProc(h, msg, wp, lp);
    }

    void ShowMenu(POINT pt)
    {
        if (menuOpen) return;   // a tray message inside TrackPopupMenuEx's loop must not open a second menu
        var items = Menu?.Invoke(); if (items == null) return;
        int cmd;
        menuOpen = true;
        try
        {
            var m = CreatePopupMenu();
            for (int i = 0; i < items.Count; i++)
                if (items[i].label == null) AppendMenu(m, MF_SEPARATOR, UIntPtr.Zero, null);
                else AppendMenu(m, MF_STRING, (UIntPtr)(i + 1), items[i].label);
            SetForegroundWindow(hwnd);
            if (pt.X == 0 && pt.Y == 0) GetCursorPos(out pt);
            cmd = TrackPopupMenuEx(m, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RIGHTALIGN, pt.X, pt.Y, hwnd, IntPtr.Zero);
            PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            DestroyMenu(m);
        }
        finally { menuOpen = false; }
        if (cmd > 0) items[cmd - 1].act?.Invoke();
    }

    [StructLayout(LayoutKind.Sequential)] struct NOTIFYICONIDENTIFIER { public uint cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT r);
    /// <summary>Opens the menu at the icon (tests; a right click does the same).</summary>
    public void ShowMenuAtIcon()
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = hwnd, uID = 1 };
        var pt = Shell_NotifyIconGetRect(ref id, out var r) == 0 ? new POINT { X = (r.L + r.R) / 2, Y = r.T } : new POINT();
        ShowMenu(pt);
    }

    public void Dispose()
    {
        if (added) { var d = Data(); Shell_NotifyIcon(NIM_DELETE, ref d); added = false; }
        if (hIcon != IntPtr.Zero) { DestroyIcon(hIcon); hIcon = IntPtr.Zero; }
        byHwnd.Remove(hwnd);
        DestroyWindow(hwnd);
    }
}
