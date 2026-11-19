#!/usr/bin/env python3
"""R4-2: relay-read.py skips forged messages and never prints control characters. Run: python3 -I THIS_FILE"""
import base64, hashlib, importlib.util, io, json, os, unittest, zlib
from contextlib import redirect_stdout
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey
from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305
HERE = os.path.dirname(os.path.abspath(__file__))


def load(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), os.path.join(HERE, name))
    m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
    return m


rr, bd = load("relay-read.py"), load("backup-decrypt.py")
PRIV = X25519PrivateKey.generate()


def seal(pt):
    """Mirror of backup.rs seal()."""
    P = PRIV.public_key().public_bytes_raw()
    e = X25519PrivateKey.generate(); E = e.public_key().public_bytes_raw()
    key = hashlib.sha256(rr.TAG + e.exchange(PRIV.public_key()) + E + P).digest()
    return f"{rr.TAG.decode()} " + base64.b64encode(E + ChaCha20Poly1305(key).encrypt(b"\0" * 12, pt, E)).decode()


def msg(title, body, t=1):
    return json.dumps({"event": "message", "time": t, "title": title, "message": body})


LOG = "gvpndows log c1 #1/240 (encrypted, zlib)"


class T(unittest.TestCase):
    def test_genuine_parts_are_shown(self):
        out = list(rr.render(PRIV, [msg(LOG, seal(zlib.compress(b"line 1\n\tline 2"))), msg("gvpndows log c1 #2/240", seal(b"plain part"))]))
        self.assertEqual(len(out), 2)
        self.assertTrue(out[0].endswith(f"{LOG}\nline 1\n\tline 2"))
        self.assertTrue(out[1].endswith("gvpndows log c1 #2/240\nplain part"))

    def test_forged_messages_are_skipped_and_reading_goes_on(self):
        T = rr.TAG.decode()
        bad = [b"not json", b"[]", json.dumps({"event": "message"}), msg(LOG, T + " AAAA"), msg(LOG, T + " !!"),
               msg(LOG, seal(b"not zlib")), msg(LOG, seal(zlib.compress(b"x")[:-3])), msg(LOG, "x", t="noon")]
        out = list(rr.render(PRIV, bad + [msg(LOG, seal(zlib.compress(b"after")))]))
        self.assertEqual(len(out), len(bad) + 1)
        self.assertTrue(all(o.startswith("=== (skipped an invalid message") for o in out[:-1]))
        self.assertTrue(out[-1].endswith("\nafter"))

    def test_big_log_part_is_cut(self):
        out = list(rr.render(PRIV, [msg(LOG, seal(zlib.compress(b"a" * (rr.MAX_LOG + 10))))]))[0]
        self.assertIn("truncated", out)
        self.assertLess(len(out), rr.MAX_LOG + 200)

    def test_no_control_characters_and_plain_text_is_marked(self):
        esc = "\x1b[2A\x1b[2K\x1b]52;c;eA==\x07"
        out = list(rr.render(PRIV, [msg("gvpndows log c1 #3/240" + esc + "\n", "fake\x1b[1m line" + esc),
                                    msg(LOG, seal(zlib.compress(("real" + esc).encode()))),
                                    msg("x", seal(b"{}"))]))
        for o in out:
            self.assertFalse(any(not c.isprintable() and c not in "\n\t" for c in o), repr(o))
        self.assertEqual(out[0].count("\n"), 1)
        self.assertIn(rr.PLAIN.strip(), out[0].split("\n")[0])
        self.assertNotIn(rr.PLAIN.strip(), out[1])
        self.assertIn(rr.PLAIN.strip(), out[2])

    def test_backup_address_is_filtered(self):
        files = {"hopr.id": json.dumps({"address": "0xab\x1b[2Kc"}).encode(), "hopr.pass": b"pw"}
        line = json.dumps({"event": "message", "time": 9, "message": seal(json.dumps({k: base64.b64encode(v).decode() for k, v in files.items()}).encode())})
        import tempfile
        with tempfile.TemporaryDirectory() as d, redirect_stdout(io.StringIO()) as f:
            self.assertEqual(bd.process(PRIV, d, [line]), 1)
        self.assertIn("address in hopr.id: 0xab?[2Kc", f.getvalue())
        self.assertNotIn("\x1b", f.getvalue())


if __name__ == "__main__":
    unittest.main()
