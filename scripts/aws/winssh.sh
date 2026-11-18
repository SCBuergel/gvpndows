#!/bin/bash
# winssh.sh RUN_DIR [ssh args/command...]  |  WINSCP=1 winssh.sh RUN_DIR scp-args (use HOST: as the remote prefix)
RUN=$1; shift
IP=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['ip'])" "$RUN/state.json")
O=(-i "$RUN/id_ed25519" -o UserKnownHostsFile="$RUN/known_hosts" -o BatchMode=yes -o ConnectTimeout=10 -o ServerAliveInterval=15 -o ControlMaster=auto -o "ControlPath=/tmp/gvw-%C" -o ControlPersist=300)
if [ "${WINSCP:-0}" = 1 ]; then exec scp -q "${O[@]}" "${@//HOST:/Administrator@$IP:}"; fi
exec ssh "${O[@]}" "Administrator@$IP" "$@"
