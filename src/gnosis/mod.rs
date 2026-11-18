//! The Gnosis VPN client core (gnosis_vpn-lib v1.0.0) running inside this app. The core is the
//! desktop "worker"; this module plays "root" for it (see `root`), like the Android bridge.

pub mod backup;
pub mod e2e;
mod root;
pub mod telemetry;

use crate::log;
use gnosis_vpn_lib::command::WorkerCommand;
use gnosis_vpn_lib::core::Core;
use gnosis_vpn_lib::event::{CoreToWorker, RequestToRoot, ResponseFromRoot, WorkerToCore};
use gnosis_vpn_lib::worker_params::{AllowFlags, ConfigFileMode, WorkerParams};
use gnosis_vpn_ipc::dirs::UserDirs;
use std::net::{IpAddr, ToSocketAddrs};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::Duration;
use tokio::runtime::Runtime;
use tokio::sync::{mpsc, oneshot};

pub const NETWORK: &str = "piz-palu-prod";
pub const BLOKLI_URL: &str = "https://blokli.piz-palu.gnosisvpn.io";
/// Mutes path-selection chatter (millions of ERROR lines) but keeps the connection story.
pub const LOG_FILTER: &str = "warn,hopr_transport::path=off,gnosis_vpn_lib::core=info,gnosis_vpn_lib::connection=info,\
gnosis_vpn_lib::hopr=info,hopr_strategy::pix=info,hopr_lib::builder=info,hopr_transport::protocol::stream=off";

static RUNTIME: OnceLock<Runtime> = OnceLock::new();
static TO_CORE: Mutex<Option<mpsc::Sender<WorkerToCore>>> = Mutex::new(None);
static ROOT: Mutex<Option<root::Root>> = Mutex::new(None);
static STARTED: AtomicBool = AtomicBool::new(false);
/// The GUI's automatic check + log upload 30 s after connecting; off in --e2e (it would skew the measurements).
pub static AUTO_CHECK: AtomicBool = AtomicBool::new(true);
/// Last status reply as JSON (polled by a background thread for the UI).
pub static STATUS: Mutex<Option<serde_json::Value>> = Mutex::new(None);
pub static CORE_STATE: Mutex<String> = Mutex::new(String::new());

fn runtime() -> &'static Runtime {
    RUNTIME.get_or_init(|| tokio::runtime::Builder::new_multi_thread().enable_all().thread_name("gnosisvpn").build().expect("tokio runtime"))
}

fn set_state(s: impl Into<String>) {
    let s = s.into();
    log::line(format!("gnosis: {s}"));
    *CORE_STATE.lock().unwrap() = s;
}

pub fn running() -> bool {
    STARTED.load(Ordering::SeqCst)
}

/// Directory with the shipped data files (config.toml, geoip.mmdb, curvy-zk/): `gnosis\` next to the .exe.
fn share_dir() -> Result<PathBuf, String> {
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    let dir = exe.parent().ok_or("no exe dir")?.join("gnosis");
    for f in ["config.toml", "geoip.mmdb", "curvy-zk"] {
        if !dir.join(f).exists() {
            return Err(format!("{} is missing: extract the whole zip, not just the .exe", dir.join(f).display()));
        }
    }
    Ok(dir)
}

pub fn user_dirs() -> UserDirs {
    let base = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(std::env::temp_dir).join("GnosisVPN");
    UserDirs { data: base.join("data"), cache: base.join("cache"), logs: base.join("logs") }
}

/// The packaged v7 prod config with the data-file paths set (top-level keys, before any table).
fn assemble_config(template: &str, share: &Path) -> String {
    let mut out = format!(
        "version = 7\ngeoip_db = '{}'\ncurvy_zk_dir = '{}'\n",
        share.join("geoip.mmdb").display(),
        share.join("curvy-zk").display()
    );
    for line in template.lines().filter(|l| !l.trim_start().starts_with("version")) {
        out.push_str(line);
        out.push('\n');
    }
    out
}

