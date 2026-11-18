//! Platform-independent logic split out of the Windows code so it can be unit-tested anywhere:
//! pipe request routing, the HOPR leak check on tunnel packets, log chunking, e2e plan steps.

use std::net::Ipv4Addr;

/// Longest request line the service accepts from the pipe (requests are small JSON objects).
pub const MAX_REQUEST: usize = 64 * 1024;

/// Core commands the app may send over the pipe. Funding commands (`funding_tool` takes a secret,
/// `fund_pix_float` moves funds) and the probing controls are not reachable from the pipe.
pub const ALLOWED_COMMANDS: [&str; 6] = ["status", "connect", "disconnect", "balance", "destinations", "nerd_stats"];

#[derive(Debug, PartialEq)]
pub enum PipeRoute {
    /// {"type":"x_state"}: the service's cached state, no core call
    State,
    /// {"type":"x_start"}: start the core if it is not running
    Start,
    /// an allowed core command, passed on unchanged
    Command,
    /// refused, with the reason for the reply
    Rejected(String),
}

pub fn route_request(line: &str) -> PipeRoute {
    if line.len() > MAX_REQUEST {
        return PipeRoute::Rejected(format!("request longer than {MAX_REQUEST} bytes"));
    }
    let v: serde_json::Value = match serde_json::from_str(line) {
        Ok(v) => v,
        Err(e) => return PipeRoute::Rejected(format!("bad request: {e}")),
    };
    match v["type"].as_str() {
        Some("x_state") => PipeRoute::State,
        Some("x_start") => PipeRoute::Start,
        Some(t) if ALLOWED_COMMANDS.contains(&t) => PipeRoute::Command,
        Some(t) => PipeRoute::Rejected(format!("command {t:?} is not available over the pipe")),
        None => PipeRoute::Rejected("request has no \"type\"".into()),
    }
}

/// The destination of an IPv4 UDP packet whose source port is one of `own_ports` (this process's
/// sockets, i.e. the HOPR node): such a packet must not enter the tunnel it carries.
pub fn hopr_leak_dst(p: &[u8], own_ports: &[u16]) -> Option<Ipv4Addr> {
    if p.len() < 20 || p[0] >> 4 != 4 || p[9] != 17 {
        return None;
    }
    let ihl = ((p[0] & 15) as usize) * 4;
    if ihl < 20 || p.len() < ihl + 4 {
        return None;
    }
    let sport = u16::from_be_bytes([p[ihl], p[ihl + 1]]);
    own_ports.contains(&sport).then(|| Ipv4Addr::new(p[16], p[17], p[18], p[19]))
}

/// Owners the service trusts for its state folders: SYSTEM and Administrators. A folder owned by
/// anyone else may hold files planted by that user (a node key whose secret they know).
pub fn owner_trusted(sid: &str) -> bool {
    matches!(sid, "S-1-5-18" | "S-1-5-32-544")
}

/// Splits the log into relay messages of at most `chunk` bytes, at line ends; a longer line is
/// cut to `chunk` bytes (on a character boundary).
pub fn chunk_log(text: &str, chunk: usize) -> Vec<String> {
    let mut parts = Vec::new();
    let mut cur = String::new();
    for line in text.lines() {
        let mut l = line;
        if l.len() + 1 > chunk {
            let mut end = chunk.saturating_sub(1);
            while end > 0 && !l.is_char_boundary(end) {
                end -= 1;
            }
            l = &l[..end];
        }
        if cur.len() + l.len() + 1 > chunk && !cur.is_empty() {
            parts.push(std::mem::take(&mut cur));
        }
        cur.push_str(l);
        cur.push('\n');
    }
    if !cur.is_empty() {
        parts.push(cur);
    }
    parts
}

