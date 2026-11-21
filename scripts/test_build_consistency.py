#!/usr/bin/env python3
"""The two build paths (GitHub Actions, the droplet scripts) and the version numbers stay in step,
the installer runs the same scripts as the zip, and CI builds exactly two flavours: release, and
DEBUG-logging (log relay only, never the identity backup), with "DEBUG-logging" in its file names.
Run: python3 -I THIS_FILE"""
import os, re

root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
def read(p): return open(os.path.join(root, p), encoding="utf-8").read()

wf = read(".github/workflows/windows-installer.yml")
build = read("scripts/do-build/build-remote.sh")
setup = read("scripts/do-build/setup-remote.sh")
iss = read("dist/windows/installer.iss")
uninstall = read("dist/windows/uninstall.ps1")

# same client commit everywhere
ref = re.search(r"^  CLIENT_REF: ([0-9a-f]{40})$", wf, re.M).group(1)
assert re.search(r"CLIENT_REF=\$\{CLIENT_REF:-" + ref + r"\}", build), "build-remote.sh CLIENT_REF differs from the workflow"
assert re.search(r"\$\{CLIENT_REF:-" + ref + r"\}", setup), "setup-remote.sh CLIENT_REF differs from the workflow"

# same Wintun archive
wsum = re.search(r"^  WINTUN_SHA256: ([0-9a-f]{64})$", wf, re.M).group(1)
assert re.search(r"^WINTUN_SHA256=" + wsum + "$", build, re.M), "Wintun hash differs"
wzip = re.search(r"^WINTUN_ZIP=(\S+)$", build, re.M).group(1)
assert wf.count("/builds/" + wzip) == 1, "Wintun version differs"

# one version in the three places
csproj = re.search(r"<Version>([^<]+)</Version>", read("winui/GnosisVpn/GnosisVpn.csproj")).group(1)
cargo = re.search(r'^version = "([^"]+)"', read("Cargo.toml"), re.M).group(1)
lock = re.search(r'name = "gvpndows"\nversion = "([^"]+)"', read("rust/Cargo.lock")).group(1)
assert csproj == cargo == lock, f"versions differ: csproj {csproj}, Cargo.toml {cargo}, rust/Cargo.lock {lock}"

# CI: no secrets (everything built there may be public); the release flavour sets GVPNDOWS_RELEASE=1;
# the only feature it enables is debug-relay (logs), never debug-identity-backup (the wallet key)
assert "secrets." not in wf, "the workflow must not read secrets (its builds may become public)"
assert "debug-identity-backup" not in wf, "CI must never build the identity backup"
assert re.findall(r"--features [\w-]+", wf) == ["--features debug-relay"], "CI enables a feature other than debug-relay"
assert re.search(r'if \[ "\$FLAVOUR" = release \]; then\n\s*export GVPNDOWS_RELEASE=1; features=""', wf), "the release flavour is not built with GVPNDOWS_RELEASE=1"
assert "flavour: [release, debug-logging]" in wf
# DEBUG-logging: visible in every file name, the Apps list and the installer's first page
for name in ("GnosisVPN-Setup-$env:VERSION$sfx.exe", "GnosisVPN-windows-$env:VERSION-DEBUG-logging.zip"):
    assert name in wf, name + " not built"
assert '#define Suffix "-DEBUG-logging"' in iss and "OutputBaseFilename=GnosisVPN-Setup-{#AppVersion}{#Suffix}" in iss
assert re.search(r'#define Shown "Gnosis VPN \(DEBUG-logging[^"]*"', iss) and "UninstallDisplayName={#Shown}" in iss
dbg, disc = read("dist/windows/DEBUG-logging.txt"), read("dist/windows/DISCLAIMER.txt")
assert read("dist/windows/DISCLAIMER-DEBUG-logging.txt") == dbg + disc, "DISCLAIMER-DEBUG-logging.txt is not DEBUG-logging.txt + DISCLAIMER.txt"
# the droplet's private test builds are the only ones with the identity backup
assert "--features debug-identity-backup" in build
# the patched crates are hash-checked
assert re.search(r'echo "\$sum  \$cv\.crate" \| sha256sum -c\n\s*tar -xzf "\$cv\.crate"', wf), "patched crates are not hash-checked before unpacking"

# the installer runs install.ps1 (with -File) and its uninstaller runs uninstall.ps1
assert re.search(r'-File "\' \+ Script \+ \'" -NoLaunch', iss), "installer.iss does not run install.ps1 -NoLaunch with -File"
assert "{tmp}\\GnosisVPN\\install.ps1" in iss
assert re.search(r'\[UninstallRun\][^\[]*-File ""\{app\}\\setup\\uninstall\.ps1""', iss), "uninstaller does not run uninstall.ps1"
for f in ("uninstall.ps1", "common.ps1"):
    assert re.search(r'Source: "\{#Payload\}\\' + re.escape(f) + r'"; DestDir: "\{app\}\\setup"', iss), f + " not kept for the uninstaller"
# Uninstall.cmd removes the installer's Apps entry (same AppId)
appid = re.search(r"^AppId=\{\{([0-9A-F-]{36})\}$", iss, re.M).group(1)
assert "{" + appid + "}_is1" in uninstall, "uninstall.ps1 does not remove the installer's entry"
# one icon everywhere: the installer, the .exe (taskbar, Explorer, Apps list) and the tray icons
# (winui/tools/gen_icons.py derives the tray and installer images from the app icon)
for key, files in (("SetupIconFile", ["../../winui/GnosisVpn/Assets/app.ico"]),
                   ("WizardImageFile", ["wizard-164.bmp", "wizard-328.bmp"]),
                   ("WizardSmallImageFile", ["wizard-small-55.bmp", "wizard-small-110.bmp"])):
    m = re.search(r"^" + key + r"=(.+)$", iss, re.M)
    assert m and [f.replace("\\", "/") for f in m.group(1).strip().split(",")] == files, key + " is not the app icon's"
    for f in files: assert os.path.isfile(os.path.join(root, "dist/windows", f)), f + " missing"
assert "<ApplicationIcon>Assets\\app.ico</ApplicationIcon>" in read("winui/GnosisVpn/GnosisVpn.csproj"), "the .exe has no app icon"
for st in ("off", "ok", "warn", "bad"): assert os.path.isfile(os.path.join(root, "winui/GnosisVpn/Assets", f"tray-{st}.ico"))
# the prototype disclaimer: shown by the installer before installing, in the README and the zip
assert re.search(r"^InfoBeforeFile=\{#Notice\}$", iss, re.M) and '#define Notice "DISCLAIMER.txt"' in iss and '#define Notice "DISCLAIMER-DEBUG-logging.txt"' in iss, "installer does not show the disclaimer"
assert "Prototype, for testing only" in read("README.md") and "prototype" in read("dist/windows/README.txt").splitlines()[0], "README or the zip readme lost the disclaimer"
print("build consistency ok")
