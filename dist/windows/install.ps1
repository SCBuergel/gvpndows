# Gnosis VPN for Windows: installs (or updates) the service and the app. Run by Install.cmd as admin.
param([switch]$NoLaunch)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$app = Join-Path $env:ProgramFiles "Gnosis VPN"
function Say($m) { Write-Host $m; Add-Content -Path (Join-Path $env:TEMP "gnosisvpn-install.log") -Value ("{0:u} {1}" -f (Get-Date), $m) }
Say "Gnosis VPN install from $here"
Get-ChildItem $here -Recurse -File | Unblock-File   # the downloaded zip marks every file

# stop what runs now (an update)
Get-Process GnosisVPN -ErrorAction SilentlyContinue | Stop-Process -Force
if (Get-Service GnosisVPN -ErrorAction SilentlyContinue) { Stop-Service GnosisVPN -Force -ErrorAction SilentlyContinue }
foreach ($i in 1..30) { if (-not (Get-Process gvpndows -ErrorAction SilentlyContinue | Where-Object Path -eq "C:\GnosisVPN\gvpndows.exe")) { break }; Start-Sleep 1 }
Get-Process gvpndows -ErrorAction SilentlyContinue | Where-Object Path -eq "C:\GnosisVPN\gvpndows.exe" | Stop-Process -Force

# the service: C:\GnosisVPN, changeable by SYSTEM and Administrators only (it runs as LocalSystem)
New-Item -ItemType Directory -Force C:\GnosisVPN | Out-Null
icacls C:\GnosisVPN /setowner "*S-1-5-32-544" /T /C /Q | Out-Null
icacls C:\GnosisVPN /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" /C /Q | Out-Null
Copy-Item (Join-Path $here "service\gvpndows.exe") C:\GnosisVPN\gvpndows.exe -Force
Copy-Item (Join-Path $here "service\gnosis") C:\GnosisVPN -Recurse -Force
icacls C:\GnosisVPN\* /reset /T /C /Q | Out-Null
if ((Get-FileHash (Join-Path $here "service\gvpndows.exe")).Hash -ne (Get-FileHash C:\GnosisVPN\gvpndows.exe).Hash) { throw "C:\GnosisVPN\gvpndows.exe is not the new build" }
$weak = icacls C:\GnosisVPN /T | Select-String "(Users|Authenticated Users|Everyone|INTERACTIVE):\(.*(F|M|W)\)"
if ($weak) { throw "C:\GnosisVPN is writable by non-admins: $weak" }
if (-not (Get-Service GnosisVPN -ErrorAction SilentlyContinue)) {
  New-Service -Name GnosisVPN -BinaryPathName '"C:\GnosisVPN\gvpndows.exe" --service' -DisplayName "Gnosis VPN" -StartupType Automatic -Description "Runs the Gnosis VPN client core and the tunnel for the Gnosis VPN app. It never connects by itself." | Out-Null
}
# starts with Windows (delayed, after the network), restarted if it ever crashes; connecting stays the app's job
sc.exe config GnosisVPN start= delayed-auto | Out-Null
sc.exe failure GnosisVPN reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
Say "service installed: $((Get-FileHash C:\GnosisVPN\gvpndows.exe).Hash.Substring(0,16))"

# the app: Program Files (admin-only writable), Start menu entry
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
Copy-Item (Join-Path $here "app") $app -Recurse
$lnk = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\Gnosis VPN.lnk"
$s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk); $s.TargetPath = Join-Path $app "GnosisVPN.exe"; $s.WorkingDirectory = $app; $s.Save()
Say "app installed in $app"

Start-Service GnosisVPN
$ok = $false
foreach ($i in 1..15) {
  try {
    $c = New-Object System.IO.Pipes.NamedPipeClientStream(".", "gnosisvpn", [System.IO.Pipes.PipeAccessRights]"Read, WriteData, Synchronize", [System.IO.Pipes.PipeOptions]::None, [System.Security.Principal.TokenImpersonationLevel]::Anonymous, [System.IO.HandleInheritability]::None)
    $c.Connect(2000); $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
    $w.WriteLine('{"type":"x_state"}'); $null = $r.ReadLine(); $c.Dispose(); $ok = $true; break
  } catch { Start-Sleep 1 }
}
if (-not $ok) { throw "the service started but does not answer; log: C:\ProgramData\gvpndows\gvpndows.log" }
Say "service running and answering"

# start the app as the signed-in user, not elevated (Windows notifications need that)
if (-not $NoLaunch) { Start-Process explorer.exe -ArgumentList ('"' + (Join-Path $app "GnosisVPN.exe") + '"') }
Say "done"