/// One e2e plan step "<exit>@<delay_s>" -> (exit query, delay); no or a bad delay is 0.
pub fn parse_step(step: &str) -> (&str, u64) {
    match step.split_once('@') {
        Some((q, d)) => (q, d.trim().parse().unwrap_or(0)),
        None => (step, 0),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn udp(sport: u16, dst: [u8; 4], ihl_words: u8) -> Vec<u8> {
        let ihl = ihl_words as usize * 4;
        let mut p = vec![0u8; ihl + 8];
        p[0] = 0x40 | ihl_words;
        p[9] = 17;
        p[16..20].copy_from_slice(&dst);
        p[ihl..ihl + 2].copy_from_slice(&sport.to_be_bytes());
        p
    }

    #[test]
    fn pipe_routes_known_requests() {
        assert_eq!(route_request(r#"{"type":"x_state"}"#), PipeRoute::State);
        assert_eq!(route_request(r#"{"type":"x_start"}"#), PipeRoute::Start);
        assert_eq!(route_request(r#"{"type":"connect","destination":"a"}"#), PipeRoute::Command);
        assert_eq!(route_request(r#"{"type":"status"}"#), PipeRoute::Command);
    }

    #[test]
    fn pipe_rejects_funding_unknown_and_malformed() {
        assert!(matches!(route_request(r#"{"type":"funding_tool","secret":"x"}"#), PipeRoute::Rejected(_)));
        assert!(matches!(route_request(r#"{"type":"fund_pix_float","amount":"1"}"#), PipeRoute::Rejected(_)));
        assert!(matches!(route_request(r#"{"type":"probe","destination":"a"}"#), PipeRoute::Rejected(_)));
        assert!(matches!(route_request("not json"), PipeRoute::Rejected(_)));
        assert!(matches!(route_request(r#"{"kind":"status"}"#), PipeRoute::Rejected(_)));
        let long = format!(r#"{{"type":"status","pad":"{}"}}"#, "x".repeat(MAX_REQUEST));
        assert!(matches!(route_request(&long), PipeRoute::Rejected(r) if r.contains("longer")));
    }

    #[test]
    fn leak_check_matches_own_udp_source_ports_only() {
        let own = [9091u16, 50000];
        assert_eq!(hopr_leak_dst(&udp(9091, [95, 217, 25, 176], 5), &own), Some(Ipv4Addr::new(95, 217, 25, 176)));
        assert_eq!(hopr_leak_dst(&udp(50000, [1, 2, 3, 4], 6), &own), Some(Ipv4Addr::new(1, 2, 3, 4)), "IP options");
        assert_eq!(hopr_leak_dst(&udp(443, [1, 2, 3, 4], 5), &own), None, "another app's UDP");
        let mut tcp = udp(9091, [1, 2, 3, 4], 5);
        tcp[9] = 6;
        assert_eq!(hopr_leak_dst(&tcp, &own), None, "TCP");
        let mut v6 = udp(9091, [1, 2, 3, 4], 5);
        v6[0] = 0x65;
        assert_eq!(hopr_leak_dst(&v6, &own), None, "IPv6");
        assert_eq!(hopr_leak_dst(&udp(9091, [1, 2, 3, 4], 5)[..21], &own), None, "truncated");
        let mut bad_ihl = udp(9091, [1, 2, 3, 4], 5);
        bad_ihl[0] = 0x42;
        bad_ihl[8..10].copy_from_slice(&9091u16.to_be_bytes()); // where IHL 2 would put the source port
        assert_eq!(hopr_leak_dst(&bad_ihl, &own), None, "IHL below 5");
    }

    #[test]
    fn log_chunks_stay_under_the_limit_and_keep_every_line() {
        let text = (0..500).map(|i| format!("line {i} {}", "é".repeat(i % 40))).collect::<Vec<_>>().join("\n");
        let parts = chunk_log(&text, 3500);
        assert!(parts.iter().all(|p| p.len() <= 3500));
        assert_eq!(parts.concat().lines().count(), 500);
        let long = "ü".repeat(5000);
        let parts = chunk_log(&long, 3500);
        assert_eq!(parts.len(), 1);
        assert!(parts[0].len() <= 3500);
        assert!(chunk_log("", 3500).is_empty());
    }

    #[test]
    fn only_system_and_administrators_own_state_folders() {
        assert!(owner_trusted("S-1-5-18"));
        assert!(owner_trusted("S-1-5-32-544"));
        assert!(!owner_trusted("S-1-5-21-1-2-3-1001"), "a local user");
        assert!(!owner_trusted("S-1-5-32-545"), "Users");
        assert!(!owner_trusted(""));
    }

    #[test]
    fn plan_steps() {
        assert_eq!(parse_step("ger@60"), ("ger", 60));
        assert_eq!(parse_step("lt"), ("lt", 0));
        assert_eq!(parse_step("lt@x"), ("lt", 0));
    }
}
