# Removes the Gnosis VPN app and service. Keeps C:\ProgramData\GnosisVPN (the node identity and its funds).
$ErrorActionPreference = "Continue"
Get-Process GnosisVPN -ErrorAction SilentlyContinue | Stop-Process -Force
if (Get-Service GnosisVPN -ErrorAction SilentlyContinue) { Stop-Service GnosisVPN -Force; Start-Sleep 3; sc.exe delete GnosisVPN | Out-Null }
Remove-Item (Join-Path $env:ProgramFiles "Gnosis VPN") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\Gnosis VPN.lnk") -ErrorAction SilentlyContinue
Remove-Item C:\GnosisVPN -Recurse -Force -ErrorAction SilentlyContinue
"Gnosis VPN removed. The node identity stays in C:\ProgramData\GnosisVPN (delete it only if its funds are gone)."
