#!/usr/bin/env python3
"""Derive every other icon from the app icon, so the installer, the window, the taskbar and the
notification area all show the same picture (the key tile).

Reads  winui/GnosisVpn/Assets/appicon-256.png (rendered by `GnosisVPN.exe --gen-icons`, IconGen.cs)
Writes winui/GnosisVpn/Assets/tray-{off,ok,warn,bad}.ico  the tile, with a status dot except when off
       dist/windows/wizard-small-{55,110}.bmp             the installer's page icon
       dist/windows/wizard-{164,328}.bmp                  the installer's welcome/finish banner
Usage: python3 -I winui/tools/gen_icons.py [--check]
"""
import io, os, sys, tempfile, filecmp
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ASSETS = os.path.join(ROOT, "winui", "GnosisVpn", "Assets")
DIST = os.path.join(ROOT, "dist", "windows")
CHECK = "--check" in sys.argv
OUT_A, OUT_D = (tempfile.mkdtemp(), tempfile.mkdtemp()) if CHECK else (ASSETS, DIST)

tile = Image.open(os.path.join(ASSETS, "appicon-256.png")).convert("RGBA")
BG_TOP, BG_BOTTOM = (0x2a, 0x23, 0x47), (0x12, 0x0f, 0x22)   # the tile's gradient (IconGen.cs)

# notification area: the same tile, the state as a dot in the corner (tray-dot in app.css)
DOTS = {"off": None, "ok": (0x8D, 0xC6, 0x0C), "warn": (0xFC, 0xE1, 0x00), "bad": (0xC4, 0x2B, 0x1C)}
def tray(size, dot):
    big = 256
    im = tile.copy()
    if dot:
        d = big * 0.42; ring = big * 0.07
        draw = ImageDraw.Draw(im)
        x0, y0 = big - d - ring / 2, big - d - ring / 2
        draw.ellipse([x0 - ring, y0 - ring, big - ring / 2 + ring, big - ring / 2 + ring], fill=(0x20, 0x20, 0x24, 255))
        draw.ellipse([x0, y0, big - ring / 2, big - ring / 2], fill=dot + (255,))
    return im.resize((size, size), Image.LANCZOS)

def ico(path, imgs):
    """A multi-size ICO of PNG images (what IconGen.Ico writes), sizes in the given order."""
    pngs = []
    for im in imgs:
        b = io.BytesIO(); im.save(b, "PNG"); pngs.append((im.size[0], b.getvalue()))
    out = bytearray(b"\0\0\1\0" + len(pngs).to_bytes(2, "little"))
    off = 6 + 16 * len(pngs)
    for s, png in pngs:
        out += bytes([s % 256, s % 256, 0, 0]) + (1).to_bytes(2, "little") + (32).to_bytes(2, "little") + len(png).to_bytes(4, "little") + off.to_bytes(4, "little")
        off += len(png)
    for _, png in pngs: out += png
    with open(path, "wb") as f: f.write(out)

for state, dot in DOTS.items():
    ico(os.path.join(OUT_A, f"tray-{state}.ico"), [tray(s, dot) for s in (16, 20, 24, 32, 48)])

# installer: the small icon at the top right of each page, and the banner of the first and last page
def gradient(w, h):
    im = Image.new("RGB", (w, h))
    px = im.load()
    for y in range(h):
        for x in range(w):
            t = (x / w + y / h) / 2
            px[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(BG_TOP, BG_BOTTOM))
    return im

for s in (55, 110):
    im = Image.new("RGB", (s, s), (255, 255, 255))
    t = tile.resize((s, s), Image.LANCZOS); im.paste(t, (0, 0), t)
    im.save(os.path.join(OUT_D, f"wizard-small-{s}.bmp"))
for w in (164, 328):
    h = w * 314 // 164
    im = gradient(w, h)
    k = w * 3 // 4
    t = tile.resize((k, k), Image.LANCZOS); im.paste(t, ((w - k) // 2, (h - k) // 2), t)
    im.save(os.path.join(OUT_D, f"wizard-{w}.bmp"))

if CHECK:
    bad = [f for d, o, fs in ((ASSETS, OUT_A, [f"tray-{s}.ico" for s in DOTS]), (DIST, OUT_D, ["wizard-small-55.bmp", "wizard-small-110.bmp", "wizard-164.bmp", "wizard-328.bmp"]))
           for f in fs if not os.path.exists(os.path.join(d, f)) or not filecmp.cmp(os.path.join(d, f), os.path.join(o, f), shallow=False)]
    if bad: sys.exit("icons out of date, run winui/tools/gen_icons.py: " + ", ".join(bad))
    print("icons ok")
else:
    print("icons written")
