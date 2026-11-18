# Side-by-side sheets: prototype (cropped to its app window) | app, at half size, 3 pairs per JPEG.
param([string]$Ref = "C:\gv\ref", [string]$App = "C:\gv\shots", [string]$Out = "C:\gv\cmp")
Add-Type -AssemblyName System.Drawing
Remove-Item $Out -Recurse -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $Out | Out-Null
$sizes = @{ wide = @(1180, 650); std = @(960, 640); compact = @(720, 620); narrow = @(520, 640) }
function Crop($name) {
  $w = 1180; $h = 650; $max = $false
  foreach ($k in $sizes.Keys) { if ($name -match "-$k-") { $w = $sizes[$k][0]; $h = $sizes[$k][1] } }
  if ($name -match "-max-") { $max = $true }
  if ($max) { return @(17, 58, 1366, 720) }
  $x = [math]::Round((1366 - $w) / 2); $y = [math]::Max(8, [math]::Round((768 - 48 - $h) / 2))
  if ($w -eq 1180) { $x = 93; $y = 34 }
  return @((17 + $x), (58 + $y), $w, $h)
}
$names = Get-ChildItem $App -Filter *.png | % { $_.BaseName } | Sort-Object
$jpeg = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | ? { $_.MimeType -eq "image/jpeg" }
$ep = New-Object System.Drawing.Imaging.EncoderParameters 1; $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 82L
$font = New-Object System.Drawing.Font "Segoe UI", 11
for ($i = 0; $i -lt $names.Count; $i += 3) {
  $grp = $names[$i..([math]::Min($i + 2, $names.Count - 1))]
  $sheet = New-Object System.Drawing.Bitmap 1380, (3 * 390)
  $g = [System.Drawing.Graphics]::FromImage($sheet); $g.Clear([System.Drawing.Color]::FromArgb(40, 40, 46)); $g.InterpolationMode = "HighQualityBicubic"
  $row = 0
  foreach ($n in $grp) {
    $y0 = $row * 390
    $g.DrawString("$n   (prototype | app)", $font, [System.Drawing.Brushes]::White, 8, $y0 + 2)
    if (Test-Path "$Ref\$n.png") {
      $r = [System.Drawing.Image]::FromFile("$Ref\$n.png"); $c = Crop $n
      $sc = [math]::Min(680 / $c[2], 360 / $c[3])
      $g.DrawImage($r, (New-Object System.Drawing.Rectangle 8, ($y0 + 24), ([int]($c[2] * $sc)), ([int]($c[3] * $sc))), (New-Object System.Drawing.Rectangle $c[0], $c[1], $c[2], $c[3]), "Pixel"); $r.Dispose()
    }
    $a = [System.Drawing.Image]::FromFile("$App\$n.png")
    $sc = [math]::Min(680 / $a.Width, 360 / $a.Height)
    $g.DrawImage($a, (New-Object System.Drawing.Rectangle 694, ($y0 + 24), ([int]($a.Width * $sc)), ([int]($a.Height * $sc)))); $a.Dispose()
    $row++
  }
  $sheet.Save("$Out\cmp-$('{0:D2}' -f ($i / 3)).jpg", $jpeg, $ep); $g.Dispose(); $sheet.Dispose()
}
"sheets: " + (Get-ChildItem $Out).Count
Get-ChildItem $Out | % { $_.Name + ": " + ((Get-Content "$App\..\cmp-index.txt" -ErrorAction SilentlyContinue) -join "") } | Out-Null
$k = 0; $idx = for ($i = 0; $i -lt $names.Count; $i += 3) { "cmp-$('{0:D2}' -f $k): " + ($names[$i..([math]::Min($i + 2, $names.Count - 1))] -join ", "); $k++ }
$idx | Set-Content "$Out\index.txt"
Compress-Archive -Force "$Out\*" C:\gv\cmp.zip
