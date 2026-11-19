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

Step "Removing the service files ($SvcDir)"
if (Test-Path -LiteralPath $SvcDir) { cmd.exe /c rmdir /s /q "$SvcDir" 2>$null }
Done $(if (Test-Path -LiteralPath $SvcDir) { "some files are still in use; they go after a restart" } else { "" })
Remove-LegacyDir $oldBin

Write-Host ("`nGnosis VPN removed in {0:0} s." -f ((Get-Date) - $script:t0).TotalSeconds)
Write-Host "The node identity and its funds stay in C:\ProgramData\GnosisVPN (delete it only if its funds are gone)."
