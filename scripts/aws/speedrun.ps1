# Speed runs through the app on the test server (runs in the desktop session, see windesk.sh):
# for each exit, the app connects (as a click on Connect), then a 90 MB download and a 90 MB upload,
# each capped at 5 min, with per-second throughput. Results: C:\gv\speed\<exit>-down.csv / -up.csv, run.log
param([string]$Exits = "lt,ger", [long]$Bytes = 90000000, [int]$MaxS = 300)
$ErrorActionPreference = "Continue"
$env:DOTNET_ROOT = "C:\dotnet"
$out = "C:\gv\speed"; New-Item -ItemType Directory -Force $out | Out-Null
function Log($m) { $l = (Get-Date).ToUniversalTime().ToString("HH:mm:ss") + " " + $m; Add-Content "$out\run.log" $l; $l }
function Pipe($req) {
  # read + FILE_WRITE_DATA only: what the service grants interactive users
  $c = New-Object System.IO.Pipes.NamedPipeClientStream(".", "gnosisvpn", [System.IO.Pipes.PipeAccessRights]"Read, WriteData, Synchronize", [System.IO.Pipes.PipeOptions]::None, [System.Security.Principal.TokenImpersonationLevel]::Anonymous, [System.IO.HandleInheritability]::None)
  try { $c.Connect(3000); $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c); $w.WriteLine($req); $r.ReadLine() | ConvertFrom-Json } catch { $null } finally { $c.Dispose() }
}
Remove-Item "$out\*" -ErrorAction SilentlyContinue
foreach ($x in $Exits.Split(",")) {
  Get-Process GnosisVPN -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep 2
  Log "=== ${x}: app starts and connects"
  Start-Process C:\gv\app\GnosisVPN.exe -ArgumentList "--click-connect", $x
  $t0 = Get-Date; $dest = $null
  while (((Get-Date) - $t0).TotalSeconds -lt 300) {
    Start-Sleep 2
    $c = (Pipe '{"type":"x_state"}').status.data.connection
    if ($c.type -eq "connected" -and $c.destination -like "*$x*") { $dest = $c.destination; break }
  }
  if (-not $dest) { Log "${x}: not connected within 5 min; skipped"; continue }
  $ip = try { Invoke-RestMethod http://api.ipify.org -TimeoutSec 15 } catch { "?" }
  Log ("${x}: connected to $dest after {0:0} s, public IP $ip" -f ((Get-Date) - $t0).TotalSeconds)
  Start-Sleep 5
  Log "${x}: download $Bytes bytes (max $MaxS s)"
  C:\gv\speedtest\speedtest.exe down "https://speed.cloudflare.com/__down?bytes=$Bytes" $Bytes $MaxS "$out\$x-down.csv" | Select-Object -Last 1 | % { Log "${x}: $_" }
  Log "${x}: upload $Bytes bytes (max $MaxS s)"
  C:\gv\speedtest\speedtest.exe up "https://speed.cloudflare.com/__up" $Bytes $MaxS "$out\$x-up.csv" | Select-Object -Last 1 | % { Log "${x}: $_" }
  $p = (Pipe '{"type":"x_state"}').status.data.connection.pix
  Log ("${x}: PIX phase " + $p.phase.type + ", chunks paid " + $p.chunks_paid + ", failed " + $p.chunks_failed)
}
Pipe '{"type":"disconnect"}' | Out-Null
Get-Process GnosisVPN -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Log "done"
