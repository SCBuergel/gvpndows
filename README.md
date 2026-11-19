# gvpndows

Gnosis VPN for Windows, built step by step (same approach as the Android app in `~/gvpndroid`):

1. **Pipeline:** a stub app cross-compiled from Linux and run on a real Windows laptop.
2. **A real tunnel without HOPR:** Wintun + WireGuard (neptun) to a plain WireGuard server.
3. **The Gnosis VPN client core** over a 1-hop HOPR path on prod. Built against gnosis_vpn-client
   `main` (8a0e3be6, version 1.1.1; set `CLIENT_REF` in `scripts/do-build/*.sh`), patched for Windows
   (`rust/patches`, `rust/crate-patches`). The client itself now requires `hopr-strategy` ~9.2 (its
   Curvy/PIX payment fixes); `BUMP=1` on a build moves the HOPR dependencies to their newest versions.
4. **Current: the Windows app.** A native WinUI 3 app (`winui/`) from the `~/mobile-UI` prototype,
   in front of a Windows service that runs the core and the tunnel.

## How the pieces fit

    WinUI app (GnosisVPN.exe, the signed-in user, unelevated)
        |  \\.\pipe\gnosisvpn, one JSON request per line
    service "GnosisVPN" (%ProgramFiles%\Gnosis VPN Service\gvpndows.exe --service, LocalSystem)
        |  client core (gnosis_vpn-lib) + Wintun adapter "GnosisVPN" + routes
    HOPR network -> exit

- **Service** (`src/service.rs`):
  - Requests: `x_state` (cached state), `x_start`, and the core commands `status`, `connect`,
    `disconnect`, `balance`, `destinations` and `nerd_stats`. Funding and probing commands are refused
    (`src/pure.rs`).
  - Pipe security: only interactive users may connect, and remote clients are refused. The first pipe
    instance must be the service's own, and one of its own always listens, so no other process can
    take over the name.
  - State: `%ProgramData%\GnosisVPN` and `%ProgramData%\gvpndows`. Both are owned by SYSTEM and readable
    by SYSTEM and Administrators only, because they hold the node key. If they can't be secured, the
    service doesn't start.
  - DNS: while the tunnel's routes are up, a WFP filter lets DNS (port 53) out only through the
    tunnel or loopback, as upstream sends all queries to the tunnel (`~.`). It lives in a dynamic
    WFP session, so it goes with the routes or when the service dies. Other ports (DoH) are not covered.
  - The service never connects on its own. It starts the core at once and retries every 10 s until
    the network allows it (Blokli must be reachable). When the core ends on its own (a final connect
    error, Blokli moved), it starts it again, after 5 s and backing off to 5 minutes.
  - Start type: `dist/windows/install.ps1` (the user install) makes it start with Windows (delayed)
    and restart after a crash.
  - `--serve` runs the same in an elevated console. The app then needs `GNOSISVPN_TRUST_PIPE=1`,
    because it otherwise trusts only the pipe served by the GnosisVPN service.
- **App** (`winui/GnosisVpn`, C# / .NET 10 / Windows App SDK 1.8):
  - It runs unelevated, because Windows App SDK toasts don't work elevated.
  - The service is its only backend: `Engine/RealBackend.cs` maps the core's state (run mode, exits
    and their route health, connection, PIX payment, balance, funding needs) onto what the pages
    show (`Engine/AppEngine.cs` holds the UI state). There is no demo or simulated backend; until
    the service answers, the app says it can't reach it and keeps polling.
- **Not done yet:**
  - Lockdown (a WFP kill switch for the HOPR tunnel) is not implemented, so the app has no switch for it.
  - There's no signed installer: `dist/windows/` is a zip with `Install.cmd` (admin prompt, then
    service, app in Program Files, Start menu entry) and `Uninstall.cmd`.

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
- **Server setup:** `setup-server.ps1` (once, over SSH): the standard user `gvuser` signed in
  automatically, .NET 8 and 10 SDKs, no display sleep.
- **Live tests:** `--snap DIR` (a screenshot every 10 s), `--click-connect QUERY` and
  `--click-disconnect-after N` drive the real app against the running service.
- **Tunnel test (milestone 3):** `gvpndows.exe --e2e PLAN` (run by `e2e.sh`) does staged downloads with
  per-second throughput. `pcap-summary.py` and `telemetry-summary.py` read its captures.
- **Speed test:** `speedtest/` measures a transfer second by second, in either direction.
- **Telemetry:** while a connection exists, the core writes `telemetry.csv` (1 row/s: stall and PIX
  counters, tunnel packets, ping to the exit) and `nerdstats.jsonl` next to its log. Each restarts at
  50 MB. `GVPNDOWS_TELEMETRY=0` turns them off.

## On Windows

- The .exe files are unsigned: SmartScreen shows "Windows protected your PC" → *More info* → *Run anyway*.
- Logs: the service in `%ProgramData%\gvpndows\gvpndows.log` (past 20 MB it moves to `gvpndows.old.log`), the app in `%LOCALAPPDATA%\GnosisVPN\ui.log`.
- The older egui app (`gvpndows.exe` without `--service`) logs to `%LOCALAPPDATA%\gvpndows\gvpndows.log`.

## Production release

Test builds contain a **debug-only developer relay**: the service streams its log (compressed and
encrypted to the developer's key) to a private ntfy.sh topic, at most 240 messages per client, and
sends an encrypted backup of the node identity once. This must not ship. For a release:

1. Build **without** `--features debug-relay`: `RELEASE=1 scripts/do-build/run.sh` (or
   `RELEASE=1 scripts/do-build/rebuild.sh` on a kept droplet). It passes `FEATURES=` (empty) and
   `GVPNDOWS_RELEASE=1` to the droplet build; with `GVPNDOWS_RELEASE` set, a build that still has
   the feature fails to compile.
2. `RELEASE=1` also leaves `GVPNDOWS_NTFY_TOPIC` and `GVPNDOWS_BACKUP_PUBKEY` unset.
3. Check: the service's `x_state` reports `"debug_relay": false`, its log has no
   "DEBUG BUILD" line, and the app shows no "Send log to developer (debug build)" button.

Everything involved is marked `DEBUG FEATURE ONLY`: `src/relay.rs`, `src/gnosis/backup.rs`, their
no-op release versions `src/relay_off.rs` and `src/gnosis/backup_off.rs`, the budget functions in
`src/pure.rs`, `x_send_log` in `src/service.rs`, and `DebugRelay` in the app.
