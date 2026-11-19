"""EC2 user data for up.py: SSH with the given key, and a deadline that ends the server.
The deadline is an absolute UTC time: the first-boot `shutdown /t` timer dies with any restart, so a
SYSTEM task re-checks it at every boot and every 5 minutes (shutdown = terminate, set by up.py)."""

def userdata(pub, now, hours):
    secs = int(hours * 3600); deadline = int(now) + secs
    return f"""<powershell>
shutdown /s /t {secs} /c "gvpndows-test deadline"
$ErrorActionPreference = 'Continue'
Set-Content -Path C:\\ProgramData\\gvpndows-deadline.ps1 -Encoding ascii -Value 'if ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() -ge {deadline}) {{ shutdown /a; shutdown /s /f /t 0 /c "gvpndows-test deadline" }}'
$a = New-ScheduledTaskAction -Execute powershell.exe -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\\ProgramData\\gvpndows-deadline.ps1'
$t = @((New-ScheduledTaskTrigger -AtStartup), (New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 5)))
Register-ScheduledTask -TaskName gvpndows-deadline -Action $a -Trigger $t -User SYSTEM -RunLevel Highest -Force | Out-Null
if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {{ Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 }}
Set-Content -Path C:\\ProgramData\\ssh\\administrators_authorized_keys -Value '{pub}' -Encoding ascii -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force C:\\ProgramData\\ssh | Out-Null
Set-Content -Path C:\\ProgramData\\ssh\\administrators_authorized_keys -Value '{pub}' -Encoding ascii
icacls C:\\ProgramData\\ssh\\administrators_authorized_keys /inheritance:r /grant "*S-1-5-32-544:F" /grant "SYSTEM:F"
New-ItemProperty -Path HKLM:\\SOFTWARE\\OpenSSH -Name DefaultShell -Value C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -PropertyType String -Force
Set-Service sshd -StartupType Automatic; Start-Service sshd
New-NetFirewallRule -Name sshd-gvpndows -DisplayName 'OpenSSH (gvpndows)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -ErrorAction SilentlyContinue
</powershell>""", deadline
