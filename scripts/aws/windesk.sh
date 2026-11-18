#!/bin/bash
# Run a PowerShell script in the signed-in desktop session (console, session 1) of the AWS Windows
# server, wait for it, print its output. SSH commands run without a desktop; GUI apps and
# screenshots need this. Usage: windesk.sh RUN_DIR SCRIPT.ps1 [timeout_s]
set -euo pipefail
RUN=$1; PS=$2; TMO=${3:-300}; LEVEL=${LEVEL:-Limited}; DESKUSER=${DESKUSER:-gvuser}
W="$(cd "$(dirname "$0")" && pwd)/winssh.sh"
WINSCP=1 "$W" "$RUN" "$PS" HOST:C:/gv/desk.ps1
"$W" "$RUN" '
Remove-Item C:\gv\desk.out, C:\gv\desk.done -ErrorAction SilentlyContinue
$a = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command & { & C:\gv\desk.ps1 *> C:\gv\desk.out; Set-Content C:\gv\desk.done x }"
$p = New-ScheduledTaskPrincipal -UserId "'"$DESKUSER"'" -LogonType Interactive -RunLevel '"$LEVEL"'
Register-ScheduledTask -TaskName gv-desk -Action $a -Principal $p -Force | Out-Null
Start-ScheduledTask -TaskName gv-desk' >/dev/null
for i in $(seq 1 $(( TMO / 3 + 1 ))); do
    if "$W" "$RUN" 'Test-Path C:\gv\desk.done' 2>/dev/null | grep -q True; then break; fi
    sleep 3
done
"$W" "$RUN" 'Get-Content C:\gv\desk.out -ErrorAction SilentlyContinue'
