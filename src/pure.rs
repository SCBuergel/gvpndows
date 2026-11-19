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
    /// {"type":"x_send_log"}: post the service's recent log to the developer relay
    SendLog,
    /// {"type":"x_app_log","lines":[...]}: the app's own log lines, added to the service log (debug builds)
    AppLog,
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
        Some("x_send_log") => PipeRoute::SendLog,
        Some("x_app_log") => PipeRoute::AppLog,
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

/// The end of a log, at most `max` bytes, starting at a line start (the newest lines matter most).
pub fn log_tail(lines: &[String], max: usize) -> String {
    let mut total = 0;
    let mut start = lines.len();
    while start > 0 && total + lines[start - 1].len() + 1 <= max {
        start -= 1;
        total += lines[start].len() + 1;
    }
    lines[start..].join("\n")
}

// ---- DEBUG FEATURE ONLY (developer relay, src/relay.rs): log stream budget and scheduling.
// Release builds do not use these (src/relay_off.rs sends nothing).

/// Log messages one client may ever send to the developer relay.
pub const LOG_BUDGET: u32 = 240;
/// Part of the budget kept for error lines (periodic sends stop earlier).
pub const ERROR_RESERVE: u32 = 40;

/// Seconds between periodic sends: 10 s, doubling every 40 messages, at most 30 minutes.
/// 240 messages then cover several hours of a session rather than its first 40 minutes.
pub fn stream_interval(sent: u32) -> u64 {
    (10u64 << (sent / 40).min(8)).min(1800)
}

/// Least spacing for urgent (error or user-requested) sends: 5 s, or half the periodic interval,
/// so an error storm slows itself down too.
pub fn error_spacing(sent: u32) -> u64 {
    (stream_interval(sent) / 2).max(5)
}

/// Whether one more message may go out: periodic ones stop before the error reserve.
pub fn may_send(sent: u32, urgent: bool) -> bool {
    sent < if urgent { LOG_BUDGET } else { LOG_BUDGET - ERROR_RESERVE }
}

/// Lines that are sent at once: tracing ERRORs, panics, and this app's own failure messages.
pub fn is_error_line(l: &str) -> bool {
    if l.contains(" ERROR ") || l.contains("panicked") {
        return true;
    }
    let own = l.contains(" gnosis: ") || l.contains(" service: ");
    let low = l.to_ascii_lowercase();
    own && (low.contains("fail") || low.contains("error") || low.contains("not reachable") || low.contains("stopped"))
        || l.contains("payment payment_failed")
}

/// Lines kept first when a message is full: errors, warnings and this app's own lines.
pub fn is_important(l: &str) -> bool {
    is_error_line(l) || l.contains(" WARN ") || l.contains(" gnosis: ") || l.contains(" service: ")
}

/// The newest `important` important lines and the newest `other` other lines, in log order.
pub fn select_lines(lines: &[String], important: usize, other: usize) -> Vec<String> {
    let (mut ni, mut no) = (0, 0);
    let mut keep = vec![false; lines.len()];
    for (i, l) in lines.iter().enumerate().rev() {
        let (n, cap) = if is_important(l) { (&mut ni, important) } else { (&mut no, other) };
        if *n < cap {
            *n += 1;
            keep[i] = true;
        }
    }
    lines.iter().zip(keep).filter(|(_, k)| *k).map(|(l, _)| l.clone()).collect()
}

