//! `gvpndows.exe --e2e PLAN`: headless end-to-end test of the core on Windows.
//!
//! PLAN is a comma-separated list of steps `<exit>@<delay_s>`; `<exit>` matches an exit id, a
//! name substring (case-insensitive) or a country code. Each step:
//!   1. same exit as now: disconnect first ("reconnect"); another exit: connect directly ("switch");
//!   2. waits for "connected", then retries a small HTTP request until one works (time to first
//!      working request), reads the public IP and the DNS resolver;
//!   3. waits <delay_s>, then a heavy download (default 100 MB, at most 180 s) with per-second
//!      throughput, so a stall right after connecting shows up as zero-byte seconds;
//!   4. a 10 MB download for the steady speed, and 10 pings to the exit inside the tunnel.
//! Before the first step: a 10 MB download without the VPN as the baseline.
//! Results: log lines "E2E-STEP {json}", "E2E-DONE {json}" and %LOCALAPPDATA%\gvpndows\e2e-result.json.
//! Telemetry (telemetry.csv, nerdstats.jsonl) runs alongside. Env: GVPNDOWS_E2E_HEAVY_MB,
//! GVPNDOWS_E2E_HEAVY_S. Exit code 0 when every step connected and moved data.

use crate::log;
use serde_json::{json, Value};
use std::io::{ErrorKind, Read, Write};
use std::net::{IpAddr, Ipv4Addr, TcpStream, ToSocketAddrs};
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

const SOURCES: [&str; 2] = ["ipv4.download.thinkbroadband.com", "speedtest.tele2.net"];

fn env_u64(k: &str, d: u64) -> u64 {
    std::env::var(k).ok().and_then(|v| v.parse().ok()).unwrap_or(d)
}

