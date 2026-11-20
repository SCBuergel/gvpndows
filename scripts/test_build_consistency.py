#!/usr/bin/env python3
"""The two build paths (GitHub Actions, the droplet scripts) and the version numbers stay in step,
the installer runs the same scripts as the zip, and CI never builds or publishes a debug build.
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

# CI builds release builds only: no secrets, no relay feature, GVPNDOWS_RELEASE set
assert "secrets." not in wf, "the workflow must not read secrets (a debug build would publish the relay topic)"
assert "--features" not in wf, "CI must not enable features (debug-relay)"
assert "GVPNDOWS_RELEASE=1" in wf
# the patched crates are hash-checked
seg = wf[wf.index("crate-patches"):wf.index("cargo build")]
assert "sha256sum -c" in seg.split("static.crates.io")[1], "patched crates are not hash-checked"

# the installer runs install.ps1 (with -File) and its uninstaller runs uninstall.ps1
assert re.search(r'-File "\' \+ Script \+ \'" -NoLaunch', iss), "installer.iss does not run install.ps1 -NoLaunch with -File"
assert "{tmp}\\GnosisVPN\\install.ps1" in iss
assert re.search(r'\[UninstallRun\][^\[]*-File ""\{app\}\\setup\\uninstall\.ps1""', iss), "uninstaller does not run uninstall.ps1"
for f in ("uninstall.ps1", "common.ps1"):
    assert re.search(r'Source: "\{#Payload\}\\' + re.escape(f) + r'"; DestDir: "\{app\}\\setup"', iss), f + " not kept for the uninstaller"
# Uninstall.cmd removes the installer's Apps entry (same AppId)
appid = re.search(r"^AppId=\{\{([0-9A-F-]{36})\}$", iss, re.M).group(1)
assert "{" + appid + "}_is1" in uninstall, "uninstall.ps1 does not remove the installer's entry"
print("build consistency ok")
