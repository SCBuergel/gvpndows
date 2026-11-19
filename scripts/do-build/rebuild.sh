#!/bin/bash
# Upload the source to the build droplet, build + Wine smoke test, copy results to out/.
# Uses the droplet from run.sh ($ip/$KNOWN_HOSTS) or the one kept by KEEP=1 (state file "current").
set -euo pipefail
cd "$(dirname "$0")/../.."
ST=$HOME/.local/state/gvpndows-do
if [ -z "${ip:-}" ]; then . "$ST/current"; KNOWN_HOSTS=$known_hosts; fi
SSH_OPTS=(-i "$ST/id_ed25519" -o StrictHostKeyChecking=accept-new -o "UserKnownHostsFile=$KNOWN_HOSTS"
          -o BatchMode=yes -o ConnectTimeout=5 -o ServerAliveInterval=30)
ssh_() { ssh "${SSH_OPTS[@]}" "root@$ip" "$@"; }
COMMIT=$(git rev-parse --short HEAD 2>/dev/null || echo none)$(git diff --quiet HEAD 2>/dev/null || echo -dirty)
# Only these source paths go up; never secrets.
ssh_ 'mkdir -p /root/src && find /root/src -mindepth 1 -maxdepth 1 ! -name target -exec rm -r {} +'
tar -czf - Cargo.toml build.rs .cargo src rust | ssh_ 'tar -xzf - -C /root/src'
scp -q "${SSH_OPTS[@]}" scripts/do-build/build-remote.sh "root@$ip:/root/upload/"
# Private relay topic for "send key/log to developer" (not in the repo; kept in ~/.config).
TOPIC=$(cat "$HOME/.config/gvpndows-secrets/ntfy-topic" 2>/dev/null || true)
# Public half of the identity-backup key (the private half never leaves this machine).
BACKUP_PUB=$(cat "$HOME/.config/gvpndows-secrets/backup-x25519.pub" 2>/dev/null || true)
# RELEASE=1: a production build, without the debug-only relay (see README "Production release").
if [ "${RELEASE:-0}" = 1 ]; then TOPIC=; BACKUP_PUB=; REL="FEATURES= GVPNDOWS_RELEASE=1"; else REL=; fi
ssh_ "$REL BUMP=${BUMP:-0} CORE_TEST=${CORE_TEST:-0} MODE=${MODE:-build} GVPNDOWS_BACKUP_PUBKEY=$BACKUP_PUB GVPNDOWS_NTFY_TOPIC=$TOPIC GVPNDOWS_COMMIT=$COMMIT nohup bash -c 'bash /root/upload/build-remote.sh; echo JOB-EXIT \$?' > /root/build.log 2>&1 &"
until ssh_ 'grep -q "^JOB-EXIT" /root/build.log' 2>/dev/null; do sleep 10; done
ssh_ 'grep -E "^(error|STEP|JOB-EXIT|WINE|MANIFEST|BUMPED|client |PURE|  wine-log)|: error|error\[|^warning: unused|gvpndows.exe$" /root/build.log | grep -v "^error: could not compile" | sort -u' || true
ssh_ 'grep -q "^JOB-EXIT 0" /root/build.log' || { ssh_ 'grep -A14 "^error" /root/build.log | head -150; tail -25 /root/build.log'; exit 1; }
[ "${MODE:-build}" = build ] || exit 0
mkdir -p out
for f in $(ssh_ 'ls /root/out'); do
    scp -q "${SSH_OPTS[@]}" "root@$ip:/root/out/$f" "out/$f.part" && mv "out/$f.part" "out/$f"
done
ls -l out/
sha256sum out/gvpndows.exe
