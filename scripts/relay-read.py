#!/usr/bin/env python3
"""Print what the Windows service/app sent to the developer relay (ntfy.sh topic from
~/.config/gvpndows-secrets/ntfy-topic). Encrypted log parts are decrypted with the backup key;
identity backups are only named (decrypt them with backup-decrypt.py). Anyone holding a test exe
can post to the topic: messages that are not decrypted log parts are marked, control characters
are shown as '?', and a malformed message is skipped. The sealing key ships in every exe, so a
decrypted part can be forged too.
Usage: relay-read.py [SINCE, default 12h]"""
import base64, binascii, hashlib, json, os, sys, time, urllib.request, zlib
TAG = b"gvpndows-backup-v1"
MAX_LOG = 4 << 20  # decompressed bytes shown per log part
PLAIN = "[NOT DECRYPTED, anyone can post this] "


def clean(s, multiline=False):
    """s with every non-printable character (but newline and tab in a multi-line text) as '?'."""
    return "".join(c if c.isprintable() or (multiline and c in "\n\t") else "?" for c in str(s))


def unseal(priv, msg):
    from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PublicKey
    from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305
    blob = base64.b64decode(msg.split(" ", 1)[1], validate=True); E, ct = blob[:32], blob[32:]
    key = hashlib.sha256(TAG + priv.exchange(X25519PublicKey.from_public_bytes(E)) + E + priv.public_key().public_bytes_raw()).digest()
    return ChaCha20Poly1305(key).decrypt(b"\0" * 12, ct, E)


def inflate(raw):
    d = zlib.decompressobj()
    out = d.decompress(raw, MAX_LOG)
    if d.unconsumed_tail or (not d.eof and len(out) >= MAX_LOG):
        return out + b"\n(log part truncated at 4 MiB)"
    if not d.eof:
        raise zlib.error("incomplete stream")
    return out


def render(priv, lines):
    """The text shown for each ntfy JSON line; a malformed message gives a one-line notice."""
    from cryptography.exceptions import InvalidTag
    for line in lines:
        try:
            m = json.loads(line)
            if m.get("event") != "message":
                continue
            t = time.strftime("%H:%M:%S", time.localtime(m["time"]))
            title, msg = str(m.get("title", "")), str(m.get("message", ""))
            if msg.startswith(TAG.decode() + " ") and title.startswith("gvpndows log"):
                raw = unseal(priv, msg)
                msg = (inflate(raw) if "zlib" in title else raw).decode("utf-8", "replace")
            elif msg.startswith(TAG.decode() + " "):
                title, msg = PLAIN + title, "(encrypted identity backup, not checked: use backup-decrypt.py)"
            else:
                title = PLAIN + title
        except (ValueError, KeyError, TypeError, AttributeError, OverflowError, OSError, binascii.Error, InvalidTag, zlib.error) as e:
            yield f"=== (skipped an invalid message: {type(e).__name__})"
            continue
        yield f"=== {t} {clean(title)}\n{clean(msg, multiline=True)}"


def main():
    from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey
    S = os.path.expanduser("~/.config/gvpndows-secrets")
    # --public: the topic of the public DEBUG-logging releases (built by GitHub Actions)
    public = "--public" in sys.argv
    args = [a for a in sys.argv[1:] if a != "--public"]
    topic = open(f"{S}/ntfy-topic-public" if public else f"{S}/ntfy-topic").read().strip()
    since = args[0] if args else "12h"
    priv = X25519PrivateKey.from_private_bytes(base64.b64decode(open(f"{S}/backup-x25519.key").read()))
    with urllib.request.urlopen(f"https://ntfy.sh/{topic}/json?poll=1&since={since}", timeout=30) as r:
        for block in render(priv, r):
            print(block)


if __name__ == "__main__":
    main()
