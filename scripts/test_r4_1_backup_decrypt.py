#!/usr/bin/env python3
"""R4-1: backup-decrypt.py writes only hopr.id/hopr.pass into a new dir. Run: python3 -I THIS_FILE"""
import base64, hashlib, importlib.util, json, os, stat, tempfile, unittest
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey
from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305
spec = importlib.util.spec_from_file_location("bd", os.path.join(os.path.dirname(os.path.abspath(__file__)), "backup-decrypt.py"))
bd = importlib.util.module_from_spec(spec); spec.loader.exec_module(bd)
PRIV = X25519PrivateKey.generate()


def seal(files):
    """Mirror of backup.rs seal()."""
    P = PRIV.public_key().public_bytes_raw()
    e = X25519PrivateKey.generate(); E = e.public_key().public_bytes_raw()
    key = hashlib.sha256(bd.TAG + e.exchange(PRIV.public_key()) + E + P).digest()
    pt = json.dumps({k: base64.b64encode(v).decode() for k, v in files.items()}).encode()
    return f"{bd.TAG.decode()} " + base64.b64encode(E + ChaCha20Poly1305(key).encrypt(b"\0" * 12, pt, E)).decode()


GOOD = {"hopr.id": b'{"address":"0xabc"}', "hopr.pass": b"pw"}


class T(unittest.TestCase):
    def setUp(self):
        t = tempfile.TemporaryDirectory(); self.addCleanup(t.cleanup); self.out = t.name

    def test_genuine_roundtrip(self):
        d = bd.store(self.out, 1700000000, bd.unseal(PRIV, seal(GOOD)))
        self.assertEqual(d, os.path.join(self.out, "backup-1700000000"))
        self.assertEqual(stat.S_IMODE(os.stat(d).st_mode), 0o700)
        for k, v in GOOD.items():
            with open(os.path.join(d, k), "rb") as f:
                self.assertEqual(f.read(), v)
            self.assertEqual(stat.S_IMODE(os.stat(os.path.join(d, k)).st_mode), 0o600)

    def test_rejects_other_names(self):
        for bad in ({"/tmp/x": b"a", "hopr.pass": b"b"}, {"../hopr.id": b"a", "hopr.pass": b"b"}, dict(GOOD, extra=b"c"), {"hopr.id": b"a"}):
            with self.assertRaises(ValueError):
                bd.unseal(PRIV, seal(bad))

    def test_rejects_malformed(self):
        for msg in (f"{bd.TAG.decode()} !!notb64", f"{bd.TAG.decode()} " + base64.b64encode(b"x" * 64).decode()):
            with self.assertRaises(Exception):
                bd.unseal(PRIV, msg)

    def test_time_must_be_int_and_dir_new(self):
        with self.assertRaises(ValueError):
            bd.store(self.out, "../../x", GOOD)
        a = bd.store(self.out, 5, GOOD)
        b = bd.store(self.out, 5, GOOD)
        self.assertEqual(b, a + "-1")

    def test_does_not_follow_symlinks(self):
        os.symlink(self.out, os.path.join(self.out, "backup-7"))
        d = bd.store(self.out, 7, GOOD)
        self.assertEqual(d, os.path.join(self.out, "backup-7-1"))
        self.assertFalse(os.path.exists(os.path.join(self.out, "hopr.id")))

    def test_process_skips_bad_messages_and_keeps_going(self):
        T = bd.TAG.decode()
        lines = [b"not json", b"[]", json.dumps({"event": "message", "message": T + " !!", "time": 1}),
                 json.dumps({"event": "message", "message": seal(dict(GOOD, x=b"y")), "time": 2}),
                 json.dumps({"event": "message", "message": seal(GOOD), "time": "../3"}),
                 json.dumps({"event": "message", "message": seal(GOOD), "time": 4})]
        self.assertEqual(bd.process(PRIV, self.out, lines), 1)
        self.assertEqual(sorted(os.listdir(self.out)), ["backup-4"])


if __name__ == "__main__":
    unittest.main()
