#!/bin/bash
# Build gvpndows.exe on a throwaway DigitalOcean droplet and copy it to out/.
# The droplet is deleted on every exit path; a deadline (systemd user timer) deletes it even if
# this script dies.
# Usage: scripts/do-build/run.sh
#        KEEP=1 scripts/do-build/run.sh   keep the droplet: iterate with rebuild.sh, end with down.sh
# Env: SIZE (c-8), REGION (ams3), DEADLINE_S (10800). Token: $DO_TOKEN_FILE (default ~/do-api-token).
set -euo pipefail
cd "$(dirname "$0")/../.."
REPO=$(pwd)
SIZE=${SIZE:-c-8}; REGION=${REGION:-ams3}; DEADLINE_S=${DEADLINE_S:-10800}
DEADLINE=${DEADLINE:-$HOME/.claude/plugins/cache/sebs-ai-tools/ephemeral-cloud-run/0.1.1/scripts/deadline}
ST=$HOME/.local/state/gvpndows-do; mkdir -p "$ST"; chmod 700 "$ST"
NAME=gvpndows-build-$(date +%s)
KNOWN_HOSTS=$ST/$NAME.known_hosts   # fresh per run: droplet IPs get reused
[ -f "$ST/id_ed25519" ] || ssh-keygen -q -t ed25519 -N '' -C gvpndows-build -f "$ST/id_ed25519"
t0=$(date +%s); t=$t0
step() { n=$(date +%s); echo "STEP $1 $((n - t))"; t=$n; }
id=""; ip=""
teardown() {
    rc=$?
    if [ -n "$id" ] && [ "${KEEP:-0}" = 1 ] && [ -n "$ip" ]; then
        printf 'id=%s\nip=%s\nname=%s\nknown_hosts=%s\nt0=%s\n' "$id" "$ip" "$NAME" "$KNOWN_HOSTS" "$t0" > "$ST/current"
        echo "KEPT droplet (exit $rc) $id at $ip (deadline $NAME armed); iterate: scripts/do-build/rebuild.sh, end: scripts/do-build/down.sh"
        exit $rc
    fi
    if [ -n "$id" ]; then
        if python3 -I scripts/do-build/droplet.py delete "$id"; then
            python3 "$DEADLINE" cancel "$NAME" >/dev/null 2>&1 || true
        else
            echo "!!! droplet $id NOT verified deleted; its deadline stays armed. Check https://cloud.digitalocean.com/droplets !!!" >&2
        fi
        echo "STEP lifetime $(( $(date +%s) - t0 ))"
    fi
    exit $rc
}
trap teardown EXIT
trap 'exit 130' INT TERM HUP

python3 -I scripts/do-build/droplet.py create "$NAME" "$SIZE" "$REGION" "$ST/id_ed25519.pub" > "$ST/$NAME.create" &
cpid=$!
for _ in $(seq 1 30); do id=$(head -1 "$ST/$NAME.create" 2>/dev/null || true); [ -n "$id" ] && break; sleep 1; done
[ -n "$id" ] || { wait $cpid || true; echo "droplet creation failed" >&2; exit 1; }
python3 "$DEADLINE" start "$NAME" "$DEADLINE_S" -- python3 -I "$REPO/scripts/do-build/droplet.py" delete "$id"
wait $cpid
ip=$(sed -n 2p "$ST/$NAME.create" | cut -d' ' -f1)
echo "droplet $id at $ip ($SIZE, \$$(sed -n 2p "$ST/$NAME.create" | cut -d' ' -f2)/h)"
step create

SSH_OPTS=(-i "$ST/id_ed25519" -o StrictHostKeyChecking=accept-new -o "UserKnownHostsFile=$KNOWN_HOSTS"
          -o BatchMode=yes -o ConnectTimeout=5 -o ServerAliveInterval=30)
ssh_() { ssh "${SSH_OPTS[@]}" "root@$ip" "$@"; }
for _ in $(seq 1 60); do ssh_ true 2>/dev/null && break; sleep 2; done
step ssh

ssh_ mkdir -p /root/upload
scp -q "${SSH_OPTS[@]}" scripts/do-build/setup-remote.sh scripts/do-build/build-remote.sh "root@$ip:/root/upload/"
ssh_ "bash /root/upload/setup-remote.sh" | grep -E '^STEP|^rustc|^cargo-xwin|SETUP-DONE'
step setup

KNOWN_HOSTS=$KNOWN_HOSTS ip=$ip scripts/do-build/rebuild.sh
step build-and-download
