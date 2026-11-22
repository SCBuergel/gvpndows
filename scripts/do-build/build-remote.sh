#!/bin/bash
# Runs ON the droplet. Builds inside a gnosis_vpn-client checkout (CLIENT_REPO at CLIENT_REF, default: side-car on client 8a0e3be (experiment 2)) (/root/client) with our
# Windows patch applied and this app added as the workspace member gnosis_vpn-windows, so the app
# links the client core against the client's own pinned Cargo.lock.
# MODE=check      only `cargo check` the app (and with it the patched core) for Windows: fast errors
# MODE=build      build gvpndows.exe, then smoke-test it under Wine
set -euo pipefail
. "$HOME/.cargo/env"
t=$(date +%s); step() { n=$(date +%s); echo "STEP $1 $((n - t))"; t=$n; }
TARGET=${TARGET:-x86_64-pc-windows-msvc}; MODE=${MODE:-build}
export XWIN_ACCEPT_LICENSE=1   # MSVC CRT + Windows SDK headers/libs, downloaded by cargo-xwin
# Test builds: no LTO, parallel codegen (fat LTO takes ~18 min and is mostly single-threaded).
export CARGO_PROFILE_RELEASE_LTO=false CARGO_PROFILE_RELEASE_CODEGEN_UNITS=16 CARGO_PROFILE_RELEASE_STRIP=true
# aws-lc-sys: use its prebuilt NASM objects when cross-compiling for Windows.
export AWS_LC_SYS_PREBUILT_NASM=1

# --- workspace: pristine client + patch + our crate ---
cd /root/client
CLIENT_REPO=${CLIENT_REPO:-https://github.com/SCBuergel/gnosis_vpn-client.git}
CLIENT_REF=${CLIENT_REF:-98de03843e214d895a85bc4c62a56433dffc359f}
git cat-file -e "$CLIENT_REF^{commit}" 2>/dev/null || git fetch -q "$CLIENT_REPO" "$CLIENT_REF"
git checkout -q -f "$CLIENT_REF" && git clean -qfdx -e target && echo "client $(git log --oneline -1)"
for p in /root/src/rust/patches/*.patch; do git apply "$p"; echo "applied $(basename "$p")"; done
# Third-party crates that need a Windows fix: rust/crate-patches/<crate>-<version>.patch is applied
# to a copy of the registry source and swapped in with [patch.crates-io].
first=1
for p in /root/src/rust/crate-patches/*.patch; do
    [ -e "$p" ] || continue
    cv=$(basename "$p" .patch); crate=${cv%-*}
    src=$(ls -d /root/.cargo/registry/src/*/"$cv" | head -1)
    rm -rf "patched/$cv"; mkdir -p patched; cp -r "$src" "patched/$cv"
    patch -s -p1 -d "patched/$cv" < "$p"
    if [ $first = 1 ]; then
        # the client may have a [patch.crates-io] table already; ours go into it, so it must be the last table
        grep -qx '\[patch.crates-io\]' Cargo.toml || printf '\n[patch.crates-io]\n' >> Cargo.toml
        [ "$(grep '^\[' Cargo.toml | tail -1)" = '[patch.crates-io]' ] || { echo "[patch.crates-io] is not the last table of the client's Cargo.toml"; exit 1; }
        first=0
    fi
    printf '%s = { path = "patched/%s" }\n' "$crate" "$cv" >> Cargo.toml
    echo "patched crate $cv"
done
mkdir -p gnosis_vpn-windows .cargo
cp -r /root/src/Cargo.toml /root/src/build.rs /root/src/src gnosis_vpn-windows/
cp /root/src/.cargo/config.toml .cargo/config.toml
LOCKED=--locked
if [ -f /root/src/rust/Cargo.lock ]; then cp /root/src/rust/Cargo.lock Cargo.lock; else LOCKED=; fi
# BUMP=1: move the HOPR dependencies to their newest compatible versions (hopr-strategy to the
# newest ~9.2 the client asks for, hoprnet master and edgli main to their branch
# heads), build without --locked, and return the new lock in /root/out (copy it to rust/Cargo.lock).
if [ "${BUMP:-0}" = 1 ]; then
    cargo update -p hopr-strategy 2>&1 | grep -E "Updating|Adding|Removing|Locking" | grep -iE "hopr|curvy|edgli" || true
    cargo update -p edgli 2>&1 | grep -E "Updating|Adding|Removing" | grep -iE "hopr|curvy|edgli" | head -20 || true
    cargo update -p hopr-lib 2>&1 | grep -E "Updating|Adding|Removing" | head -40 || true
    grep -o 'git+https://github.com/hoprnet/[a-z-]*?branch=[a-z]*#[0-9a-f]\{8\}' Cargo.lock | sort -u | sed 's/^/BUMPED /'
    grep -A1 -E '^name = "(hopr-strategy|curvy-sdk|edgli|hopr-lib)"$' Cargo.lock | grep version | sed 's/^/BUMPED /'
    LOCKED=
