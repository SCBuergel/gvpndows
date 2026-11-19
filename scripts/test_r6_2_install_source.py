#!/usr/bin/env python3
"""R6-2: install.ps1 refuses a source folder other users can change, before it loads or copies
anything from it. PowerShell cannot run here; this checks the script text and the mask.
Run: python3 -I THIS_FILE"""
import os, re
d = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "dist", "windows")
install = open(os.path.join(d, "install.ps1")).read()
readme = open(os.path.join(d, "README.txt")).read()

expr = re.search(r"^\$srcMask = (.+)$", install, re.M).group(1)
assert re.fullmatch(r"(0x[0-9a-fA-F]+)( -bor 0x[0-9a-fA-F]+)*", expr), expr
mask = 0
for h in re.findall(r"0x[0-9a-fA-F]+", expr):
    mask |= int(h, 16)
for bit in (0x2, 0x4, 0x10, 0x40, 0x100, 0x10000, 0x40000, 0x80000, 0x10000000, 0x40000000):
    assert mask & bit, hex(bit)
assert mask & 0x1200A9 == 0 and mask < 2**31

trusted = re.search(r"^\$trusted = (.+)$", install, re.M).group(1)
for sid in ("S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", "S-1-3-0"):
    assert f'"{sid}"' in trusted, sid
assert "$me" in trusted and "WindowsIdentity]::GetCurrent().User.Value" in install
assert not re.search(r'"S-1-5-(11|4|32-545)"|"S-1-1-0"', trusted), "other users must not be trusted"
check = install.index("Get-ChildItem -LiteralPath $here -Recurse -Force")
assert "[Security.Principal.SecurityIdentifier]" in install[check:check + 600]
for later in ('. (Join-Path $here "common.ps1")', "Unblock-File", "Copy-Item"):
    assert check < install.index(later), later
assert "Downloads" in readme
print("ok")
