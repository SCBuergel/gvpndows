# Shared by install.ps1 and uninstall.ps1: visible, time-bounded steps (nothing waits silently).

$script:t0 = Get-Date
function Step([string]$text) {
  $script:stepStart = Get-Date
  Write-Host ("[{0:HH:mm:ss}] {1} ..." -f (Get-Date), $text)
  Log $text
}
function Done([string]$note = "") {
  $s = ((Get-Date) - $script:stepStart).TotalSeconds
  Write-Host ("           done in {0:0.0} s{1}" -f $s, $(if ($note) { " - $note" } else { "" })) -ForegroundColor DarkGray
}
function Log([string]$m) {
  try { Add-Content -Path (Join-Path $env:TEMP "gnosisvpn-install.log") -Value ("{0:u} {1}" -f (Get-Date), $m) } catch { }
}

# Closes the app (it reconnects to the new service by itself when started again).
function Stop-App {
  Step "Closing the Gnosis VPN app"
  $p = @(Get-Process GnosisVPN -ErrorAction SilentlyContinue)
  $p | Stop-Process -Force -ErrorAction SilentlyContinue
  Done $(if ($p.Count) { "closed $($p.Count) window(s)" } else { "it was not running" })
}

# Stops the service with a countdown: it disconnects the VPN first, which can take a while. After
# $max seconds the service process is ended, so uninstall/update never hangs.
function Stop-GnosisService([int]$max = 30) {
  $svc = Get-Service GnosisVPN -ErrorAction SilentlyContinue
  if (-not $svc) { Write-Host "           (the service is not installed)"; return }
  if ($svc.Status -eq "Stopped") { Write-Host "           (the service is already stopped)"; return }
  Step "Stopping the Gnosis VPN service (it disconnects the VPN first; at most $max s)"
  sc.exe stop GnosisVPN | Out-Null
  for ($i = 1; $i -le $max; $i++) {
    $st = (Get-Service GnosisVPN).Status
    $proc = Get-Process gvpndows -ErrorAction SilentlyContinue | Where-Object Path -eq "C:\GnosisVPN\gvpndows.exe"
    if ($st -eq "Stopped" -and -not $proc) { Write-Host ""; Done; return }
    Write-Host ("`r           waiting: {0,2} s, service {1}   " -f $i, $st) -NoNewline
    Start-Sleep 1
  }
  Write-Host ""
  Get-Process gvpndows -ErrorAction SilentlyContinue | Where-Object Path -eq "C:\GnosisVPN\gvpndows.exe" | Stop-Process -Force
  Start-Sleep 1
  Done "did not stop within $max s, so its process was ended"
}
