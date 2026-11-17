#!/usr/bin/env python3
"""Check that a backed-up HOPR identity (hopr.id + hopr.pass) decrypts and controls the given node
address. Run with the bench venv (eth_account): verify-identity.py DIR 0xNODEADDRESS
Prints only addresses and the plaintext's shape, never key material."""
import json, sys
from pathlib import Path
from eth_account import Account
from eth_keyfile import decode_keyfile_json
d, want = Path(sys.argv[1]), (sys.argv[2] if len(sys.argv) > 2 else "")
raw = decode_keyfile_json(json.loads((d / "hopr.id").read_text()), (d / "hopr.pass").read_bytes().strip())
# hopr-types (v2/v3 keystore): the plaintext is JSON {"packet_key", "chain_key", "babyjubjub_key"};
# the node address is the Ethereum address of chain_key. (v1: the plaintext is the chain key.)
if len(raw) == 32:
    chain = raw
else:
    k = json.loads(raw)["chain_key"]
    chain = bytes(k) if isinstance(k, list) else bytes.fromhex(k.removeprefix("0x"))
addr = Account.from_key(chain).address
ok = addr.lower() == want.lower()
print(f"decrypted the keystore; chain key address {addr}: {'MATCH' if ok else 'NO MATCH for ' + want}")
sys.exit(0 if ok else 1)
