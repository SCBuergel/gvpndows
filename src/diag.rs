//! Connection checks shown in the log: public IP, which DNS resolver answers, download speed.
//! Plain HTTP over std sockets, so no TLS stack is needed for these tests.

use std::io::{Read, Write};
use std::net::{TcpStream, ToSocketAddrs};
use std::time::{Duration, Instant};

fn http_get(host: &str, path: &str, limit: usize) -> Result<(Vec<u8>, Duration), String> {
    let addr = (host, 80)
        .to_socket_addrs()
        .map_err(|e| format!("DNS lookup of {host} failed: {e}"))?
        .next()
        .ok_or(format!("{host}: no address"))?;
    let mut s = TcpStream::connect_timeout(&addr, Duration::from_secs(10)).map_err(|e| format!("connect {host}: {e}"))?;
    s.set_read_timeout(Some(Duration::from_secs(20))).ok();
    write!(s, "GET {path} HTTP/1.0\r\nHost: {host}\r\nUser-Agent: gvpndows-test\r\nConnection: close\r\n\r\n")
        .map_err(|e| format!("send {host}: {e}"))?;
    let t = Instant::now();
    let mut buf = Vec::new();
    let mut chunk = vec![0u8; 64 * 1024];
    loop {
        match s.read(&mut chunk) {
            Ok(0) => break,
            Ok(n) => {
                buf.extend_from_slice(&chunk[..n]);
                if buf.len() >= limit {
                    break;
                }
            }
            Err(e) => return Err(format!("read {host}: {e} after {} bytes", buf.len())),
        }
    }
    let elapsed = t.elapsed();
    let body_at = buf.windows(4).position(|w| w == b"\r\n\r\n").map(|p| p + 4).unwrap_or(0);
    let status = String::from_utf8_lossy(&buf[..buf.len().min(40)]).lines().next().unwrap_or("").to_string();
    if !status.contains(" 200") {
        return Err(format!("{host}{path}: {status}"));
    }
    Ok((buf.split_off(body_at), elapsed))
}

/// Runs all checks and logs each result (blocking; call from a thread).
pub fn run_checks() {
    crate::log::line("check: starting (public IP, DNS resolver, download)");
    match http_get("api.ipify.org", "/", 1000) {
        Ok((b, _)) => crate::log::line(format!("check: public IPv4 = {}", String::from_utf8_lossy(&b).trim())),
        Err(e) => crate::log::line(format!("check: public IP failed: {e}")),
    }
    // whoami.ds.akadns.net answers with the address of the resolver that asked Akamai:
    // through the tunnel this should belong to the configured DNS (e.g. Quad9), not the ISP.
    match ("whoami.ds.akadns.net", 0).to_socket_addrs() {
        Ok(a) => crate::log::line(format!(
            "check: DNS resolver seen by Akamai = {}",
            a.map(|x| x.ip().to_string()).collect::<Vec<_>>().join(", ")
        )),
        Err(e) => crate::log::line(format!("check: DNS lookup failed: {e}")),
    }
    let sources = [
        ("speedtest.tele2.net", "/10MB.zip"),
        ("ipv4.download.thinkbroadband.com", "/10MB.zip"),
    ];
    for (host, path) in sources {
        match http_get(host, path, 10_000_000) {
            Ok((b, d)) => {
                let mbit = b.len() as f64 * 8.0 / d.as_secs_f64().max(0.001) / 1e6;
                crate::log::line(format!(
                    "check: downloaded {:.1} MB from {host} in {:.1} s = {mbit:.1} Mbit/s",
                    b.len() as f64 / 1e6,
                    d.as_secs_f64()
                ));
                break;
            }
            Err(e) => crate::log::line(format!("check: download from {host} failed: {e}")),
        }
    }
    crate::log::line("check: done");
}
