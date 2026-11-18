#!/bin/bash
# Run the headless end-to-end test on the AWS Windows server and fetch every artifact.
# Usage: scripts/aws/e2e.sh RUN_DIR ZIP PLAN [OUT_DIR]
# Env: LOG_FILTER (tracing filter for the core; default = the app's), CAPTURE=1 (pktmon), MAX_MIN (90)
set -euo pipefail
RUN=$1; ZIP=$2; PLAN=$3; OUT=${4:-$HOME/gvpndows/out/e2e-$(date +%Y%m%d-%H%M%S)}
HERE=$(cd "$(dirname "$0")" && pwd); W="$HERE/winssh.sh"
mkdir -p "$OUT"
t0=$(date +%s); step() { echo "STEP $1 $(( $(date +%s) - t0 ))"; }
ME=$(curl -s https://api.ipify.org)
if [ "$ZIP" != - ]; then
    WINSCP=1 "$W" "$RUN" "$ZIP" HOST:C:/Users/Administrator/gv.zip
    "$W" "$RUN" 'Get-Process gvpndows -ErrorAction SilentlyContinue | Stop-Process -Force; Expand-Archive -Force C:\Users\Administrator\gv.zip C:\gv; (Get-FileHash C:\gv\gvpndows\gvpndows.exe).Hash'
    step upload
fi
python3 -c "import json,sys; print(json.dumps({'plan':sys.argv[1],'controller_ip':sys.argv[2],'log_filter':sys.argv[3],'capture':sys.argv[4]=='1'}))" \
    "$PLAN" "$ME" "${LOG_FILTER:-}" "${CAPTURE:-1}" > "$OUT/e2e-args.json"
WINSCP=1 "$W" "$RUN" "$OUT/e2e-args.json" "$HERE/e2e-remote.ps1" HOST:C:/gv/
"$W" "$RUN" 'schtasks /Delete /TN gvpndows-e2e /F 2>$null | Out-Null; Set-Content C:\gv\e2e-state.txt starting;
  schtasks /Create /TN gvpndows-e2e /RU SYSTEM /RL HIGHEST /SC ONCE /ST 00:00 /F /TR "powershell -NoProfile -ExecutionPolicy Bypass -File C:\gv\e2e-remote.ps1" | Out-Null;
  schtasks /Run /TN gvpndows-e2e'
step started
fetch() {
    "$W" "$RUN" 'Compress-Archive -Force -Path C:\Users\Administrator\AppData\Local\gvpndows\*.log, C:\Users\Administrator\AppData\Local\gvpndows\*.csv, C:\Users\Administrator\AppData\Local\gvpndows\*.jsonl, C:\Users\Administrator\AppData\Local\gvpndows\*.json, C:\gv\e2e-*.txt -DestinationPath C:\gv\results.zip -ErrorAction SilentlyContinue' >/dev/null 2>&1 || true
    WINSCP=1 "$W" "$RUN" HOST:C:/gv/results.zip "$OUT/results.zip.part" && mv "$OUT/results.zip.part" "$OUT/results.zip" && (cd "$OUT" && unzip -qo results.zip) || true
}
last=""
for i in $(seq 1 $(( ${MAX_MIN:-90} * 4 ))); do
    st=$("$W" "$RUN" 'Get-Content C:\gv\e2e-state.txt' 2>/dev/null | tr -d '\r' || echo "ssh-failed")
    line=$("$W" "$RUN" 'Get-Content C:\Users\Administrator\AppData\Local\gvpndows\gvpndows.log -Tail 400 | Select-String "e2e:|E2E-|tunnel ready|PANIC" | Select-Object -Last 1 | % Line' 2>/dev/null | tr -d '\r' | cut -c1-220 || true)
    [ "$line" != "$last" ] && [ -n "$line" ] && echo "  $line" && last=$line
    case "$st" in JOB-EXIT*) echo "$st"; break;; esac
    [ $((i % 20)) = 0 ] && fetch   # every 5 min: partial results
    sleep 15
done
step job
fetch
if [ "${CAPTURE:-1}" = 1 ]; then WINSCP=1 "$W" "$RUN" HOST:C:/gv/cap.pcapng "$OUT/cap.pcapng" 2>/dev/null && ls -l "$OUT/cap.pcapng" || echo "no capture"; fi
step fetch
echo "results in $OUT"
