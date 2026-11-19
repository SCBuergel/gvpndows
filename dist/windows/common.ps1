# Shared by install.ps1 and uninstall.ps1: visible, time-bounded steps (nothing waits silently).

$script:t0 = Get-Date
# The service lives under Program Files, which only administrators can create folders in: a folder
# a standard user made first (C:\ is open to them) could keep a handle that survives an ACL reset.
$script:SvcDir = Join-Path $env:ProgramFiles "Gnosis VPN Service"
$script:SvcExe = Join-Path $script:SvcDir "gvpndows.exe"
# Builds before 0.2.1 installed the service in C:\GnosisVPN; an update removes it (Remove-LegacyDir).
$script:LegacyDir = "C:\GnosisVPN"
# The old folder is removed only if the installed service ran from it and it is still the folder
# the old installer made: not a link and owned by Administrators or SYSTEM. A folder a standard user
# made (C:\ is open to them) may hold junctions; rmdir /s /q never follows them (Windows
# PowerShell's Remove-Item -Recurse can, and would delete their targets as administrator).
function Get-ServiceBinPath { try { (Get-CimInstance Win32_Service -Filter "Name='GnosisVPN'" -ErrorAction Stop).PathName } catch { "" } }
function Remove-LegacyDir([string]$oldBinPath) {
  if (-not (Test-Path -LiteralPath $script:LegacyDir)) { return }
  if (-not ("$oldBinPath" -like "*$($script:LegacyDir)\gvpndows.exe*")) { Write-Host "           ($($script:LegacyDir) is not from Gnosis VPN; left alone)"; return }
  $item = Get-Item -LiteralPath $script:LegacyDir -Force
  # by SID: account names are localized (e.g. VORDEFINIERT\Administratoren)
  $owner = (Get-Acl -LiteralPath $script:LegacyDir).GetOwner([Security.Principal.SecurityIdentifier]).Value
  if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or ($owner -notin @("S-1-5-32-544", "S-1-5-18"))) {
    Write-Host "           ($($script:LegacyDir) is a link or owned by $owner, not by the old installer; left alone)"; return
  }
  Step "Removing the old service folder $($script:LegacyDir)"
  cmd.exe /c rmdir /s /q "$($script:LegacyDir)" 2>$null
  Done $(if (Test-Path -LiteralPath $script:LegacyDir) { "some files are still in use; delete it after a restart" } else { "" })
}
# Native icacls never throws (Windows PowerShell 5.1); a failed ACL step must stop the install.
function Invoke-Icacls { icacls @args | Out-Null; if ($LASTEXITCODE) { throw "icacls $args failed (exit $LASTEXITCODE)" } }
# The service folder and everything in it must be owned and changeable only by SYSTEM,
# Administrators and TrustedInstaller. By SID, not icacls text (names are localized).
$script:AdminSids = @("S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")
# WriteData/AddFile, AppendData/AddSubdirectory, WriteEA, DeleteChild, WriteAttributes, Delete,
# WRITE_DAC, WRITE_OWNER, GENERIC_ALL, GENERIC_WRITE
$script:WriteMask = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
function Assert-AdminOnly([string]$dir) {
  foreach ($p in @($dir) + @(Get-ChildItem -LiteralPath $dir -Recurse -Force | ForEach-Object { $_.FullName })) {
    $acl = Get-Acl -LiteralPath $p
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin $script:AdminSids) { throw "$p is owned by $owner, not by Administrators or SYSTEM" }
    foreach ($a in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
      if ($a.AccessControlType -eq "Allow" -and $a.IdentityReference.Value -notin $script:AdminSids -and ([int]$a.FileSystemRights -band $script:WriteMask)) {
        throw "$p is writable by non-admins: $($a.IdentityReference.Value) has $($a.FileSystemRights)"
      }
    }
  }
}
function Get-ServiceProcess { Get-Process gvpndows -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $script:SvcExe -or $_.Path -eq (Join-Path $script:LegacyDir "gvpndows.exe") } }

# A click into a console window starts a QuickEdit selection, which pauses the script until Enter
# is pressed (it looked like a hang). Turn QuickEdit off for this window only.
try {
  Add-Type -Namespace GnosisVpn -Name Console -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int n);
[DllImport("kernel32.dll")] public static extern bool GetConsoleMode(IntPtr h, out uint m);
[DllImport("kernel32.dll")] public static extern bool SetConsoleMode(IntPtr h, uint m);
'@
  $h = [GnosisVpn.Console]::GetStdHandle(-10)   # STD_INPUT_HANDLE
  $m = 0
  if ([GnosisVpn.Console]::GetConsoleMode($h, [ref]$m)) {
    # clear ENABLE_QUICK_EDIT_MODE (0x40), keep ENABLE_EXTENDED_FLAGS (0x80) so the change applies
    [void][GnosisVpn.Console]::SetConsoleMode($h, ($m -band (-bnot 0x40)) -bor 0x80)
  }
} catch { }
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
    $proc = Get-ServiceProcess
    if ($st -eq "Stopped" -and -not $proc) { Write-Host ""; Done; return }
    Write-Host ("`r           waiting: {0,2} s, service {1}   " -f $i, $st) -NoNewline
    Start-Sleep 1
  }
  Write-Host ""
  # A killed service counts as failed: without this the SCM restarts it (install.ps1's recovery
  # actions) while the files are being replaced. install.ps1 sets the actions again at its end.
  # Start-Process passes the empty actions= "" verbatim (PowerShell 5.1 drops "" for native commands).
  $sc = Start-Process sc.exe -ArgumentList 'failure GnosisVPN reset= 0 actions= ""' -Wait -NoNewWindow -PassThru
  if ($sc.ExitCode) { throw "could not turn off the service's restart on failure (sc.exe exit $($sc.ExitCode)); not ending its process" }
  Get-ServiceProcess | Stop-Process -Force
  for ($i = 0; $i -lt 15 -and ((Get-Service GnosisVPN).Status -ne "Stopped" -or (Get-ServiceProcess)); $i++) { Start-Sleep 1 }
  if ((Get-Service GnosisVPN).Status -ne "Stopped" -or (Get-ServiceProcess)) { throw "the service did not stop even after its process was ended" }
  Done "did not stop within $max s, so its process was ended (restart on failure is off until an install sets it again)"
}
