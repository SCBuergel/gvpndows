# Gnosis VPN for Windows: installs (or updates) the service and the app. Run by Install.cmd as admin.
# Every step prints what it does and how long it took; no step waits without a limit.
param([switch]$NoLaunch)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
. (Join-Path $here "common.ps1")
$app = Join-Path $env:ProgramFiles "Gnosis VPN"
Write-Host "Installing Gnosis VPN from $here`n"
Log "install from $here"

Step "Preparing the downloaded files"
Get-ChildItem $here -Recurse -File | Unblock-File   # the downloaded zip marks every file
Done

# an update: stop what runs now (bounded, with a countdown)
Stop-App
Stop-GnosisService 30

$oldBin = Get-ServiceBinPath
Step "Installing the service in $SvcDir (changeable by administrators only)"
New-Item -ItemType Directory -Force $SvcDir | Out-Null
Invoke-Icacls $SvcDir /setowner "*S-1-5-32-544" /T /C /Q
Invoke-Icacls $SvcDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" /C /Q
Copy-Item (Join-Path $here "service\gvpndows.exe") $SvcExe -Force
Copy-Item (Join-Path $here "service\gnosis") $SvcDir -Recurse -Force
Invoke-Icacls "$SvcDir\*" /reset /T /C /Q
# again after the copy, so the new files are covered too
Invoke-Icacls $SvcDir /setowner "*S-1-5-32-544" /T /C /Q
if ((Get-FileHash (Join-Path $here "service\gvpndows.exe")).Hash -ne (Get-FileHash $SvcExe).Hash) { throw "$SvcExe is not the new build" }
Assert-AdminOnly $SvcDir
$bin = '"' + $SvcExe + '" --service'
if (-not (Get-Service GnosisVPN -ErrorAction SilentlyContinue)) {
  New-Service -Name GnosisVPN -BinaryPathName $bin -DisplayName "Gnosis VPN" -StartupType Automatic -Description "Runs the Gnosis VPN client core and the tunnel for the Gnosis VPN app. It never connects by itself." | Out-Null
} elseif ($oldBin -ne $bin) {
  # an update may move it from the old folder. Through the service manager, not sc.exe: Windows
  # PowerShell 5.1 drops the quotes inside $bin when it starts a native program.
  $r = Get-CimInstance Win32_Service -Filter "Name='GnosisVPN'" | Invoke-CimMethod -MethodName Change -Arguments @{ PathName = $bin }
  if ($r.ReturnValue -ne 0) { throw "could not point the service at $SvcExe (error $($r.ReturnValue))" }
}
if ((Get-ServiceBinPath) -ne $bin) { throw "the service runs '$(Get-ServiceBinPath)', not '$bin'" }
# starts with Windows (delayed, after the network), restarted if it ever crashes; connecting stays the app's job
sc.exe config GnosisVPN start= delayed-auto | Out-Null
sc.exe failure GnosisVPN reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
Done ("build " + (Get-FileHash $SvcExe).Hash.Substring(0, 16))

Remove-LegacyDir $oldBin

Step "Installing the app in $app, with a Start menu entry"
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
Copy-Item (Join-Path $here "app") $app -Recurse
$lnk = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\Gnosis VPN.lnk"
$s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk); $s.TargetPath = Join-Path $app "GnosisVPN.exe"; $s.WorkingDirectory = $app; $s.Save()
Done

Step "Starting the service and waiting until it answers (at most 15 s)"
Start-Service GnosisVPN
$ok = $false
for ($i = 1; $i -le 15; $i++) {
  try {
    $c = New-Object System.IO.Pipes.NamedPipeClientStream(".", "gnosisvpn", [System.IO.Pipes.PipeAccessRights]"Read, WriteData, Synchronize", [System.IO.Pipes.PipeOptions]::None, [System.Security.Principal.TokenImpersonationLevel]::Anonymous, [System.IO.HandleInheritability]::None)
    $c.Connect(1000); $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
    $w.WriteLine('{"type":"x_state"}'); $null = $r.ReadLine(); $c.Dispose(); $ok = $true; break
  } catch { Write-Host ("`r           waiting: {0,2} s" -f $i) -NoNewline; Start-Sleep 1 }
}
if (-not $ok) { Write-Host ""; throw "the service started but does not answer; its log: C:\ProgramData\gvpndows\gvpndows.log" }
Done

if (-not $NoLaunch) {
  Step "Starting the app (as you, not as administrator: Windows notifications need that)"
  Start-Process explorer.exe -ArgumentList ('"' + (Join-Path $app "GnosisVPN.exe") + '"')
  Done
}
Write-Host ("`nGnosis VPN installed in {0:0} s." -f ((Get-Date) - $script:t0).TotalSeconds)
Log "done"