/// DEBUG FEATURE ONLY: the app's log lines as the service records them: at most 50 per request,
/// each cut to 300 bytes (on a character boundary), control characters replaced.
pub fn app_log_lines(v: &serde_json::Value) -> Vec<String> {
    v["lines"].as_array().map(|a| a.iter().filter_map(|l| l.as_str()).take(50).map(|l| {
        let mut end = l.len().min(300);
        while end > 0 && !l.is_char_boundary(end) { end -= 1; }
        format!("app: {}", l[..end].chars().map(|c| if c.is_control() { ' ' } else { c }).collect::<String>())
    }).collect()).unwrap_or_default()
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
        assert_eq!(route_request(r#"{"type":"x_send_log"}"#), PipeRoute::SendLog);
        assert_eq!(route_request(r#"{"type":"x_app_log","lines":["a"]}"#), PipeRoute::AppLog);
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
        // IHL 2 would read the source port from bytes 8-9; byte 9 must stay 17 (UDP), so the port
        // there is 0x0B11: with the IHL guard removed this packet would match
        let mut bad_ihl = udp(9091, [1, 2, 3, 4], 5);
        bad_ihl[0] = 0x42;
        bad_ihl[8] = 0x0B;
        assert_eq!(bad_ihl[9], 17);
        assert_eq!(hopr_leak_dst(&bad_ihl, &[0x0B11]), None, "IHL below 5");
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
    fn log_tail_keeps_the_newest_whole_lines() {
        let lines: Vec<String> = (0..100).map(|i| format!("line {i:03}")).collect();
        let t = log_tail(&lines, 50);
        assert!(t.len() <= 50);
        assert!(t.ends_with("line 099") && t.starts_with("line "));
        assert_eq!(log_tail(&lines, 1_000_000).lines().count(), 100);
        assert_eq!(log_tail(&[], 10), "");
    }

    #[test]
    fn log_stream_slows_down_and_never_exceeds_the_budget() {
        assert_eq!(stream_interval(0), 10);
        assert_eq!(stream_interval(39), 10);
        assert_eq!(stream_interval(40), 20);
        assert_eq!(stream_interval(199), 160);
        assert_eq!(stream_interval(10_000), 1800);
        assert_eq!(error_spacing(0), 5);
        assert_eq!(error_spacing(120), 40);
        assert!(may_send(199, false) && !may_send(200, false), "periodic sends leave the error reserve");
        assert!(may_send(239, true) && !may_send(240, true), "nothing after 240");
        // simulate a day of a busy client: one send whenever allowed, errors every second
        let (mut sent, mut last, mut t) = (0u32, 0u64, 0u64);
        while t < 86_400 {
            let since = t - last;
            if (since >= stream_interval(sent) && may_send(sent, false)) || (since >= error_spacing(sent) && may_send(sent, true)) {
                sent += 1;
                last = t;
                assert!(since >= 5, "never faster than one message per 5 s");
            }
            t += 1;
        }
        assert_eq!(sent, LOG_BUDGET);
    }

    #[test]
    fn error_lines_and_line_selection() {
        assert!(is_error_line("2026-11-18 21:35:55.352Z ERROR hopr_strategy::pix::strategy: funding failed"));
        assert!(is_error_line("2026-11-18 20:43:42.804Z service: core start failed: resolve blokli"));
        assert!(is_error_line("2026-11-18 21:35:57.845Z gnosis: connected to GVPN-LT-1 (LT), payment payment_failed"));
        assert!(!is_error_line("2026-11-18 21:13:18.323Z gnosis: run mode running"));
        assert!(!is_error_line("2026-11-18 21:13:18.323Z  INFO hopr_lib: retry failed peers later"), "library INFO lines are not urgent");
        assert!(!is_error_line("2026-11-18 21:13:18.323Z debug relay: post failed (rate limited (429))"), "the relay's own failures do not trigger sends");
        let lines: Vec<String> = ["a INFO x1", "b WARN w1", "c INFO x2", "d ERROR e1", "e INFO x3"].iter().map(|s| s.to_string()).collect();
        assert_eq!(select_lines(&lines, 9, 9), lines);
        assert_eq!(select_lines(&lines, 9, 1), ["b WARN w1", "d ERROR e1", "e INFO x3"]);
        assert_eq!(select_lines(&lines, 1, 0), ["d ERROR e1"]);
    }

    #[test]
    fn app_log_lines_are_bounded() {
        let many: Vec<String> = (0..80).map(|i| format!("line {i}")).collect();
        let v = serde_json::json!({ "type": "x_app_log", "lines": many });
        let got = app_log_lines(&v);
        assert_eq!(got.len(), 50);
        assert_eq!(got[0], "app: line 0");
        let long = serde_json::json!({ "lines": ["é".repeat(400) + "\n\u{1b}x"] });
        let l = &app_log_lines(&long)[0];
        assert!(l.len() <= 305 && !l.contains('\n'));
        assert!(app_log_lines(&serde_json::json!({})).is_empty());
    }

    #[test]
    fn plan_steps() {
        assert_eq!(parse_step("ger@60"), ("ger", 60));
        assert_eq!(parse_step("lt"), ("lt", 0));
        assert_eq!(parse_step("lt@x"), ("lt", 0));
    }
}
