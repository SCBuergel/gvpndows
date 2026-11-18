# Set the console resolution (default 1920x1080); lists the supported modes first.
param([int]$W = 1920, [int]$H = 1080)
Add-Type @"
using System; using System.Runtime.InteropServices;
public class D {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)] public struct DM {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string n; public short sv, dv, sz, ex; public int fl;
    public int px, py, ori, fo; public short c, d, y, t, co; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string f;
    public short lp; public int bpp, w, h, dfl, freq, i1, i2, i3, i4, i5, i6, i7, i8; }
  [DllImport("user32.dll")] public static extern bool EnumDisplaySettings(string d, int m, ref DM dm);
  [DllImport("user32.dll")] public static extern int ChangeDisplaySettings(ref DM dm, int f);
}
"@
$dm = New-Object D+DM; $dm.sz = [Runtime.InteropServices.Marshal]::SizeOf($dm)
$modes = @(); $i = 0; while ([D]::EnumDisplaySettings($null, $i, [ref]$dm)) { $modes += "$($dm.w)x$($dm.h)"; $i++ }
"modes: " + (($modes | Select-Object -Unique) -join " ")
[D]::EnumDisplaySettings($null, -1, [ref]$dm) | Out-Null
$dm.w = $W; $dm.h = $H; $dm.fl = 0x180000
"change: " + [D]::ChangeDisplaySettings([ref]$dm, 1)
Add-Type -AssemblyName System.Windows.Forms; "now: " + [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
