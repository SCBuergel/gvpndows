# Removes the Gnosis VPN app and service. Keeps C:\ProgramData\GnosisVPN and C:\ProgramData\gvpndows (the node identity, its funds, the logs).
# Every step prints what it does and how long it took; no step waits without a limit.
$ErrorActionPreference = "Continue"
. (Join-Path $PSScriptRoot "common.ps1")
Write-Host "Removing Gnosis VPN`n"

$oldBin = Get-ServiceBinPath
Stop-App
Stop-GnosisService 30

if (Get-Service GnosisVPN -ErrorAction SilentlyContinue) {
  Step "Unregistering the service"
  $out = sc.exe delete GnosisVPN
  Done ($out -join " ").Trim()
}

Step "Removing the app (Program Files) and its Start menu entry"
Remove-Item (Join-Path $env:ProgramFiles "Gnosis VPN") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\Gnosis VPN.lnk") -ErrorAction SilentlyContinue
Done

# "Start with Windows" (the app's per-user Run value) of every signed-in user, only if it starts this app.
Step "Removing the app's Start with Windows entries"
$appExe = Join-Path $env:ProgramFiles "Gnosis VPN\GnosisVPN.exe"
$runKeys = @("HKCU:\Software\Microsoft\Windows\CurrentVersion\Run") + @(Get-ChildItem Registry::HKEY_USERS -ErrorAction SilentlyContinue |
  ForEach-Object { "Registry::$($_.Name)\Software\Microsoft\Windows\CurrentVersion\Run" })
$removed = 0
foreach ($k in $runKeys) {
  $v = [string](Get-ItemProperty -LiteralPath $k -Name GnosisVPN -ErrorAction SilentlyContinue).GnosisVPN
  $exe = if ($v -match '^\s*"([^"]*)"') { $Matches[1] } else { ($v -split ' --', 2)[0].Trim() }
  if ($v -and $exe -ieq $appExe) {
    Remove-ItemProperty -LiteralPath $k -Name GnosisVPN -ErrorAction SilentlyContinue
    $removed++
  }
}
Done "$removed removed; users who are not signed in keep theirs (it then starts nothing)"

# Installed by GnosisVPN-Setup.exe: its uninstaller lives in the service folder removed below, so
# after Uninstall.cmd its "Gnosis VPN" entry in Apps would point at nothing. (AppId in installer.iss)
$innoKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6F0C2A34-9E5B-4D1C-8B7A-0D9E3C5B2A61}_is1"
if (Test-Path $innoKey) {
  Step "Removing the installer's entry in Apps"
  Remove-Item $innoKey -Recurse -Force -ErrorAction SilentlyContinue
  Done
}

Step "Removing the service files ($SvcDir)"
if (Test-Path -LiteralPath $SvcDir) { cmd.exe /c rmdir /s /q "$SvcDir" 2>$null }
Done $(if (Test-Path -LiteralPath $SvcDir) { "some files are still in use; they go after a restart" } else { "" })
Remove-LegacyDir $oldBin

Write-Host ("`nGnosis VPN removed in {0:0} s." -f ((Get-Date) - $script:t0).TotalSeconds)
Write-Host "The node identity and its funds stay in C:\ProgramData\GnosisVPN (delete it only if its funds are gone)."
