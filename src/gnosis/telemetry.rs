//! Debug telemetry while a connection exists (connecting/connected/reconnecting), one row per
//! second, so a stall can be lined up with what the core and the tunnel were doing:
//!   %LOCALAPPDATA%\gvpndows\telemetry.csv  - connection state, the core's stall and PIX payment
//!       counters, packets each way through Wintun, drops, the reader backlog, and an in-tunnel
//!       ICMP ping to the exit (10.128.0.1) with a 900 ms timeout
//!   %LOCALAPPDATA%\gvpndows\nerdstats.jsonl - the core's NerdStats every 30 s and on each
//!       connection state change
//! GVPNDOWS_TELEMETRY=0 turns it off. Each file is restarted when it passes 50 MB.

use super::root::TUN;
use std::io::Write;
use std::net::{IpAddr, Ipv4Addr};
use std::sync::atomic::Ordering::Relaxed;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const MAX_FILE: u64 = 50 * 1024 * 1024;
const HEADER: &str = "utc,unix_ms,conn,phase,dest,conn_age_s,core_ping_ms,stall_failed_pings,stall_age_s,awaiting_payment_s,\
pix_phase,chunks_committed,chunks_paid,chunks_failed,bytes_bought,bytes_spent,bytes_left,bytes_received,\
up_pkts,up_kB,down_pkts,down_kB,up_pps,down_kbps,dropped,backlog,exit_ping_ms,hopr_leaked";

pub fn csv_path() -> std::path::PathBuf {
    crate::log::dir().join("telemetry.csv")
}

pub fn nerd_path() -> std::path::PathBuf {
    crate::log::dir().join("nerdstats.jsonl")
}

fn open(path: &std::path::Path, header: Option<&str>) -> Option<std::fs::File> {
    let fresh = std::fs::metadata(path).map(|m| m.len() > MAX_FILE).unwrap_or(true);
    let mut f = std::fs::OpenOptions::new().create(true).append(!fresh).write(true).truncate(fresh).open(path).ok()?;
    if let (true, Some(h)) = (fresh, header) {
        let _ = writeln!(f, "{h}");
    }
    Some(f)
}

fn now_ms() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_millis() as u64).unwrap_or(0)
}

fn age_s(ms: Option<u64>, now: u64) -> String {
    ms.map(|m| format!("{:.1}", now.saturating_sub(m) as f64 / 1000.0)).unwrap_or_default()
}

fn opt(v: &serde_json::Value) -> String {
    match v {
        serde_json::Value::Null => String::new(),
        serde_json::Value::String(s) => s.clone(),
        v => v.to_string(),
    }
}

fn exit_ping() -> String {
    let o = gnosis_vpn_lib::ping::Options {
        address: IpAddr::V4(Ipv4Addr::new(10, 128, 0, 1)),
        timeout: Duration::from_millis(900),
        ttl: 6,
        seq_count: 1,
    };
    match super::root::icmp_ping(&o) {
        Ok(d) => d.as_millis().to_string(),
        Err(_) => "timeout".into(),
    }
}

fn nerd_dump(reason: &str) {
    let Ok(v) = super::command(r#"{"type":"nerd_stats"}"#) else { return };
    if let Some(mut f) = open(&nerd_path(), None) {
        let _ = writeln!(f, "{}", serde_json::json!({"unix_ms": now_ms(), "reason": reason, "stats": v["data"]}));
    }
}

pub fn start() {
    if std::env::var("GVPNDOWS_TELEMETRY").is_ok_and(|v| v == "0") {
        return;
    }
    static ONCE: std::sync::Once = std::sync::Once::new();
    ONCE.call_once(|| {
        std::thread::Builder::new()
            .name("telemetry".into())
            .spawn(run)
            .ok();
    });
}

fn run() {
    let mut file: Option<std::fs::File> = None;
    let mut last = (0u64, 0u64, Instant::now());
    let mut last_kind = String::new();
    let mut last_nerd = Instant::now();
    let mut rows = 0u64;
    loop {
        let tick = Instant::now();
        if !super::running() {
            std::thread::sleep(Duration::from_secs(2));
            continue;
        }
        let Ok(v) = super::command(r#"{"type":"status"}"#) else {
            std::thread::sleep(Duration::from_secs(1));
            continue;
        };
        let c = &v["data"]["connection"];
        let kind = c["type"].as_str().unwrap_or("?").to_string();
        let active = matches!(kind.as_str(), "connecting" | "connected" | "reconnecting");
        if kind != last_kind {
            nerd_dump(&format!("connection {last_kind} -> {kind}"));
            last_kind = kind.clone();
            last_nerd = Instant::now();
        }
        if !active {
            file = None;
            std::thread::sleep(Duration::from_secs(1));
            continue;
        }
        if last_nerd.elapsed() >= Duration::from_secs(30) {
            nerd_dump("periodic");
            last_nerd = Instant::now();
        }
        let f = match &mut file {
            Some(f) => f,
            None => match open(&csv_path(), Some(HEADER)) {
                Some(f) => file.insert(f),
                None => {
                    std::thread::sleep(Duration::from_secs(5));
                    continue;
                }
            },
        };
        let now = now_ms();
        let pix = &c["pix"];
        let stall = &c["stall"];
        let (up_p, up_b, dn_p, dn_b) = (TUN.up_pkts.load(Relaxed), TUN.up_bytes.load(Relaxed), TUN.down_pkts.load(Relaxed), TUN.down_bytes.load(Relaxed));
        let backlog = up_p.saturating_sub(TUN.up_taken.load(Relaxed));
        let dt = last.2.elapsed().as_secs_f64().max(0.1);
        let up_pps = (up_p.saturating_sub(last.0) as f64 / dt).round() as u64;
        let down_kbps = (dn_b.saturating_sub(last.1) as f64 * 8.0 / 1000.0 / dt).round() as u64;
        last = (up_p, dn_b, Instant::now());
        let ping = if kind == "connected" { exit_ping() } else { String::new() };
        let utc = crate::log::timestamp();
        let row = [
            utc,
            now.to_string(),
            kind.clone(),
            opt(&c["phase"]["type"]),
            opt(&c["destination"]),
            age_s(c["since"].as_u64(), now),
            opt(&c["tunnel_ping_rtt"]),
            opt(&stall["failed_pings"]),
            age_s(stall["since"].as_u64(), now),
            age_s(stall["awaiting_payment_since"].as_u64(), now),
            opt(&pix["phase"]["type"]),
            opt(&pix["chunks_committed"]),
            opt(&pix["chunks_paid"]),
            opt(&pix["chunks_failed"]),
            opt(&pix["bytes_bought"]),
            opt(&pix["bytes_spent"]),
            opt(&pix["bytes_left"]),
            opt(&pix["bytes_received"]),
            up_p.to_string(),
            (up_b / 1000).to_string(),
            dn_p.to_string(),
            (dn_b / 1000).to_string(),
            up_pps.to_string(),
            down_kbps.to_string(),
            TUN.dropped.load(Relaxed).to_string(),
            backlog.to_string(),
            ping,
            TUN.leaked.load(Relaxed).to_string(),
        ];
        let _ = writeln!(f, "{}", row.join(","));
        rows += 1;
        // a long connection: restart the file once it passes the limit (checked every 10 min)
        if rows % 600 == 0 && f.metadata().map(|m| m.len() > MAX_FILE).unwrap_or(false) {
            file = None;
        }
        if let Some(rest) = Duration::from_secs(1).checked_sub(tick.elapsed()) {
            std::thread::sleep(rest);
        }
    }
}
