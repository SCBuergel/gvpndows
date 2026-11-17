# gvpndows

Gnosis VPN for Windows, built step by step (same approach as the Android app in `~/gvpndroid`):

1. **Pipeline** (current): a stub app with one window (Connect/Disconnect, status line, log pane), cross-compiled
   from Linux and run on a real Windows laptop.
2. A real tunnel without HOPR: Wintun + WireGuard (neptun) to a plain WireGuard server.
3. The Gnosis VPN client core (v1.0.0) over a 1-hop HOPR path on prod.

## Build

Builds happen on a throwaway DigitalOcean droplet, not on this machine:

    scripts/do-build/run.sh              # droplet -> build -> Wine smoke test -> out/ -> delete droplet
    KEEP=1 scripts/do-build/run.sh       # keep the droplet; then scripts/do-build/rebuild.sh ...
    scripts/do-build/down.sh             # ... and delete it

Target `x86_64-pc-windows-msvc` via [cargo-xwin](https://github.com/rust-cross/cargo-xwin), C runtime linked
statically (`.cargo/config.toml`), so `out/gvpndows.exe` is a single file. A deadline timer deletes the droplet
even if the script dies. The token is read from `~/do-api-token`; nothing secret is in this repository.

## On Windows

The .exe is unsigned: SmartScreen shows "Windows protected your PC" → *More info* → *Run anyway*.
Log file: `%LOCALAPPDATA%\gvpndows\gvpndows.log` (the *Copy log* button copies it to the clipboard).