/// Sends `tracing` output (the core's logs) into the app log.
fn init_tracing() {
    use tracing_subscriber::prelude::*;
    use tracing_subscriber::EnvFilter;
    static ONCE: std::sync::Once = std::sync::Once::new();
    ONCE.call_once(|| {
        struct W(Vec<u8>);
        impl std::io::Write for W {
            fn write(&mut self, b: &[u8]) -> std::io::Result<usize> {
                self.0.extend_from_slice(b);
                Ok(b.len())
            }
            fn flush(&mut self) -> std::io::Result<()> {
                Ok(())
            }
        }
        impl Drop for W {
            fn drop(&mut self) {
                let t = String::from_utf8_lossy(&self.0);
                let t = t.trim_end();
                if !t.is_empty() {
                    log::line(t);
                }
            }
        }
        let filter = EnvFilter::try_new(std::env::var("GVPNDOWS_LOG").unwrap_or_else(|_| LOG_FILTER.into()))
            .unwrap_or_else(|_| EnvFilter::new(LOG_FILTER));
        let fmt = tracing_subscriber::fmt::layer().with_ansi(false).without_time().with_writer(|| W(Vec::new()));
        let _ = tracing_subscriber::registry().with(filter).with(fmt).try_init();
    });
}

/// Starts the core in the background: identity, Blokli, HOPR node. Progress shows in the status.
pub fn start() -> Result<(), String> {
    if STARTED.swap(true, Ordering::SeqCst) {
        return Err("core already running".into());
    }
    let r = prepare_and_spawn();
    if r.is_err() {
        STARTED.store(false, Ordering::SeqCst);
    }
    r
}

fn prepare_and_spawn() -> Result<(), String> {
    init_tracing();
    let share = share_dir()?;
    let template = std::fs::read_to_string(share.join("config.toml")).map_err(|e| e.to_string())?;
    let config = gnosis_vpn_lib::config::parse(&assemble_config(&template, &share)).map_err(|e| format!("config: {e}"))?;
    // The PIX prover finds its proving files only through this variable (desktop root sets it for
    // the worker). Without it PIX payments fail and the exit holds inbound traffic.
    if RUNTIME.get().is_none() {
        // SAFETY: no runtime thread exists yet; the UI thread is the only one reading env vars.
        unsafe { std::env::set_var(gnosis_vpn_lib::hopr::CURVY_ZK_DIR_ENV, config.curvy_zk_dir.as_os_str()) };
    }
    let blokli_url = url::Url::parse(BLOKLI_URL).map_err(|e| e.to_string())?;
    let host = blokli_url.host_str().ok_or("blokli host")?.to_string();
    let blokli_ip = (host.as_str(), 443)
        .to_socket_addrs()
        .map_err(|e| format!("resolve {host}: {e}"))?
        .find_map(|a| match a.ip() {
            IpAddr::V4(v) => Some(v),
            _ => None,
        })
        .ok_or_else(|| format!("{host} has no IPv4 address"))?;
    let dirs = user_dirs();
    log::line(format!("gnosis: data in {}", dirs.data.display()));
    let params = WorkerParams::new(ConfigFileMode::Generated, AllowFlags::default(), blokli_url, blokli_ip, dirs, NETWORK.to_string());
    params.ensure_dirs().map_err(|e| format!("state dirs: {e}"))?;
    *ROOT.lock().unwrap() = Some(root::Root::new(blokli_ip));
    let (leak_tx, leak_rx) = std::sync::mpsc::sync_channel::<std::net::Ipv4Addr>(1024);
    root::set_leak_sender(leak_tx);
    std::thread::Builder::new()
        .name("hopr-bypass".into())
        .spawn(move || {
            while let Ok(ip) = leak_rx.recv() {
                if with_root(|r| r.bypass_leak(ip)) == Some(true) {
                    log::line(format!("bypass: HOPR traffic to {ip} was entering the tunnel (not in the core's peer list); routed around it"));
                }
            }
        })
        .map_err(|e| e.to_string())?;

    let (sender, mut from_core) = mpsc::channel(32);
    set_state(format!("starting (Blokli {blokli_ip}, network {NETWORK})"));
    runtime().spawn(async move {
        match Core::init(config, params, None, sender).await {
            Ok((core, to_core)) => {
                *TO_CORE.lock().unwrap() = Some(to_core);
                tokio::spawn(async move {
                    while let Some(CoreToWorker::RequestToRoot(req)) = from_core.recv().await {
                        handle_request(req);
                    }
                });
                set_state("running");
                core.start().await;
                TO_CORE.lock().unwrap().take();
                set_state("stopped");
            }
            Err(e) => set_state(format!("init failed: {e}")),
        }
        if let Some(mut r) = ROOT.lock().unwrap().take() {
            r.shutdown();
        }
        STARTED.store(false, Ordering::SeqCst);
    });
    start_status_poller();
    telemetry::start();
    Ok(())
}

