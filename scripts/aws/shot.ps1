# Screenshot of the whole desktop (or of one window by process name) to C:\gv\shots\NAME.png
param([string]$Name = "desk", [string]$Proc = "")
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
New-Item -ItemType Directory -Force C:\gv\shots | Out-Null
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
if ($Proc) {
  Add-Type @"
using System; using System.Runtime.InteropServices;
public class W { [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out R r, int s); }
"@
  $p = Get-Process $Proc -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
  if ($p) { $r = New-Object W+R; [W]::DwmGetWindowAttribute($p.MainWindowHandle, 9, [ref]$r, 16) | Out-Null
    $b = New-Object System.Drawing.Rectangle $r.L, $r.T, ($r.Ri - $r.L), ($r.B - $r.T) }
}
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save("C:\gv\shots\$Name.png", [System.Drawing.Imaging.ImageFormat]::Png)
"shot $Name $($b.Width)x$($b.Height)"
