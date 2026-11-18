#!/usr/bin/env python3
"""Decrypt gvpndows identity backups from the relay and store them (mode 700 dir).
Usage: backup-decrypt.py OUT_DIR [SINCE=24h]. Prints only file names, sizes and the node
address found in hopr.id, never secret content."""
import base64, hashlib, json, os, sys, urllib.request
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305
S = os.path.expanduser("~/.config/gvpndows-secrets")
TAG = b"gvpndows-backup-v1"
priv = X25519PrivateKey.from_private_bytes(base64.b64decode(open(f"{S}/backup-x25519.key").read()))
P = priv.public_key().public_bytes_raw()
out, since = sys.argv[1], (sys.argv[2] if len(sys.argv) > 2 else "24h")
os.makedirs(out, mode=0o700, exist_ok=True); os.chmod(out, 0o700)
topic = open(f"{S}/ntfy-topic").read().strip()
n = 0
with urllib.request.urlopen(f"https://ntfy.sh/{topic}/json?poll=1&since={since}", timeout=30) as r:
    for line in r:
        m = json.loads(line)
        msg = m.get("message", "")
        if m.get("event") != "message" or not msg.startswith(TAG.decode() + " ") or m.get("title", "").startswith("gvpndows log"):
            continue
        blob = base64.b64decode(msg.split(" ", 1)[1])
        E, ct = blob[:32], blob[32:]
        shared = priv.exchange(X25519PublicKey.from_public_bytes(E))
        key = hashlib.sha256(TAG + shared + E + P).digest()
        files = json.loads(ChaCha20Poly1305(key).decrypt(b"\0" * 12, ct, E))
        d = os.path.join(out, f"backup-{m['time']}"); os.makedirs(d, mode=0o700, exist_ok=True)
        for name, b64 in files.items():
            data = base64.b64decode(b64)
            fd = os.open(os.path.join(d, name), os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
            os.write(fd, data); os.close(fd)
            print(f"{d}/{name}: {len(data)} bytes")
        try:
            print("  address in hopr.id:", json.loads(base64.b64decode(files["hopr.id"])).get("address"))
        except Exception:
            pass
        n += 1
print(f"{n} backup(s) decrypted")
