#!/usr/bin/env python3
"""Print what the Windows service/app sent to the developer relay (ntfy.sh topic from
~/.config/gvpndows-secrets/ntfy-topic). Encrypted log parts are decrypted with the backup key;
identity backups are only named (decrypt them with backup-decrypt.py).
Usage: relay-read.py [SINCE, default 12h]"""
import base64, hashlib, json, os, sys, time, urllib.request, zlib
S = os.path.expanduser("~/.config/gvpndows-secrets")
TAG = b"gvpndows-backup-v1"
topic = open(f"{S}/ntfy-topic").read().strip()
since = sys.argv[1] if len(sys.argv) > 1 else "12h"

def unseal(msg):
    from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
    from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305
    priv = X25519PrivateKey.from_private_bytes(base64.b64decode(open(f"{S}/backup-x25519.key").read()))
    blob = base64.b64decode(msg.split(" ", 1)[1]); E, ct = blob[:32], blob[32:]
    key = hashlib.sha256(TAG + priv.exchange(X25519PublicKey.from_public_bytes(E)) + E + priv.public_key().public_bytes_raw()).digest()
    return ChaCha20Poly1305(key).decrypt(b"\0" * 12, ct, E)

with urllib.request.urlopen(f"https://ntfy.sh/{topic}/json?poll=1&since={since}", timeout=30) as r:
    for line in r:
        m = json.loads(line)
        if m.get("event") != "message":
            continue
        t = time.strftime("%H:%M:%S", time.localtime(m["time"]))
        title, msg = m.get("title", ""), m.get("message", "")
        if msg.startswith(TAG.decode() + " "):
            if title.startswith("gvpndows log"):
                raw = unseal(msg)
                msg = (zlib.decompress(raw) if "zlib" in title else raw).decode("utf-8", "replace")
            else:
                msg = "(encrypted identity backup: use backup-decrypt.py)"
        print(f"=== {t} {title}\n{msg}")
