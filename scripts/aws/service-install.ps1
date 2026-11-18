# Install (or update) the Gnosis VPN service on the test server: C:\GnosisVPN\gvpndows.exe --service,
# demand start (never by itself), LocalSystem. Then start it and ask the pipe for x_state.
param([string]$Exe = "C:\gv\gvpndows-svc.exe")
$ErrorActionPreference = "Continue"
if (Get-Service GnosisVPN -ErrorAction SilentlyContinue) { Stop-Service GnosisVPN -Force -ErrorAction SilentlyContinue; Start-Sleep 2 }
# wait until the old service process has exited (it holds the .exe open)
foreach ($i in 1..30) { if (-not (Get-Process gvpndows -ErrorAction SilentlyContinue | Where-Object Path -eq "C:\GnosisVPN\gvpndows.exe")) { break }; Start-Sleep 1 }
Get-Process gvpndows -ErrorAction SilentlyContinue | Where-Object Path -eq "C:\GnosisVPN\gvpndows.exe" | Stop-Process -Force; Start-Sleep 1
New-Item -ItemType Directory -Force C:\GnosisVPN | Out-Null
# The LocalSystem service runs and reads what is in here: only SYSTEM and Administrators may change
# it (C:\ would let Authenticated Users modify a new subfolder). Owner Administrators, no inheritance.
icacls C:\GnosisVPN /setowner "*S-1-5-32-544" /T /C /Q | Out-Null
icacls C:\GnosisVPN /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" /C /Q | Out-Null
if (Get-ChildItem C:\GnosisVPN) { icacls C:\GnosisVPN\* /reset /T /C /Q | Out-Null }   # everything inside inherits the folder's ACL
Copy-Item $Exe C:\GnosisVPN\gvpndows.exe -Force
if ((Get-FileHash $Exe).Hash -ne (Get-FileHash C:\GnosisVPN\gvpndows.exe).Hash) { "INSTALL ABORTED: C:\GnosisVPN\gvpndows.exe is not the new build"; exit 1 }
"installed " + (Get-FileHash C:\GnosisVPN\gvpndows.exe).Hash.Substring(0, 16)
if (-not (Test-Path C:\GnosisVPN\gnosis)) { Copy-Item C:\gv\gvpndows\gnosis C:\GnosisVPN\gnosis -Recurse }
if (-not (Get-Service GnosisVPN -ErrorAction SilentlyContinue)) {
  New-Service -Name GnosisVPN -BinaryPathName '"C:\GnosisVPN\gvpndows.exe" --service' -DisplayName "Gnosis VPN" -StartupType Manual -Description "Runs the Gnosis VPN client core and the tunnel for the Gnosis VPN app." | Out-Null
}
icacls C:\GnosisVPN\* /reset /T /C /Q | Out-Null   # copied files take the folder's ACL
$weak = icacls C:\GnosisVPN /T | Select-String "(Users|Authenticated Users|Everyone|INTERACTIVE):\(.*(F|M|W)\)"
if ($weak) { "INSTALL ABORTED: writable by non-admins: $weak"; exit 1 }
Start-Service GnosisVPN; Start-Sleep 5
"service: " + (Get-Service GnosisVPN).Status
try {
  $c = New-Object System.IO.Pipes.NamedPipeClientStream(".", "gnosisvpn", [System.IO.Pipes.PipeDirection]::InOut); $c.Connect(5000)
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $rd = New-Object System.IO.StreamReader($c)
  $w.WriteLine('{"type":"x_state"}'); $l = $rd.ReadLine(); "pipe: " + $l.Substring(0, [Math]::Min(300, $l.Length)); $c.Dispose()
} catch { "pipe: " + $_.Exception.Message }
Get-Content C:\ProgramData\gvpndows\gvpndows.log -Tail 8 -ErrorAction SilentlyContinue | % { $_.Substring(0, [Math]::Min(200, $_.Length)) }