fn respond(resp: ResponseFromRoot) {
    let Some(tx) = TO_CORE.lock().unwrap().clone() else { return };
    runtime().spawn(async move {
        let _ = tx.send(WorkerToCore::ResponseFromRoot(resp)).await;
    });
}

fn with_root<T>(f: impl FnOnce(&mut root::Root) -> T) -> Option<T> {
    ROOT.lock().unwrap().as_mut().map(f)
}

fn handle_request(req: RequestToRoot) {
    match req {
        RequestToRoot::KillswitchLockdown { request_id, peer_ips, .. } => {
            log::line(format!("gnosis: kill switch requested ({} peer IPs) - not enforced yet on Windows", peer_ips.len()));
            respond(ResponseFromRoot::KillswitchLockdown { request_id, res: Ok(()) });
        }
        RequestToRoot::SetupTunnel { request_id, interface_address, mtu, dns, peer_ips } => {
            // Blocking Win32 calls: off the runtime threads.
            std::thread::spawn(move || {
                let res = with_root(|r| r.setup_tunnel(&interface_address, mtu, dns.as_deref(), &peer_ips))
                    .unwrap_or_else(|| Err("core is stopping".into()));
                if let Err(e) = &res {
                    log::line(format!("gnosis: tunnel setup failed: {e}"));
                }
                respond(ResponseFromRoot::TunnelReady { request_id, res });
            });
        }
        RequestToRoot::TearDownWg => {
            std::thread::spawn(|| with_root(|r| r.teardown_routes()));
        }
        RequestToRoot::Ping { request_id, options } => {
            std::thread::spawn(move || {
                let res = root::icmp_ping(&options);
                if let Err(e) = &res {
                    log::line(format!("gnosis: in-tunnel ping: {e}"));
                }
                respond(ResponseFromRoot::Ping { request_id, res });
            });
        }
        RequestToRoot::UpdatePeerIps { peer_ips } => {
            std::thread::spawn(move || with_root(|r| r.update_peer_ips(&peer_ips)));
        }
        RequestToRoot::ProbeStopped => {}
    }
}

/// Sends a WorkerCommand as JSON ({"type":"status"}, {"type":"connect","destination":"<id>"},
/// {"type":"disconnect"}) and returns the core's Response as JSON. Blocking (up to 60 s).
pub fn command(json: &str) -> Result<serde_json::Value, String> {
    let cmd: WorkerCommand = serde_json::from_str(json).map_err(|e| format!("bad command: {e}"))?;
    let tx = TO_CORE.lock().unwrap().clone().ok_or("core is not running yet")?;
    runtime().block_on(async move {
        let (resp, rx) = oneshot::channel();
        tx.send(WorkerToCore::WorkerCommand { cmd, resp }).await.map_err(|_| "core stopped".to_string())?;
        let r = tokio::time::timeout(Duration::from_secs(60), rx)
            .await
            .map_err(|_| "core did not answer within 60 s".to_string())?
            .map_err(|_| "core dropped the command".to_string())?;
        serde_json::to_value(&r).map_err(|e| e.to_string())
    })
}

pub fn connect(destination: &str) {
    let cmd = serde_json::json!({"type": "connect", "destination": destination}).to_string();
    std::thread::spawn(move || match command(&cmd) {
        Ok(v) => log::line(format!("gnosis: connect -> {}", short(&v))),
        Err(e) => log::line(format!("gnosis: connect failed: {e}")),
    });
}

pub fn disconnect() {
    std::thread::spawn(|| match command(r#"{"type":"disconnect"}"#) {
        Ok(v) => log::line(format!("gnosis: disconnect -> {}", short(&v))),
        Err(e) => log::line(format!("gnosis: disconnect failed: {e}")),
    });
}

pub fn short(v: &serde_json::Value) -> String {
    let s = v.to_string();
    if s.len() > 300 { format!("{}...", &s[..s.char_indices().nth(300).map(|(i, _)| i).unwrap_or(s.len())]) } else { s }
}

/// Exit id -> "GVPN-GER-1 (DE)" for readable log lines.
pub fn dest_names(data: &serde_json::Value) -> std::collections::HashMap<String, String> {
    data["destinations"]
        .as_array()
        .map(|a| {
            a.iter()
                .map(|d| {
                    let x = &d["destination"];
                    let id = x["id"].as_str().unwrap_or("?").to_string();
                    let name = format!("{} ({})", x["meta"]["name"].as_str().unwrap_or(&id), x["geo"]["country"].as_str().unwrap_or("?"));
                    (id, name)
                })
                .collect()
        })
        .unwrap_or_default()
}

