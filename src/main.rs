//! gvpndows.exe: the Gnosis VPN service for Windows (core, tunnel, pipe for the app). It has no
//! window of its own; the app (winui/) is the user interface. Modes: --service (started by the
//! Service Control Manager), --serve (the same in a console), --selftest, --e2e, --core-test.
// No console window on Windows; the log file replaces stdout.
#![cfg_attr(windows, windows_subsystem = "windows")]

mod diag;
mod log;
mod pure;
// DEBUG FEATURE ONLY: the developer relay. Release builds (no `debug-relay` feature) compile the
// no-op src/relay_off.rs instead, so they contain no code that sends anything to the developer.
#[cfg_attr(not(feature = "debug-relay"), path = "relay_off.rs")]
mod relay;
mod sysinfo;
#[cfg(windows)]
mod gnosis;
#[cfg(windows)]
mod service;
#[cfg(windows)]
mod win;


fn main() {
    #[cfg(windows)]
    let svc = std::env::args().any(|a| a == "--service" || a == "--serve");
    #[cfg(windows)]
    if svc {
        if let Err(e) = service::use_program_data() {
            // fail closed: without private folders the node key would be readable by other users
            eprintln!("gvpndows: cannot secure the service folders: {e}");
            std::process::exit(3);
        }
    }
    let log_file = log::init();
    std::panic::set_hook(Box::new(|info| {
        log::line(format!("PANIC: {info}"));
    }));
    log::line("---- start ----");
    match &log_file {
        Ok(p) => log::line(format!("log file: {}", p.display())),
        Err(e) => log::line(format!("log file unavailable: {e}")),
    }
    for l in sysinfo::report() {
        log::line(l);
    }

    // --selftest: log the facts above (and, in debug builds, post to the relay), then exit
    // (used by the build's Wine check).
    if std::env::args().any(|a| a == "--selftest") {
        #[cfg(windows)]
        if gnosis::backup::available() {
            let r = gnosis::backup::seal(b"{\"selftest\": \"c2VsZnRlc3Q=\"}").and_then(|s| relay::post_text("gvpndows backup selftest", &s));
            log::line(format!("selftest: sealed backup post {:?}", r));
        }
        if relay::available() {
            match relay::selftest() {
                Ok(()) => log::line("selftest: relay post ok"),
                Err(e) => log::line(format!("selftest: relay post failed: {e}")),
            }
        }
        log::line("selftest ok");
        return;
    }

    // --service / --serve: the core and tunnel for the WinUI app (see service.rs)
    #[cfg(windows)]
    if svc {
        if std::env::args().any(|a| a == "--service") {
            if !service::run_service() {
                log::line("service: not started by the Service Control Manager; use --serve in a console");
            }
        } else {
            service::run_console();
        }
        return;
    }

    // --e2e PLAN: headless end-to-end test (see gnosis/e2e.rs), e.g. --e2e "ger@0,ger@60,fra@0".
    #[cfg(windows)]
    if let Some(i) = std::env::args().position(|a| a == "--e2e") {
        let plan = std::env::args().nth(i + 1).unwrap_or_default();
        let rc = gnosis::e2e::run(&plan);
        log::line(format!("e2e exit code {rc}"));
        std::process::exit(rc);
    }

    // --core-test: start the Gnosis VPN core headless, log its progress for 150 s, exit
    // (build check under Wine: identity, Blokli over HTTPS, HOPR node start).
    #[cfg(windows)]
    if std::env::args().any(|a| a == "--core-test") {
        if let Err(e) = gnosis::start() {
            log::line(format!("core-test: start failed: {e}"));
            return;
        }
        for _ in 0..30 {
            std::thread::sleep(std::time::Duration::from_secs(5));
            let mode = gnosis::STATUS.lock().unwrap().as_ref().map(|v| v["data"]["run_mode"]["type"].to_string());
            log::line(format!("core-test: core {:?}, run mode {:?}", gnosis::CORE_STATE.lock().unwrap(), mode));
        }
        gnosis::shutdown();
        log::line("core-test done");
        return;
    }

    // Started without a mode (for example double-clicked): the service has no window.
    let msg = "This is the Gnosis VPN service. Windows starts it; open the Gnosis VPN app from the Start menu instead.";
    log::line(msg);
    sysinfo::info_box(msg);
}
