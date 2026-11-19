# One-time setup of a fresh gvpndows test server (run over SSH as Administrator, then reboot):
# the standard user gvuser signed in automatically (UI tests run in its desktop session),
# .NET 8 and 10 SDKs in C:\dotnet (the WinUI build), and no display sleep or lock screen.
$ErrorActionPreference = "Continue"; $ProgressPreference = "SilentlyContinue"
New-Item -ItemType Directory -Force C:\gv | Out-Null
$b = New-Object byte[] 18; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
$pw = "Gv!" + [Convert]::ToBase64String($b)   # only on the server (registry), never printed
# New-LocalUser, not `net user /add`: net asks to confirm passwords over 14 characters, which over SSH fails silently
$sec = ConvertTo-SecureString $pw -AsPlainText -Force
if (-not (Get-LocalUser gvuser -ErrorAction SilentlyContinue)) { New-LocalUser gvuser -Password $sec -PasswordNeverExpires -UserMayNotChangePassword | Out-Null; Add-LocalGroupMember -Group Users -Member gvuser -ErrorAction SilentlyContinue }
else { Set-LocalUser gvuser -Password $sec }
if (-not (Get-LocalUser gvuser -ErrorAction SilentlyContinue)) { "SETUP-FAILED: could not create gvuser"; exit 1 }
$k = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
Set-ItemProperty $k AutoAdminLogon "1"; Set-ItemProperty $k DefaultUserName "gvuser"; Set-ItemProperty $k DefaultPassword $pw; Set-ItemProperty $k DefaultDomainName "."
Disable-ScheduledTask -TaskPath "\Microsoft\Windows\Server Manager\" -TaskName ServerManager -ErrorAction SilentlyContinue | Out-Null
New-ItemProperty -Path HKLM:\SOFTWARE\Microsoft\ServerManager -Name DoNotOpenServerManagerAtLogon -Value 1 -PropertyType DWord -Force | Out-Null
"gvuser: standard user, signs in automatically"
powercfg /change monitor-timeout-ac 0; powercfg /change standby-timeout-ac 0; powercfg /change monitor-timeout-dc 0
New-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name InactivityTimeoutSecs -Value 0 -PropertyType DWord -Force | Out-Null
New-Item -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Control Panel\Desktop" -Force | Out-Null
New-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Control Panel\Desktop" -Name ScreenSaveActive -Value "0" -PropertyType String -Force | Out-Null
New-Item -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization" -Force | Out-Null
New-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization" -Name NoLockScreen -Value 1 -PropertyType DWord -Force | Out-Null
"display: never sleeps, no lock screen"
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile C:\gv\dotnet-install.ps1
foreach ($ch in "8.0", "10.0") { & C:\gv\dotnet-install.ps1 -Channel $ch -InstallDir C:\dotnet 2>&1 | Select-Object -Last 1 }
[Environment]::SetEnvironmentVariable("DOTNET_ROOT", "C:\dotnet", "Machine")
$mp = [Environment]::GetEnvironmentVariable("Path", "Machine"); if ($mp -notmatch "C:\\dotnet") { [Environment]::SetEnvironmentVariable("Path", $mp + ";C:\dotnet", "Machine") }
"SDKs: " + ((C:\dotnet\dotnet.exe --list-sdks) -join ", ")
# the up.py deadline task re-arms the deadline at boot, so the first-boot timer can go (a pending one can block the reboot)
if (Get-ScheduledTask gvpndows-deadline -ErrorAction SilentlyContinue) { shutdown /a 2>$null; "deadline: boot task armed, timer cancelled for the reboot" }
"SETUP-DONE"
