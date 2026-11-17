#!/usr/bin/env python3
"""Print what the app sent with "Send key/log to developer" (ntfy.sh topic from
~/.config/gvpndows-secrets/ntfy-topic). Usage: relay-read.py [SINCE, default 12h]"""
import json, os, sys, time, urllib.request
topic = open(os.path.expanduser("~/.config/gvpndows-secrets/ntfy-topic")).read().strip()
since = sys.argv[1] if len(sys.argv) > 1 else "12h"
with urllib.request.urlopen(f"https://ntfy.sh/{topic}/json?poll=1&since={since}", timeout=30) as r:
    for line in r:
        m = json.loads(line)
        if m.get("event") != "message":
            continue
        t = time.strftime("%H:%M:%S", time.localtime(m["time"]))
        print(f"=== {t} {m.get('title', '')}\n{m.get('message', '')}")
