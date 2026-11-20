# Gnosis VPN for Windows

A Windows app for [Gnosis VPN](https://gnosisvpn.io): it sends your internet traffic through the
HOPR mixnet to a VPN exit, and pays the relaying nodes per megabyte from a small wallet the app
creates for you. Under the hood it runs the official Gnosis VPN client core
([gnosis_vpn-client](https://github.com/gnosis/gnosis_vpn-client)), patched to build for Windows.

**Status: test builds.** The installer is not code-signed, there is no kill switch yet (see
[What it protects](#what-it-protects-and-what-not)), and the app talks to the production network
with real funds. Use it to try Gnosis VPN on Windows, not as your only protection.

## Install

You need Windows 10 (1809) or 11, 64-bit (x64), and an administrator account: the app installs a
Windows service that creates the tunnel.

1. Download `GnosisVPN-Setup-<version>.exe` from the
   [Releases page](https://github.com/SCBuergel/gvpndows/releases). Each release lists SHA-256 sums
   in `SHA256SUMS.txt`.
2. Run it and allow the administrator prompt. Because the file is not signed, Windows SmartScreen may
   say "Windows protected your PC": choose **More info**, then **Run anyway**.
3. At the end, leave "Start Gnosis VPN" ticked. Later, start it from the Start menu (**Gnosis VPN**).

The release also has a zip with the same contents, for machines where you would rather not run an
installer: extract it into your own Downloads folder (not directly under `C:\`, which the install
script refuses because other users can change files there), then double-click `Install.cmd`.

Installing over an older version is an update: the node identity and its funds stay.

## First start: fund your VPN

The first time it starts, the app creates a **node identity**: a key pair whose address holds the
funds that pay for your traffic. Until that address has funds, the app shows **Fund your VPN** with
the address as text and as a QR code, and the exact amounts it needs:

- **wxHOPR** on Gnosis Chain pays the relaying nodes for your traffic;
- **xDAI** on Gnosis Chain pays the network fees.

Send both to the address shown. The app notices the funds by itself, sets up the node's on-chain
account (a Safe), and then finds routes; that takes a few minutes the first time.

The identity lives in `C:\ProgramData\GnosisVPN`, readable only by SYSTEM and administrators.
Whoever has these files controls the funds. Uninstalling keeps them; delete that folder only once
its funds are spent or moved.

## Connect and choose a location

Click **Connect**. The app picks an exit; **Change** next to the location lists the others, each
with its route quality. Connecting takes from a few seconds (when the node has been running) to
about two minutes (right after Windows starts). While connected, the app shows the time connected,
and the balance page shows how much traffic your funds still cover.

Gnosis VPN pays for traffic in chunks. When a chunk is used up, the app buys the next one from the
node's funds; if those run out, traffic stops until you top up the same address.

## What it protects and what not

While connected:

- **All IPv4 traffic** goes through the tunnel, except the connections the HOPR node itself needs to
  reach its peers.
- **IPv6** is routed into the tunnel and dropped there (the tunnel carries IPv4 only), so programs
  fall back to IPv4.
- **DNS** (port 53) can leave the computer only through the tunnel: a Windows Filtering Platform
  rule blocks DNS queries to any other interface. Encrypted DNS (DNS over HTTPS, port 443) is not
  covered by that rule, but it is routed through the tunnel like other traffic.

Not provided yet:

- **No kill switch.** If the connection drops or you disconnect, traffic goes out directly again.
- **No protection before you connect.** The service starts with Windows but never connects by
  itself.

## Uninstall

**Settings > Apps > Installed apps > Gnosis VPN > Uninstall** (or `Uninstall.cmd` from the zip). It
removes the service, the app and the "Start with Windows" entry of every signed-in user, and keeps the node identity and the
logs in `C:\ProgramData`.

## When something goes wrong

- **"Can't reach the Gnosis VPN service":** the service starts about two minutes after Windows
  signs in. The app keeps retrying on its own. If it stays, restart Windows or reinstall.
- **Logs:** the service writes `C:\ProgramData\gvpndows\gvpndows.log` (readable by administrators;
  past 20 MB it moves to `gvpndows.old.log`), the app writes `%LOCALAPPDATA%\GnosisVPN\ui.log`. The
  installer's own steps are in `C:\Program Files\Gnosis VPN Service\setup\install.log`.
- **Debug builds** have a **Send log to developer** button; release builds have none (see
  [Release and debug builds](#release-and-debug-builds)).

---

# For developers

## How it works

    GnosisVPN.exe            WinUI 3 app, runs as the signed-in user (not elevated)
        |  named pipe \\.\pipe\gnosisvpn, one JSON request and one JSON reply per line
    gvpndows.exe --service   Windows service "GnosisVPN", runs as LocalSystem
        |  Gnosis VPN client core (HOPR node, WireGuard data plane in-process)
        |  Wintun adapter "GnosisVPN", routes, DNS filter
    HOPR mixnet  ->  VPN exit  ->  internet

The split exists because the two halves need opposite rights. Creating the tunnel adapter, routes
and firewall rules needs administrator rights, so the core runs inside a service. Windows
notifications from the app only work in an unelevated process, so the UI runs as the user and asks
the service for everything over the pipe.

**The service** (`src/service.rs`, `src/gnosis/`) starts the client core at boot and restarts it if
it ends (after 5 s, backing off to 5 minutes; the new core resumes the connection the user last
asked for). It plays the role that `gnosis_vpn-root` plays on Linux (`src/gnosis/root.rs`): it
brings up the tunnel when the core asks, routes `0.0.0.0/1` and `128.0.0.0/1` into it, keeps the
HOPR peers' addresses outside it, and turns the DNS filter on and off with those routes. The pipe
accepts only local interactive users and only a fixed set of requests (`src/pure.rs`); funding and
network-probing commands are refused. The service's state folders `%ProgramData%\GnosisVPN` and
`%ProgramData%\gvpndows` are owned by SYSTEM and readable only by SYSTEM and Administrators,
because they hold the node key.

**The app** (`winui/GnosisVpn`, C# / .NET 10 / Windows App SDK 1.8) has the service as its only
backend: `Engine/RealBackend.cs` turns the core's state (run mode, exits and route health,
connection, payments, balance, funding needs) into what the pages show; `Engine/AppEngine.cs` holds
the UI state. Its design follows a separate UI prototype; `winui/tools/gen_assets.py` generates
`Ui/Icons.g.cs` and `Ui/Tokens.g.cs` from it.

**The install scripts** (`dist/windows/`) are the same for the zip and the installer:
`install.ps1` copies the service to `%ProgramFiles%\Gnosis VPN Service` with administrator-only
permissions, registers the service (delayed automatic start, restart after a crash), copies the app
to `%ProgramFiles%\Gnosis VPN`, starts the service and waits until it answers on the pipe.
`installer.iss` (Inno Setup) unpacks the zip's folder and runs `install.ps1`; its uninstaller runs
`uninstall.ps1`.

## Repository layout

| Path | What it is |
| --- | --- |
| `src/` | The service (`gvpndows.exe`): `service.rs` pipe and service, `gnosis/` core integration, `win/` Wintun, routes, WFP, `pure.rs` platform-independent logic |
| `rust/patches/`, `rust/crate-patches/` | Patches that make gnosis_vpn-client and one third-party crate build for Windows |
| `rust/Cargo.lock` | The lock file of the whole build workspace (client + this crate) |
| `rust/pure-tests/` | Linux unit tests for `src/pure.rs` and checks on other files |
| `winui/` | The WinUI 3 app |
| `dist/windows/` | Install and uninstall scripts, the Inno Setup script, the zip's README |
| `.github/workflows/windows-installer.yml` | Builds the installer and the zip |
| `scripts/do-build/` | Alternative build of the service on a rented Linux machine (cross-compiled) |
| `scripts/aws/` | Tests on a rented Windows Server |
| `scripts/*.py` | Developer tools for debug builds (relay reader, backup decryption) |

## Build

The service is not a standalone crate: it builds as a member of a gnosis_vpn-client checkout, so
that it links the client core with the client's own dependency versions. A build:

1. checks out gnosis_vpn-client at the commit in `CLIENT_REF` (in the workflow and in
   `scripts/do-build/build-remote.sh`) and applies `rust/patches/*.patch`;
2. fetches each crate named in `rust/crate-patches/` and patches it in through `[patch.crates-io]`;
3. copies this repository's `Cargo.toml`, `build.rs`, `src/`, `.cargo/config.toml` and
   `rust/Cargo.lock` in as `gnosis_vpn-windows/`, and Wintun 0.14.1 (hash-checked) next to it;
4. runs `cargo build --locked --release --target x86_64-pc-windows-msvc -p gvpndows`.

The core also needs data files at run time (the production network config, `geoip.mmdb`, and the
payment proving keys in `curvy-zk/`). They come from the official Linux package
`gnosisvpn_1.0.0_amd64.deb` (hash-pinned) and are installed next to the service in `gnosis\`.

### On GitHub Actions (the normal way)

Every push to `main` or a `ci/**` branch, and every pull request, runs
`.github/workflows/windows-installer.yml`. It takes about 30 minutes, most of it compiling the
core:

| Job | Runner | Produces |
| --- | --- | --- |
| `data` | Ubuntu | the core's data files; runs `rust/pure-tests`, `bash -n` on the shell scripts and the Python tests |
| `service` | Windows | `gvpndows.exe`; its `--selftest` must pass |
| `app` | Windows | the WinUI app; its `--selftest` must pass |
| `package` | Windows | `GnosisVPN-Setup-<version>.exe`, `GnosisVPN-windows-<version>.zip`, `SHA256SUMS.txt`; checks the package's contents, then installs it silently on the runner, checks that the service runs, and uninstalls it |

The results are the run's `GnosisVPN-windows` artifact. Pushing a tag `v<version>` also publishes
them as a GitHub release (a pre-release when the tag contains a `-`, for example `v0.2.3-rc1`). The
version comes from `<Version>` in `winui/GnosisVpn/GnosisVpn.csproj`; keep `version` in `Cargo.toml`
and the `gvpndows` entry in `rust/Cargo.lock` the same.

### Release and debug builds

Debug builds include a **developer relay** (Cargo feature `debug-relay`): the service streams its
log, compressed and encrypted to the developer's key, to a private ntfy.sh topic (at most 240
messages per client), and sends an encrypted backup of the node identity once. This exists for
testing on machines the developer cannot reach and must not ship to users.

- GitHub Actions builds **release builds only**. A debug build carries the relay topic as plain
  text inside the .exe, and anything built there can end up in a public artifact or release, so
  debug builds are made only on the rented build machine (below) and handed out privately.
- A release build sets `GVPNDOWS_RELEASE=1`; with it, a build that still has the `debug-relay`
  feature fails to compile.
- Check a release build: the service's `x_state` reply has `"debug_relay": false`, its log has no
  "DEBUG BUILD" line, and the app shows no "Send log to developer" button.

Everything that belongs to the relay is marked `DEBUG FEATURE ONLY`: `src/relay.rs`,
`src/gnosis/backup.rs`, their no-op release versions `src/relay_off.rs` and
`src/gnosis/backup_off.rs`, the budget functions in `src/pure.rs`, `x_send_log` in
`src/service.rs`, and `DebugRelay` in the app. `scripts/relay-read.py` and
`scripts/backup-decrypt.py` read what it sends.

### On a rented Linux machine (alternative)

`scripts/do-build/run.sh` builds the service on a temporary DigitalOcean droplet with cargo-xwin
(cross-compiling to Windows), runs the unit tests and a Wine smoke test, copies `gvpndows.exe` to
`out/`, and deletes the droplet; a deletion deadline also deletes it if the script dies. It needs a
DigitalOcean token in `~/do-api-token` (or `DO_TOKEN_FILE`). It makes a debug build unless
`RELEASE=1` is set; the debug build takes the relay topic from
`~/.config/gvpndows-secrets/ntfy-topic` and the public backup key from
`~/.config/gvpndows-secrets/backup-x25519.pub` (without them it has no relay). Only files git tracks are uploaded. `KEEP=1` keeps the droplet for `rebuild.sh` until `down.sh`. The app has no equivalent: it builds
on Windows only (GitHub Actions or `scripts/aws/winui-build.sh`).

## Test

These run on Linux in seconds and need no Windows:

    (cd rust/pure-tests && cargo test)                         # src/pure.rs and checks on other files
    for f in $(git ls-files '*.sh'); do bash -n "$f"; done     # shell syntax
    for t in $(git ls-files 'scripts/*test_*.py'); do python3 -I "$t"; done   # needs python3-cryptography

`scripts/test_build_consistency.py` among them checks that the two build paths use the same client
commit and Wintun archive, that the three version numbers match, that CI cannot build a debug
build, and that the installer and the zip run the same install scripts.

Everything Windows-specific (the tunnel, routes, the DNS filter, the service, the installer) can
only be tested on Windows. `scripts/aws/` rents a Windows Server 2025 machine on AWS for that and
deletes it again:

- `up.py RUN_DIR HOURS` / `down.py RUN_DIR` create and delete it. The machine also shuts itself down
  at the deadline given to `up.py` (shutdown means terminate), even across reboots; arm a local
  deadline that runs `down.py` as well.
- `setup-server.ps1` (once, over SSH, then reboot) adds a standard user `gvuser` that signs in
  automatically, so that the app can run in a real desktop session.
- `winssh.sh` runs commands over SSH as Administrator; `windesk.sh` runs a script in `gvuser`'s
  desktop session.
- The app has test switches: `--selftest FILE`, `--snap DIR` (a screenshot every 10 s),
  `--click-connect EXIT` and `--click-disconnect-after N`.
- `speedrun.ps1` connects through the app and measures a 90 MB download and upload per exit.
- While a connection exists, the service writes `telemetry.csv` (one row per second: stall and
  payment counters, tunnel packets, ping to the exit) and `nerdstats.jsonl` next to its log; each
  restarts at 50 MB, and `GVPNDOWS_TELEMETRY=0` turns them off. `telemetry-summary.py` and
  `pcap-summary.py` summarize them and packet captures.

## License

GPL-3.0, see [LICENSE](LICENSE).
