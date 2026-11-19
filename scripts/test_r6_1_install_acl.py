#!/usr/bin/env python3
"""R6-1: the installer checks the service folder by SID, with every write/planting/takeover right,
and stops on a failed icacls. PowerShell cannot run here; this checks the scripts' text and the mask.
Run: python3 -I THIS_FILE"""
import os, re
d = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "dist", "windows")
common = open(os.path.join(d, "common.ps1")).read()
install = open(os.path.join(d, "install.ps1")).read()

expr = re.search(r"^\$script:WriteMask = (.+)$", common, re.M).group(1)
assert re.fullmatch(r"(0x[0-9a-fA-F]+)( -bor 0x[0-9a-fA-F]+)*", expr), expr
mask = 0
for h in re.findall(r"0x[0-9a-fA-F]+", expr):
    mask |= int(h, 16)
# WD, AD, WEA, DC, WA, D, WDAC, WO, GA, GW
for bit in (0x2, 0x4, 0x10, 0x40, 0x100, 0x10000, 0x40000, 0x80000, 0x10000000, 0x40000000):
    assert mask & bit, hex(bit)
assert mask & 0x1200A9 == 0, "ReadAndExecute (the Users grant) must not count as write"
assert mask < 2**31, "fits [int] in PowerShell"

for sid in ("S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"):
    assert f'"{sid}"' in re.search(r"^\$script:AdminSids = (.+)$", common, re.M).group(1), sid
assert "[Security.Principal.SecurityIdentifier]" in common and "-Recurse -Force" in common
assert "Assert-AdminOnly $SvcDir" in install and "Select-String" not in install
icacls = [l for l in install.splitlines() if "icacls " in l.lower()]
assert icacls and all(l.startswith("Invoke-Icacls ") for l in icacls), icacls
assert install.index("Copy-Item (Join-Path $here \"service\\gnosis\")") < install.rindex("/setowner") < install.index("Assert-AdminOnly")
print("ok")
