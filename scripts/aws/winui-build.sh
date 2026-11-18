#!/bin/bash
# Build the WinUI 3 app on the AWS Windows server: upload winui/, dotnet publish (Release, x64,
# self-contained) to C:\gv\app. Prints errors and the summary. Usage: winui-build.sh RUN_DIR [Debug|Release]
set -euo pipefail
RUN=$1; CFG=${2:-Release}
REPO=$(cd "$(dirname "$0")/../.." && pwd); W="$REPO/scripts/aws/winssh.sh"
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
# the committed generated files must match the prototype they are generated from
if [ -d "$HOME/mobile-UI" ]; then python3 -I "$REPO/winui/tools/gen_assets.py" --check | tail -1 || { echo "GEN-ASSETS out of date: run winui/tools/gen_assets.py"; exit 1; }; fi
(cd "$REPO" && zip -qr "$TMP/winui.zip" winui -x 'winui/*/bin/*' 'winui/*/obj/*')
WINSCP=1 "$W" "$RUN" "$TMP/winui.zip" HOST:C:/gv/winui.zip
"$W" "$RUN" "
\$ErrorActionPreference = 'Continue'
Get-Process GnosisVPN -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path C:\gv\src\winui) { Get-ChildItem C:\gv\src\winui -Directory | ForEach-Object { Get-ChildItem \$_.FullName -Exclude bin,obj | Remove-Item -Recurse -Force } }
Expand-Archive -Force C:\gv\winui.zip C:\gv\src
# zip times carry no time zone (local US time read as UTC): mark the sources as new so MSBuild rebuilds
\$now = Get-Date; Get-ChildItem C:\gv\src\winui -Recurse -File | Where-Object { \$_.FullName -notmatch '\\\\(bin|obj)\\\\' } | ForEach-Object { \$_.LastWriteTime = \$now }
\$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; \$env:DOTNET_NOLOGO = '1'
\$t = Get-Date
C:\dotnet\dotnet.exe publish C:\gv\src\winui\GnosisVpn\GnosisVpn.csproj -c $CFG -r win-x64 -p:Platform=x64 -o C:\gv\app -v:minimal 2>&1 | Out-File C:\gv\build.log
'BUILD-EXIT ' + \$LASTEXITCODE + ' in ' + [int]((Get-Date) - \$t).TotalSeconds + ' s'
Get-Content C:\gv\build.log | Select-String -Pattern 'error|warning CS|-> ' | Select-Object -First 60 | ForEach-Object { \$_.Line.Trim() }
if (Test-Path C:\gv\app\GnosisVPN.exe) {
  Remove-Item C:\gv\selftest.txt -ErrorAction SilentlyContinue
  \$p = Start-Process C:\gv\app\GnosisVPN.exe -ArgumentList '--selftest','C:\gv\selftest.txt' -PassThru -Wait
  'SELFTEST-EXIT ' + \$p.ExitCode; Get-Content C:\gv\selftest.txt -ErrorAction SilentlyContinue
} else { 'SELFTEST-EXIT missing GnosisVPN.exe' }
"
