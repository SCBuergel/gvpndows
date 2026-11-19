#!/usr/bin/env python3
"""Decrypt gvpndows identity backups from the relay and store them (mode 700 dir).
Usage: backup-decrypt.py OUT_DIR [SINCE=24h]. Prints only file names, sizes and the node
address found in hopr.id, never secret content.
A backup is unauthenticated: anyone holding a test exe can forge one. Match it against the node
address with verify-identity.py before funding. Only hopr.id and hopr.pass are written, each
message into a new backup-<time> directory, never over an existing file."""
import base64, hashlib, json, os, sys, urllib.request
from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305
TAG = b"gvpndows-backup-v1"
NAMES = {"hopr.id", "hopr.pass"}
MAX_MSG = 64 * 1024


def unseal(priv, msg):
    """The file map of a sealed backup message (as backup.rs seal() builds it)."""
    if len(msg) > MAX_MSG:
        raise ValueError("message too large")
    blob = base64.b64decode(msg.split(" ", 1)[1], validate=True)
    E, ct = blob[:32], blob[32:]
    P = priv.public_key().public_bytes_raw()
    key = hashlib.sha256(TAG + priv.exchange(X25519PublicKey.from_public_bytes(E)) + E + P).digest()
    files = json.loads(ChaCha20Poly1305(key).decrypt(b"\0" * 12, ct, E))
    if not isinstance(files, dict) or set(files) != NAMES or not all(isinstance(v, str) for v in files.values()):
        raise ValueError("unexpected file names or shape")
    return {k: base64.b64decode(v, validate=True) for k, v in files.items()}


def store(out, t, files):
    """Writes files into a new directory out/backup-<t>[-k]; returns it."""
    base = os.path.join(out, f"backup-{int(t)}")
    for k in range(100):
        d = base if k == 0 else f"{base}-{k}"
        try:
            os.mkdir(d, 0o700)
            break
        except FileExistsError:
            continue
    else:
        raise ValueError("no free backup directory")
    os.chmod(d, 0o700)
    for name, data in files.items():
        fd = os.open(os.path.join(d, name), os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "wb") as f:
            f.write(data)
        print(f"{d}/{name}: {len(data)} bytes")
    return d


def process(priv, out, lines):
    """Decrypts and stores each backup among the ntfy JSON lines; skips anything else. Returns the count."""
    n = 0
    for line in lines:
        try:
            m = json.loads(line)
            msg = m.get("message", "")
            if m.get("event") != "message" or not msg.startswith(TAG.decode() + " ") or m.get("title", "").startswith("gvpndows log"):
                continue
            files = unseal(priv, msg)
            store(out, m["time"], files)
        except (ValueError, KeyError, TypeError, AttributeError, OverflowError, RecursionError, InvalidTag, OSError) as e:
            print(f"skipped a message: {type(e).__name__}: not a valid backup")
            continue
        try:
            addr = str(json.loads(files["hopr.id"]).get("address"))
            print("  address in hopr.id:", "".join(c if c.isprintable() else "?" for c in addr))
        except Exception:
            pass
        n += 1
    return n


def main():
    S = os.path.expanduser("~/.config/gvpndows-secrets")
    priv = X25519PrivateKey.from_private_bytes(base64.b64decode(open(f"{S}/backup-x25519.key").read()))
    out, since = sys.argv[1], (sys.argv[2] if len(sys.argv) > 2 else "24h")
    os.makedirs(out, mode=0o700, exist_ok=True); os.chmod(out, 0o700)
    topic = open(f"{S}/ntfy-topic").read().strip()
    with urllib.request.urlopen(f"https://ntfy.sh/{topic}/json?poll=1&since={since}", timeout=30) as r:
        print(f"{process(priv, out, r)} backup(s) decrypted")


if __name__ == "__main__":
    main()