fi

# Wintun (signed driver DLL from the WireGuard project), embedded into the .exe.
WINTUN_ZIP=wintun-0.14.1.zip
WINTUN_SHA256=07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51
mkdir -p /root/cache
[ -f /root/cache/$WINTUN_ZIP ] || { curl -sSfL -o /root/cache/$WINTUN_ZIP.part https://www.wintun.net/builds/$WINTUN_ZIP && mv /root/cache/$WINTUN_ZIP.part /root/cache/$WINTUN_ZIP; }
echo "$WINTUN_SHA256  /root/cache/$WINTUN_ZIP" | sha256sum -c --quiet || { echo "wintun zip hash mismatch"; exit 1; }
mkdir -p gnosis_vpn-windows/vendor && unzip -qo /root/cache/$WINTUN_ZIP -d gnosis_vpn-windows/vendor
step workspace

# Unit tests of the platform-independent logic (src/pure.rs), on the Linux host: fast, no client workspace.
(cd /root/src/rust/pure-tests && cargo test -q --locked 2>&1 | tail -4) | tee /root/pure-tests.log
grep -q "test result: ok" /root/pure-tests.log || { echo "PURE-TESTS failed"; exit 1; }
echo "PURE-TESTS ok"; step pure-tests

if [ "$MODE" = check ]; then
    cargo xwin check $LOCKED --target "$TARGET" -p gvpndows --message-format short ${FEATURES---features debug-identity-backup}
    mkdir -p /root/out && cp Cargo.lock /root/out/Cargo.lock
    step check
    exit 0
fi

# PRIVATE TEST BUILD: the developer relay (log stream) plus the encrypted identity backup.
# A production release builds WITHOUT `--features debug-relay` and with GVPNDOWS_RELEASE=1
# (which makes a build that still has the feature fail): see README.md, "Production release".
FEATURES=${FEATURES---features debug-identity-backup}
GVPNDOWS_BACKUP_PUBKEY=${GVPNDOWS_BACKUP_PUBKEY:-} GVPNDOWS_NTFY_TOPIC=${GVPNDOWS_NTFY_TOPIC:-} GVPNDOWS_COMMIT=${GVPNDOWS_COMMIT:-unknown} \
    cargo xwin build $LOCKED --release --target "$TARGET" -p gvpndows $FEATURES
mkdir -p /root/out && rm -f /root/out/*
cp "target/$TARGET/release/gvpndows.exe" /root/out/
cp Cargo.lock /root/out/Cargo.lock   # pin: copied to rust/Cargo.lock for the next builds
grep -c requireAdministrator /root/out/gvpndows.exe >/dev/null && echo "MANIFEST requireAdministrator embedded" || echo "MANIFEST missing"
step build

# Smoke test under Wine (not a Windows substitute: catches missing DLLs and crashes at start).
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
# Core start under Wine, if the data files were uploaded to /root/gnosis (CORE_TEST=1).
if [ "${CORE_TEST:-0}" = 1 ] && [ -d /root/gnosis ]; then
    rm -rf /root/coretest && mkdir -p /root/coretest && cp /root/out/gvpndows.exe /root/coretest/ && ln -s /root/gnosis /root/coretest/gnosis
    rc=0; timeout 200 "$wine" /root/coretest/gvpndows.exe --core-test || rc=$?
    echo "WINE-CORETEST rc=$rc"
    find /root/wine -name gvpndows.log -exec cp {} /root/out/wine-coretest.log \;
    step coretest
fi
sha256sum /root/out/gvpndows.exe
