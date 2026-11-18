#!/bin/bash
# All prototype states as app screenshots on the server, fetched and checked (check-shots.py).
# Usage: scripts/aws/shots.sh RUN_DIR [OUT_DIR]
set -euo pipefail
RUN=$1; HERE=$(cd "$(dirname "$0")" && pwd); REPO=$(cd "$HERE/../.." && pwd); OUT=${2:-$REPO/out/app-$(date +%H%M)}
WINSCP=1 "$HERE/winssh.sh" "$RUN" "$REPO/winui/tools/shots-all.txt" HOST:C:/gv/shots-all.txt
"$HERE/windesk.sh" "$RUN" "$HERE/shots-all.ps1" 1600
mkdir -p "$OUT"; WINSCP=1 "$HERE/winssh.sh" "$RUN" HOST:C:/gv/shots.zip "$OUT/shots.zip"; unzip -qo "$OUT/shots.zip" -d "$OUT"
python3 -I "$HERE/check-shots.py" "$REPO/winui/tools/shots-all.txt" "$OUT"