fn status() -> Option<Value> {
    super::command(r#"{"type":"status"}"#).ok().map(|v| v["data"].clone())
}

fn wait(what: &str, timeout: Duration, ok: impl Fn(&Value) -> bool) -> Result<(Value, f64), String> {
    let t = Instant::now();
    let mut last = String::new();
    loop {
        if let Some(d) = status() {
            let names = super::dest_names(&d);
            let now = format!("{} | {}", d["run_mode"]["type"].as_str().unwrap_or("?"), super::connection_summary(&d["connection"], &names));
            if now != last {
                log::line(format!("e2e: [{:.0} s] {now}", t.elapsed().as_secs_f64()));
                last = now;
            }
            if ok(&d) {
                return Ok((d, t.elapsed().as_secs_f64()));
            }
        }
        if t.elapsed() > timeout {
            return Err(format!("no {what} within {} s (last: {last})", timeout.as_secs()));
        }
        std::thread::sleep(Duration::from_millis(500));
    }
}

fn resolve(d: &Value, q: &str) -> Option<(String, String)> {
    let q = q.to_lowercase();
    d["destinations"].as_array()?.iter().map(|x| &x["destination"]).find_map(|x| {
        let id = x["id"].as_str()?.to_string();
        let name = x["meta"]["name"].as_str().unwrap_or(&id).to_string();
        let country = x["geo"]["country"].as_str().unwrap_or("").to_lowercase();
        (id.to_lowercase() == q || name.to_lowercase().contains(&q) || country == q).then(|| (id, name))
    })
}

fn connect_tcp(host: &str, timeout: Duration) -> Result<TcpStream, String> {
    let addr = (host, 80).to_socket_addrs().map_err(|e| format!("DNS {host}: {e}"))?.next().ok_or(format!("{host}: no address"))?;
    TcpStream::connect_timeout(&addr, timeout).map_err(|e| format!("connect {host}: {e}"))
}

/// One small GET (5 s limits); the body as text.
fn small_get(host: &str, path: &str) -> Result<String, String> {
    let mut s = connect_tcp(host, Duration::from_secs(5))?;
    s.set_read_timeout(Some(Duration::from_secs(5))).ok();
    write!(s, "GET {path} HTTP/1.0\r\nHost: {host}\r\nUser-Agent: gvpndows-e2e\r\n\r\n").map_err(|e| e.to_string())?;
    let mut b = Vec::new();
    s.read_to_end(&mut b).map_err(|e| format!("read {host}: {e}"))?;
    let t = String::from_utf8_lossy(&b).to_string();
    if !t.starts_with("HTTP/1.1 200") && !t.starts_with("HTTP/1.0 200") {
        return Err(format!("{host}: {}", t.lines().next().unwrap_or("no answer")));
    }
    Ok(t.split_once("\r\n\r\n").map(|x| x.1.trim().to_string()).unwrap_or_default())
}

/// Streams a download, counting bytes per second; zero-byte seconds after the first byte are stalls.
fn download(mb: u64, max_s: u64) -> Value {
    let path = format!("/{mb}MB.zip");
    let mut errors = Vec::new();
    for host in SOURCES {
        let t0 = Instant::now();
        let mut s = match connect_tcp(host, Duration::from_secs(10)) {
            Ok(s) => s,
            Err(e) => {
                errors.push(e);
                continue;
            }
        };
        let connect_ms = t0.elapsed().as_millis() as u64;
        s.set_read_timeout(Some(Duration::from_millis(250))).ok();
        if let Err(e) = write!(s, "GET {path} HTTP/1.0\r\nHost: {host}\r\nUser-Agent: gvpndows-e2e\r\n\r\n") {
            errors.push(format!("send {host}: {e}"));
            continue;
        }
        let (mut total, mut first_ms, mut per_sec, mut head) = (0u64, None::<u64>, Vec::<u64>::new(), Vec::new());
        let mut buf = vec![0u8; 256 * 1024];
        let mut end = "max time";
        let target = mb * 1_000_000;
        while t0.elapsed() < Duration::from_secs(max_s) {
            match s.read(&mut buf) {
                Ok(0) => {
                    end = "complete";
                    break;
                }
                Ok(n) => {
                    first_ms.get_or_insert(t0.elapsed().as_millis() as u64);
                    if head.len() < 64 {
                        head.extend_from_slice(&buf[..n.min(64)]);
                    }
                    total += n as u64;
                    let sec = t0.elapsed().as_secs() as usize;
                    if per_sec.len() <= sec {
                        per_sec.resize(sec + 1, 0);
                    }
                    per_sec[sec] += n as u64;
                    if total >= target {
                        end = "complete";
                        break;
                    }
                }
                Err(e) if matches!(e.kind(), ErrorKind::WouldBlock | ErrorKind::TimedOut) => {}
                Err(e) => {
                    errors.push(format!("read {host}: {e} after {total} bytes"));
                    end = "error";
                    break;
                }
            }
        }
        let secs = t0.elapsed().as_secs_f64();
        per_sec.resize((secs as usize) + 1, 0);
        if first_ms.is_none() && end != "error" {
            errors.push(format!("{host}: no data in {max_s} s"));
        }
        // Stalls: runs of >= 3 zero-byte seconds after the first byte arrived.
        let start = first_ms.map(|m| (m / 1000) as usize).unwrap_or(per_sec.len());
        let (mut stalls, mut run) = (Vec::new(), 0usize);
        for (i, b) in per_sec.iter().enumerate().skip(start) {
            if *b == 0 {
                run += 1;
            } else {
                if run >= 3 {
                    stalls.push(json!({"at_s": i - run, "len_s": run}));
                }
                run = 0;
            }
        }
        if run >= 3 {
            stalls.push(json!({"at_s": per_sec.len() - run, "len_s": run, "open": true}));
        }
        let status = String::from_utf8_lossy(&head).lines().next().unwrap_or("").to_string();
        let mbit = total as f64 * 8.0 / secs.max(0.001) / 1e6;
        let kb: Vec<u64> = per_sec.iter().map(|b| b / 1000).collect();
        return json!({"host": host, "size_mb": mb, "status": status, "end": end, "bytes": total, "secs": (secs * 10.0).round() / 10.0,
            "mbit_s": (mbit * 100.0).round() / 100.0, "connect_ms": connect_ms, "first_byte_ms": first_ms,
            "stalls": stalls, "kB_per_s": kb, "errors": errors});
    }
    json!({"size_mb": mb, "bytes": 0, "errors": errors})
}

fn pings(n: u32) -> Value {
    let o = gnosis_vpn_lib::ping::Options { address: IpAddr::V4(Ipv4Addr::new(10, 128, 0, 1)), timeout: Duration::from_secs(2), ttl: 6, seq_count: 1 };
    let rtts: Vec<u64> = (0..n).filter_map(|_| super::root::icmp_ping(&o).ok().map(|d| d.as_millis() as u64)).collect();
    let avg = if rtts.is_empty() { None } else { Some(rtts.iter().sum::<u64>() / rtts.len() as u64) };
    json!({"sent": n, "ok": rtts.len(), "avg_ms": avg, "min_ms": rtts.iter().min(), "max_ms": rtts.iter().max()})
}

fn light_probe(limit: Duration) -> Value {
    let t = Instant::now();
    let mut attempts = 0;
    let mut last_err = String::new();
    while t.elapsed() < limit {
        attempts += 1;
        match small_get("api.ipify.org", "/") {
            Ok(ip) => {
                let dns = ("whoami.ds.akadns.net", 0)
                    .to_socket_addrs()
                    .map(|a| a.map(|x| x.ip().to_string()).collect::<Vec<_>>().join(" "))
                    .unwrap_or_else(|e| format!("lookup failed: {e}"));
                return json!({"first_ok_s": (t.elapsed().as_secs_f64() * 10.0).round() / 10.0, "attempts": attempts, "public_ip": ip, "dns_resolver": dns});
            }
            Err(e) => last_err = e,
        }
        std::thread::sleep(Duration::from_secs(1));
    }
    json!({"first_ok_s": null, "attempts": attempts, "error": last_err})
}

fn conn_is(d: &Value, kind: &str, id: Option<&str>) -> bool {
    let c = &d["connection"];
    c["type"] == kind && id.is_none_or(|id| c["destination"] == id)
}

fn step(plan: &str, heavy_mb: u64, heavy_s: u64) -> Result<Value, String> {
    let (q, delay) = plan.split_once('@').map(|(a, b)| (a, b.parse::<u64>().unwrap_or(0))).unwrap_or((plan, 0));
    let (d, _) = wait(&format!("an exit matching {q:?}"), Duration::from_secs(180), |d| resolve(d, q).is_some())?;
    let (id, name) = resolve(&d, q).ok_or_else(|| format!("no exit matches {q:?}"))?;
    let cur = d["connection"]["destination"].as_str().map(str::to_string);
    let kind = match cur.as_deref() {
        Some(c) if c == id => {
            log::line(format!("e2e: disconnecting from {name} first"));
            super::command(r#"{"type":"disconnect"}"#)?;
            wait("disconnect", Duration::from_secs(90), |d| d["connection"]["type"] == "disconnected")?;
            "reconnect"
        }
        Some(_) => "switch",
        None => "connect",
    };
    log::line(format!("e2e: step {plan}: {kind} to {name} ({id}), heavy download {delay} s after the first working request"));
    let t0 = Instant::now();
    let r = super::command(&json!({"type": "connect", "destination": id}).to_string())?;
    log::line(format!("e2e: connect -> {}", super::short(&r)));
    let (_, connect_s) = wait("connected", Duration::from_secs(300), |d| conn_is(d, "connected", Some(&id)))?;
    let light = light_probe(Duration::from_secs(180));
    log::line(format!("e2e: light probe {light}"));
    if delay > 0 {
        std::thread::sleep(Duration::from_secs(delay));
    }
    let heavy = download(heavy_mb, heavy_s);
    log::line(format!("e2e: heavy {}", json!({"bytes": heavy["bytes"], "mbit_s": heavy["mbit_s"], "first_byte_ms": heavy["first_byte_ms"], "stalls": heavy["stalls"], "errors": heavy["errors"]})));
    let steady = download(10, 90);
    let ping = pings(10);
    let after = status().map(|d| d["connection"].clone()).unwrap_or(Value::Null);
    Ok(json!({"step": plan, "kind": kind, "exit": name, "exit_id": id, "connect_s": (connect_s * 10.0).round() / 10.0,
        "step_s": t0.elapsed().as_secs(), "light": light, "heavy_after_s": delay, "heavy": heavy,
        "steady": steady, "exit_ping": ping, "connection_after": after}))
}

pub fn run(plan: &str) -> i32 {
    super::AUTO_CHECK.store(false, Ordering::SeqCst);
    let (heavy_mb, heavy_s) = (env_u64("GVPNDOWS_E2E_HEAVY_MB", 100), env_u64("GVPNDOWS_E2E_HEAVY_S", 180));
    log::line(format!("e2e: plan {plan}, heavy {heavy_mb} MB / {heavy_s} s"));
    let started = chrono_now();
    let baseline = download(10, 60);
    log::line(format!("e2e: baseline without VPN {} Mbit/s", baseline["mbit_s"]));
    if let Err(e) = super::start() {
        log::line(format!("e2e: core start failed: {e}"));
        return 2;
    }
    let mut steps = Vec::new();
    let mut ok = true;
    match wait("run mode running", Duration::from_secs(45 * 60), |d| d["run_mode"]["type"] == "running") {
        Err(e) => {
            log::line(format!("e2e: {e}"));
            ok = false;
        }
        Ok((_, s)) => {
            // The exit list fills a little after "running".
            let d = wait("exits", Duration::from_secs(180), |d| d["destinations"].as_array().is_some_and(|a| !a.is_empty()))
                .map(|x| x.0)
                .unwrap_or(Value::Null);
            let names: Vec<String> = super::dest_names(&d).into_values().collect();
            log::line(format!("e2e: core ready after {s:.0} s, {} exits: {}", names.len(), names.join(", ")));
            for p in plan.split(',').map(str::trim).filter(|p| !p.is_empty()) {
                let r = step(p, heavy_mb, heavy_s).unwrap_or_else(|e| json!({"step": p, "error": e}));
                ok &= r["error"].is_null() && r["heavy"]["bytes"].as_u64().unwrap_or(0) > 0;
                log::line(format!("E2E-STEP {r}"));
                steps.push(r);
            }
            let _ = super::command(r#"{"type":"disconnect"}"#);
            let _ = wait("disconnect", Duration::from_secs(90), |d| d["connection"]["type"] == "disconnected");
        }
    }
    let result = json!({"started": started, "ok": ok, "plan": plan, "baseline": baseline, "steps": steps,
        "telemetry": super::telemetry::csv_path(), "nerdstats": super::telemetry::nerd_path()});
    let _ = std::fs::write(log::dir().join("e2e-result.json"), serde_json::to_string_pretty(&result).unwrap_or_default());
    log::line(format!("E2E-DONE {}", json!({"ok": ok, "steps": steps.len()})));
    super::shutdown();
    std::thread::sleep(Duration::from_secs(2));
    if ok { 0 } else { 1 }
}

fn chrono_now() -> String {
    log::timestamp()
}
