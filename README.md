# gvpndows

Gnosis VPN for Windows, built step by step (same approach as the Android app in `~/gvpndroid`):

1. **Pipeline:** a stub app cross-compiled from Linux and run on a real Windows laptop.
2. **A real tunnel without HOPR:** Wintun + WireGuard (neptun) to a plain WireGuard server.
3. **The Gnosis VPN client core** over a 1-hop HOPR path on prod. Built against gnosis_vpn-client
   `main` (00641e58, version 1.0.1; set `CLIENT_REF` in `scripts/do-build/*.sh`), patched for Windows
   (`rust/patches`, `rust/crate-patches`).
4. **Current: the Windows app.** A native WinUI 3 app (`winui/`) from the `~/mobile-UI` prototype,
   in front of a Windows service that runs the core and the tunnel.

## How the pieces fit

    WinUI app (GnosisVPN.exe, the signed-in user, unelevated)
        |  \\.\pipe\gnosisvpn, one JSON request per line
    service "GnosisVPN" (C:\GnosisVPN\gvpndows.exe --service, LocalSystem, demand start)
        |  client core (gnosis_vpn-lib) + Wintun adapter "GnosisVPN" + routes
    HOPR network -> exit

- **Service** (`src/service.rs`):
  - Requests: `x_state` (cached state), `x_start`, and the core commands `status`, `connect`,
    `disconnect`, `balance`, `destinations` and `nerd_stats`. Funding and probing commands are refused
    (`src/pure.rs`).
  - Pipe security: only interactive users may connect, and remote clients are refused. The first pipe
    instance must be the service's own.
  - State: `%ProgramData%\GnosisVPN` and `%ProgramData%\gvpndows`. Both are owned by SYSTEM and readable
    by SYSTEM and Administrators only, because they hold the node key. If they can't be secured, the
    service doesn't start.
  - The service never connects on its own.
  - `--serve` runs the same in an elevated console. The app then needs `GNOSISVPN_TRUST_PIPE=1`,
    because it otherwise trusts only the pipe served by the GnosisVPN service.
- **App** (`winui/GnosisVpn`, C# / .NET 10 / Windows App SDK 1.8):
  - It runs unelevated, because Windows App SDK toasts don't work elevated.
  - When the service's pipe exists, `Engine/RealBackend.cs` maps the core's state onto the
    prototype's state.
  - Otherwise, or with `--fake`, it runs the prototype's own fake backend (`Engine/FakeEngine.cs`, a
    port of `engine.js`), where every state can be reached from Demo controls.
- **Not done yet:**
  - Lockdown (the WFP kill switch for the HOPR tunnel) is only a switch in the app.
  - The core reports no latency or load per exit, so exits show a neutral grade.
  - There's no installer: `scripts/aws/service-install.ps1` sets up the service on a test machine.

## Build

Rust builds happen on a throwaway DigitalOcean droplet, not on this machine:

    scripts/do-build/run.sh              # droplet -> unit tests -> build -> Wine smoke test -> out/ -> delete droplet
    KEEP=1 scripts/do-build/run.sh       # keep the droplet; then scripts/do-build/rebuild.sh ...
    scripts/do-build/down.sh             # ... and delete it

- **Target:** `x86_64-pc-windows-msvc` via [cargo-xwin](https://github.com/rust-cross/cargo-xwin), with
  the C runtime linked statically (`.cargo/config.toml`).
- **Lock pin:** `rust/Cargo.lock` pins the whole workspace; builds run `--locked` when it exists.
- **Unit tests:** the platform-independent logic in `src/pure.rs` is tested on Linux in seconds with
  `cd rust/pure-tests && cargo test`; the droplet build runs these first.
- **WinUI app:** it builds on the Windows test server, with `scripts/aws/winui-build.sh RUN_DIR`
  (`dotnet publish`, then `GnosisVPN.exe --selftest`).

## Testing on a Windows server (`scripts/aws`)

- **Server:** `up.py`/`down.py` rent and delete a Windows Server 2025 instance in eu-north-1. Arm a
  `deadline` before `up.py`; the server also shuts itself down, and shutdown means terminate.
- **Helpers:** `winssh.sh` (SSH, multiplexed) and `windesk.sh` (scripts in the signed-in desktop
  session; the console auto-signs in as the standard user `gvuser`).
- **Screenshots:** `GnosisVPN.exe --shots LIST DIR` captures any prototype deep link at any window
  size; `check-shots.py` validates the PNGs. `ref-shots.ps1` captures the prototype in Edge, and
  `montage.ps1` builds side-by-side comparison sheets.
- **Live tests:** `--snap DIR`, `--click-connect QUERY` and `--click-disconnect-after N` drive the real
  app.
- **Tunnel test (milestone 3):** `gvpndows.exe --e2e PLAN` (run by `e2e.sh`) does staged downloads with
  per-second throughput. `pcap-summary.py` and `telemetry-summary.py` read its captures.
- **Speed test:** `speedtest/` measures a transfer second by second, in either direction.
- **Telemetry:** while a connection exists, the core writes `telemetry.csv` (1 row/s: stall and PIX
  counters, tunnel packets, ping to the exit) and `nerdstats.jsonl` next to its log. Each restarts at
  50 MB. `GVPNDOWS_TELEMETRY=0` turns them off.

## On Windows

- The .exe files are unsigned: SmartScreen shows "Windows protected your PC" → *More info* → *Run anyway*.
- Logs: the service in `%ProgramData%\gvpndows\gvpndows.log`, the app in `%LOCALAPPDATA%\GnosisVPN\ui.log`.
- The older egui app (`gvpndows.exe` without `--service`) logs to `%LOCALAPPDATA%\gvpndows\gvpndows.log`.