/// "connected to GVPN-GER-1 (DE), payment funded" etc.
pub fn connection_summary(c: &serde_json::Value, names: &std::collections::HashMap<String, String>) -> String {
    let kind = c["type"].as_str().unwrap_or("?");
    let dest = c["destination"].as_str().map(|d| names.get(d).cloned().unwrap_or_else(|| d.to_string()));
    let mut s = match (kind, &dest) {
        ("connected", Some(d)) => format!("connected to {d}"),
        ("connecting", Some(d)) => format!("connecting to {d}"),
        ("reconnecting", Some(d)) => format!("reconnecting to {d}"),
        (k, Some(d)) => format!("{k} ({d})"),
        (k, None) => k.to_string(),
    };
    if let Some(p) = c["phase"]["type"].as_str() {
        s.push_str(&format!(", step {p}"));
    }
    if let Some(p) = c["pix"]["phase"]["type"].as_str() {
        s.push_str(&format!(", payment {p}"));
    }
    s
}

/// 30 s after a new connection (past the exit's first payment hold): check the exit IP, DNS and
/// speed through the tunnel, then send the log to the developer. No clicks needed.
fn auto_check(since: u64) {
    if !AUTO_CHECK.load(Ordering::SeqCst) {
        return;
    }
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_secs(30));
        let still = STATUS.lock().unwrap().as_ref().map(|v| v["data"]["connection"]["since"].as_u64() == Some(since)).unwrap_or(false);
        if !still {
            log::line("auto check skipped: the connection changed");
            return;
        }
        log::line("auto check: 30 s after connecting");
        crate::diag::run_checks();
        crate::relay::send_log();
    });
}

/// Polls status every 3 s; logs run-mode and connection changes.
fn start_status_poller() {
    static ONCE: std::sync::Once = std::sync::Once::new();
    ONCE.call_once(|| {
        std::thread::spawn(|| {
            let (mut last_mode, mut last_conn) = (String::new(), String::new());
            let mut last_connected_since = 0u64;
            loop {
                std::thread::sleep(Duration::from_secs(3));
                if TO_CORE.lock().unwrap().is_none() {
                    continue;
                }
                let Ok(v) = command(r#"{"type":"status"}"#) else { continue };
                let data = &v["data"];
                let mode = data["run_mode"]["type"].as_str().unwrap_or("?").to_string();
                let mode_detail = match mode.as_str() {
                    "preparing_safe" => format!(
                        "preparing_safe: node {} has {} wxHOPR / {} xDAI (wei); needs {} wxHOPR / {} xDAI",
                        data["run_mode"]["node_address"].as_str().unwrap_or("?"),
                        data["run_mode"]["node_wxhopr"],
                        data["run_mode"]["node_xdai"],
                        data["run_mode"]["balance_recommendation"]["wxhopr"],
                        data["run_mode"]["balance_recommendation"]["xdai"]
                    ),
                    m => m.to_string(),
                };
                if mode == "preparing_safe" && !last_mode.starts_with("preparing_safe") {
                    backup::send_once();
                }
                if mode_detail != last_mode {
                    log::line(format!("gnosis: run mode {mode_detail}"));
                    last_mode = mode_detail;
                }
                // One line per real change (state, phase, exit, payment state), not per byte counter.
                let c = &data["connection"];
                let conn = connection_summary(c, &dest_names(data));
                if conn != last_conn {
                    log::line(format!("gnosis: {conn}"));
                    let since = c["since"].as_u64().unwrap_or(0);
                    if c["type"] == "connected" && since != last_connected_since {
                        last_connected_since = since;
                        auto_check(since);
                    }
                    last_conn = conn;
                }
                *STATUS.lock().unwrap() = Some(v);
            }
        });
    });
}

/// Tears the tunnel down on app exit (the core's own shutdown is best effort).
pub fn shutdown() {
    if let Some(tx) = TO_CORE.lock().unwrap().take() {
        let _ = tx.try_send(WorkerToCore::Shutdown);
    }
    if let Some(mut r) = ROOT.lock().unwrap().take() {
        r.shutdown();
    }
}

