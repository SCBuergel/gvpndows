#!/bin/bash
# Runs ON the droplet: build the .exe, then smoke-test it under Wine (selftest + screenshot).
set -euo pipefail
. "$HOME/.cargo/env"
t=$(date +%s); step() { n=$(date +%s); echo "STEP $1 $((n - t))"; t=$n; }
TARGET=${TARGET:-x86_64-pc-windows-msvc}
cd /root/src
export XWIN_ACCEPT_LICENSE=1   # MSVC CRT + Windows SDK headers/libs, downloaded by cargo-xwin
GVPNDOWS_COMMIT=${GVPNDOWS_COMMIT:-unknown} cargo xwin build --release --locked --target "$TARGET"
mkdir -p /root/out && rm -f /root/out/*
cp "target/$TARGET/release/gvpndows.exe" /root/out/
step build

# Smoke test under Wine (not a Windows substitute: catches missing DLLs and crashes at start).
if [ "$TARGET" = x86_64-pc-windows-msvc ]; then
    export WINEDEBUG=-all WINEDLLOVERRIDES="mscoree,mshtml=" WINEPREFIX=/root/wine
    wine=$(command -v wine || command -v wine64 || echo /usr/lib/wine/wine64)
    rc=0; timeout 120 "$wine" /root/out/gvpndows.exe --selftest || rc=$?
    echo "WINE-SELFTEST rc=$rc"
    find /root/wine -name gvpndows.log -exec cat {} \; | sed 's/^/  wine-log: /' || true
    Xvfb :99 -screen 0 800x600x24 >/dev/null 2>&1 & xpid=$!
    sleep 2
    DISPLAY=:99 GVPNDOWS_AUTOCONNECT=1 timeout 60 "$wine" /root/out/gvpndows.exe >/dev/null 2>&1 & gpid=$!
    sleep 25
    DISPLAY=:99 import -window root /root/out/wine-screenshot.png && echo WINE-SCREENSHOT ok || echo WINE-SCREENSHOT failed
    kill $gpid $xpid 2>/dev/null || true
    find /root/wine -name gvpndows.log -exec cp {} /root/out/wine-gvpndows.log \;
    step wine
fi
sha256sum /root/out/gvpndows.exe
