# AGENTS.md

Guide for coding agents working in this repository. Read `README.md` first for what the app is and
how the pieces fit (app, pipe, service, core); this file adds what you need to change it safely.

## Ground rules

- **No Windows build on a developer machine unless it is Windows.** The service compiles a whole
  HOPR node (thousands of crates, many GB of RAM); the app needs the Windows SDK. Build through
  GitHub Actions (push to `main`) or the rented-machine scripts (`scripts/do-build/`,
  `scripts/aws/`). Rented machines cost money: every script there deletes its machine on exit and
  arms a deadline that deletes it if the script dies; keep it that way in any new script.
- **Secrets never enter the repository, a log, a commit message or CI output:** node identities
  (`hopr.id`, `hopr.pass`), WireGuard configs (`*.conf`), API tokens, the relay topic and the
  backup key. `.gitignore` covers the usual file names. The repository is public.
- **Routing parity with upstream.** The tunnel must route exactly what the Linux client routes. Do
  not add Windows-only routes that send some traffic outside the tunnel (for example, to make a
  service such as Curvy reachable directly); fix the cause instead.
- **The debug relay must stay removable.** Anything that sends data to the developer belongs to the
  `debug-relay` feature (logs) or `debug-identity-backup` (the wallet key), is marked
  `DEBUG FEATURE ONLY`, and has a no-op twin for release builds (`src/relay_off.rs`,
  `src/gnosis/backup_off.rs`). `GVPNDOWS_RELEASE=1` must keep failing to compile a build that has
  either feature (`src/relay.rs`). GitHub Actions builds two flavours, release and DEBUG-logging
  (`debug-relay` only, "DEBUG-logging" in every file name), never reads repository secrets and
  never builds `debug-identity-backup`: its artifacts and releases are public. The identity backup
  exists only in private test builds from `scripts/do-build/`.

## Tests to run before every commit

All of these run on Linux in seconds:

    (cd rust/pure-tests && cargo test -q)
    for f in $(git ls-files '*.sh'); do bash -n "$f"; done
    python3 -I -c 'import ast,sys
    for f in sys.argv[1:]: ast.parse(open(f).read(), f)' $(git ls-files '*.py')
    for t in $(git ls-files 'scripts/*test_*.py'); do python3 -I "$t"; done

`rust/pure-tests` compiles `src/pure.rs` on its own (it is included by path) and holds tests named
after the review finding they guard (`r3_1_...`). Many of those tests read other files of the
repository (PowerShell, C#, other Rust files) and check their text, because that code cannot run on
Linux. When you change such a file, run the tests: a failing text check usually means you removed a
fix, not that the test is stale. Read the test before changing it.

Keep logic that can be tested without Windows in `src/pure.rs` (request filtering, parsing, budget
and backoff arithmetic) and test it there.

None of the above runs Windows code. A change to `src/win/`, `src/service.rs`, `src/gnosis/`, the
app or `dist/windows/` is only verified once a Windows build has run it: say so when you report
the change, and test it on the AWS server (`README.md`, "Test") when it affects the tunnel,
routes, DNS, the pipe or installation.

## Where things are decided

| Concern | Where | Notes |
| --- | --- | --- |
| Which pipe requests are allowed | `src/pure.rs` (`route_request`), `src/service.rs` | Funding and probing commands stay refused |
| Pipe security | `src/service.rs` | Interactive local users only; the service owns the first pipe instance and always keeps one listening, so no other process can take the name |
| State folder security | `src/service.rs` (`use_program_data`) | A folder the service did not create is moved aside, never adopted |
| Tunnel routes, IPv6 guard, peer bypass routes | `src/gnosis/root.rs`, `src/win/net.rs` | Requests from the core are applied in order |
| DNS filter | `src/win/wfp.rs` | Dynamic WFP session: it disappears with the service |
| Core lifecycle (start, restart, backoff) | `src/gnosis/mod.rs`, `core_restart` in `src/pure.rs` | Each core run has its own tokio runtime, shut down fully before a restart, so two HOPR nodes never run on one identity |
| What the app shows | `winui/GnosisVpn/Engine/RealBackend.cs` | Maps the core's status JSON to UI state |
| Install, update, uninstall | `dist/windows/*.ps1`, `dist/windows/installer.iss` | The installer only runs `install.ps1`; keep both paths identical |
| Build steps | `.github/workflows/windows-installer.yml`, `scripts/do-build/build-remote.sh` | Keep `CLIENT_REF`, the Wintun archive and the patch steps the same in both (`scripts/test_build_consistency.py` checks the first two) |

## Pitfalls that cost time before

- **Windows PowerShell 5.1** runs the install scripts (not PowerShell 7). It drops quotes inside
  arguments to native programs (`sc.exe config binPath=` broke; `install.ps1` uses CIM instead), and
  native programs never throw, so check `$LASTEXITCODE` (see `Invoke-Icacls`).
- **Account names are localized** (`VORDEFINIERT\Administratoren`): compare permissions by SID,
  never by name.
- **Folders under `C:\` and `C:\ProgramData` are writable by standard users** unless their ACLs say
  otherwise. Elevated scripts must not trust a folder a standard user could have created first, and
  must not follow junctions when deleting (`rmdir /s /q`, not `Remove-Item -Recurse`).
- **The app must run unelevated:** Windows App SDK notifications fail in an elevated process. The
  installer starts it with `runasoriginaluser`, `install.ps1` through `explorer.exe`.
- **WinUI timers:** a `DispatcherQueueTimer` that nothing references is garbage collected and
  stops. Keep a field.
- **Zip archives carry no time zone.** Sources extracted from a zip can look hours old, and MSBuild
  then skips them (`scripts/aws/winui-build.sh` touches them).
- **Funding amounts are in wei** and not round; round up, never down.
- **The core's data files** (`config.toml`, `geoip.mmdb`, `curvy-zk/`) are not in the repository;
  builds take them from the pinned Linux package. A new client version may need a newer package:
  update `DEB_URL` and `DEB_SHA256` together.

## Updating the client core

1. Set the new commit as `CLIENT_REF` in `.github/workflows/windows-installer.yml` and
   `scripts/do-build/build-remote.sh` (and its mention in `setup-remote.sh`).
2. Rebase `rust/patches/0001-windows-target.patch` onto it; check whether each crate patch in
   `rust/crate-patches/` is still needed.
3. Build without `--locked` once (the droplet build's `BUMP=1` does this) and commit the new lock as
   `rust/Cargo.lock`.
4. Run a Windows test (connect, traffic through the exit) before tagging a release.

## Releases

Bump `<Version>` in `winui/GnosisVpn/GnosisVpn.csproj`, `version` in `Cargo.toml` and the
`gvpndows` entry in `rust/Cargo.lock` together (the consistency test checks it), then push a tag
`v<version>`. The workflow publishes both installers and both zips (release and DEBUG-logging) and
`SHA256SUMS.txt` as a GitHub release.

## Commits

One topic per commit; the message says what changed and why. The owner is the only developer:
work on `main` directly (no feature branches) and push when the owner asks. Each push to `main`
builds both flavours on GitHub Actions.
