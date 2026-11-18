#!/usr/bin/env python3
"""Check a screenshot run: every name in LIST has a PNG in DIR, it is not near-empty, and its size
matches the window token in the name (wide/std/compact/narrow/max, default 1180x650).
Names with "closed" are skipped (the window is hidden). Usage: check-shots.py LIST DIR"""
import os, struct, sys
SIZES = {"wide": (1180, 650), "std": (960, 640), "compact": (720, 620), "narrow": (520, 640), "max": (1366, 720)}
lst, d = sys.argv[1], sys.argv[2]
bad, n = [], 0
for line in open(lst):
    parts = line.split()
    if len(parts) < 2 or line.startswith("#"): continue
    name = parts[0]
    if "-closed-" in name: continue
    n += 1
    want = next((v for k, v in SIZES.items() if f"-{k}-" in name), (1180, 650))
    p = os.path.join(d, name + ".png")
    if not os.path.exists(p): bad.append(f"{name}: missing"); continue
    data = open(p, "rb").read(32)
    if data[:8] != b"\x89PNG\r\n\x1a\n": bad.append(f"{name}: not a PNG"); continue
    w, h = struct.unpack(">II", data[16:24])
    if (w, h) != want: bad.append(f"{name}: {w}x{h}, expected {want[0]}x{want[1]}")
    if os.path.getsize(p) < 12000: bad.append(f"{name}: only {os.path.getsize(p)} bytes (blank?)")
print(f"{n - len(bad)} of {n} screenshots ok")
for b in bad: print("  " + b)
sys.exit(1 if bad else 0)
