//! ==================================================================================================
//! DEBUG FEATURE ONLY: REMOVED FROM PRODUCTION RELEASES.
//!
//! The developer relay sends this client's log to an ntfy.sh topic that the developer reads (and,
//! with the `debug-identity-backup` feature, gnosis/backup.rs sends the encrypted node identity
//! once). It exists to debug builds on machines the developer cannot reach.
//!
//! It is compiled in only with the Cargo feature `debug-relay`: the "DEBUG-logging" release (built
//! by GitHub Actions, logs only) and private test builds (scripts/do-build, with the identity
//! backup). Without the feature, `src/relay_off.rs` is compiled instead: the same functions, doing
//! nothing, so a release build contains no code that sends anything. A build with
//! GVPNDOWS_RELEASE set refuses to compile with this feature (see the assertion below).
//! ==================================================================================================
//!
//! What is sent: the newest log lines, compressed (zlib) and sealed to the developer's X25519 key
//! (gnosis/backup.rs `seal`), so only the developer can read them. When: a stream started by the
//! service. Every 10 s at first; the interval doubles every 40 messages; error lines are sent at once
//! (from a reserve, spaced at least 5 s). At most 240 messages per client, ever (counted in
//! relay-budget.json next to the log), which also stays under ntfy.sh's limits for anonymous use.

use std::collections::VecDeque;
use std::io::Write;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

use crate::pure::{self, LOG_BUDGET};

/// True in test builds with the developer relay (shown by the app as "debug build").
pub const ACTIVE: bool = true;

// A release build must never contain the relay.
const _: () = assert!(option_env!("GVPNDOWS_RELEASE").is_none(), "the debug-relay feature must not be enabled in a release build");

const TOPIC: Option<&str> = option_env!("GVPNDOWS_NTFY_TOPIC");
/// zlib output per message: sealed and base64-encoded it stays under ntfy's 4 KB body limit.
const MAX_COMPRESSED: usize = 2850;
/// Lines kept waiting while sending is slow or the budget is spent (older ones are dropped).
const MAX_PENDING: usize = 20_000;

pub fn available() -> bool {
    TOPIC.is_some_and(|t| !t.is_empty())
}

pub fn post_text(title: &str, body: &str) -> Result<(), String> {
    post(title, body)
}

/// The relay host's last resolved addresses. While the tunnel is up DNS goes only through it (the
/// DNS guard), so a stalled tunnel would also stop the relay's lookups; then these are used.
static LAST_ADDRS: Mutex<Vec<std::net::SocketAddr>> = Mutex::new(Vec::new());

fn resolve(netloc: &str) -> std::io::Result<Vec<std::net::SocketAddr>> {
    use std::net::ToSocketAddrs;
    let cached = || LAST_ADDRS.lock().map(|a| a.clone()).unwrap_or_default();
    match netloc.to_socket_addrs().map(|a| a.collect::<Vec<_>>()) {
        Ok(a) if !a.is_empty() => {
            if let Ok(mut l) = LAST_ADDRS.lock() {
                *l = a.clone();
            }
            Ok(a)
        }
        r => match cached() {
            c if !c.is_empty() => Ok(c),
            _ => r,
        },
    }
}

fn post(title: &str, body: &str) -> Result<(), String> {
    let topic = TOPIC.ok_or("no relay topic in this build")?;
    ureq::AgentBuilder::new()
        .resolver(resolve)
        .build()
        .post(&format!("https://ntfy.sh/{topic}"))
        .timeout(Duration::from_secs(20))
        .set("Title", title)
        .send_string(body)
        .map(|_| ())
        .map_err(|e| match e {
            ureq::Error::Status(429, _) => "rate limited (429)".to_string(),
            e => e.to_string(),
        })
}

/// Blocking post used by --selftest to prove the relay works from the Windows binary.
pub fn selftest() -> Result<(), String> {
    post("gvpndows selftest", "relay selftest from the Windows binary")
}

// ------------------------------------------------------------------------------- log stream

struct Budget {
    client: String,
    sent: u32,
}

fn budget_path() -> std::path::PathBuf {
    crate::log::dir().join("relay-budget.json")
}

fn load_budget() -> Budget {
    let v: serde_json::Value = std::fs::read_to_string(budget_path()).ok().and_then(|s| serde_json::from_str(&s).ok()).unwrap_or_default();
    let client = v["client"].as_str().map(str::to_string).unwrap_or_else(|| {
        let mut b = [0u8; 4];
        let _ = getrandom::getrandom(&mut b);
        b.iter().map(|x| format!("{x:02x}")).collect()
    });
    Budget { client, sent: v["sent"].as_u64().unwrap_or(0).min(LOG_BUDGET as u64) as u32 }
}

fn save_budget(b: &Budget) {
    let _ = std::fs::write(budget_path(), serde_json::json!({ "client": b.client, "sent": b.sent }).to_string());
}

static STARTED: OnceLock<()> = OnceLock::new();
static FLUSH: AtomicBool = AtomicBool::new(false);
static FINAL: AtomicBool = AtomicBool::new(false);
static LAST_SEND: Mutex<Option<Instant>> = Mutex::new(None);

/// Starts the log stream (once per process). Nothing is sent without a topic and a developer key.
pub fn start_stream() {
    if !available() || !crate::gnosis::backup::available() {
        return;
    }
    if STARTED.set(()).is_err() {
        return;
    }
    crate::log::line("DEBUG BUILD: this client streams its log, encrypted, to the developer (at most 240 messages)");
    std::thread::Builder::new().name("debug-relay".into()).spawn(stream).ok();
}

/// The app's "Send log to developer": the next message goes out now (still within the budget).
pub fn flush_now() -> Result<(), String> {
    if !available() || !crate::gnosis::backup::available() {
        return Err("this build has no developer relay".into());
    }
    start_stream();
    if LAST_SEND.lock().unwrap().is_some_and(|t| t.elapsed() < Duration::from_secs(5)) {
        return Err("the log was sent less than 5 seconds ago".into());
    }
    FLUSH.store(true, Ordering::SeqCst);
    Ok(())
}

/// On service stop: send the remaining lines now (within the budget, ignoring the spacing) and wait
/// up to `max` for it, so the last lines before a stop or update are not lost.
pub fn flush_final(max: Duration) {
    if STARTED.get().is_none() {
        return;
    }
    let before = *LAST_SEND.lock().unwrap();
    FINAL.store(true, Ordering::SeqCst);
    let t = Instant::now();
    while t.elapsed() < max && *LAST_SEND.lock().unwrap() == before {
        std::thread::sleep(Duration::from_millis(100));
    }
}

/// Hosts the relay posts to: routed outside the tunnel, so logs still arrive while the exit holds
/// the tunnel's traffic (exactly when they are needed).
pub fn bypass_hosts() -> Vec<&'static str> {
    if available() { vec!["ntfy.sh"] } else { Vec::new() }
}

/// Sends what the log has now (after the diagnostics in gnosis/mod.rs): same as flush_now.
pub fn send_log() {
    let _ = flush_now();
}

fn stream() {
    let mut budget = load_budget();
    let mut cursor = 0u64;
    let mut pending: VecDeque<String> = VecDeque::new();
    let mut skipped = 0u64;
    let mut backoff = 1u32; // multiplies the interval after failed posts (rate limits, no network)
    let mut last = Instant::now() - Duration::from_secs(3600);
    let mut told_spent = false;
    loop {
        std::thread::sleep(Duration::from_secs(1));
        let (new, next, lost) = crate::log::since(cursor);
        cursor = next;
        skipped += lost;
        pending.extend(new);
        while pending.len() > MAX_PENDING {
            pending.pop_front();
            skipped += 1;
        }
        if pending.is_empty() {
            continue;
        }
        if budget.sent >= LOG_BUDGET {
            if !told_spent {
                told_spent = true;
                crate::log::line(format!("debug relay: all {LOG_BUDGET} log messages of this client are used; no more logs are sent"));
            }
            pending.clear();
            continue;
        }
        let since_last = last.elapsed().as_secs();
        let error = pending.iter().any(|l| pure::is_error_line(l));
        let asked = FLUSH.load(Ordering::SeqCst);
        let periodic = since_last >= pure::stream_interval(budget.sent) * backoff as u64 && pure::may_send(budget.sent, false);
        let urgent = (error || asked) && since_last >= pure::error_spacing(budget.sent) * backoff as u64 && pure::may_send(budget.sent, true);
        let last_call = FINAL.swap(false, Ordering::SeqCst) && pure::may_send(budget.sent, true);
        if !(periodic || urgent || last_call) {
            continue;
        }
        FLUSH.store(false, Ordering::SeqCst);
        last = Instant::now();
        *LAST_SEND.lock().unwrap() = Some(last);
        let lines: Vec<String> = pending.iter().cloned().collect();
        let (body, dropped) = match pack(&budget, &lines, skipped) {
            Ok(x) => x,
            Err(e) => {
                crate::log::line(format!("debug relay: not sending: {e}"));
                pending.clear();
                continue;
            }
        };
        let title = format!("gvpndows log {} #{}/{LOG_BUDGET} (encrypted, zlib)", budget.client, budget.sent + 1);
        match post(&title, &body) {
            Ok(()) => {
                budget.sent += 1;
                save_budget(&budget);
                pending.clear();
                skipped = 0;
                backoff = 1;
                let _ = dropped;
            }
            Err(e) => {
                backoff = (backoff * 2).min(32);
                // not logged as an error line: that would trigger another urgent send
                crate::log::line(format!("debug relay: post failed ({e}); next try in {} s", pure::error_spacing(budget.sent) * backoff as u64));
            }
        }
    }
}

/// One message: a header plus as many lines as fit, compressed and sealed. Important lines
/// (errors, warnings, this app's own) are kept before the rest; within each group the newest win.
fn pack(b: &Budget, lines: &[String], skipped: u64) -> Result<(String, usize), String> {
    let mut keep_other = lines.len();
    let mut keep_important = lines.len();
    loop {
        let chosen = pure::select_lines(lines, keep_important, keep_other);
        let dropped = lines.len() - chosen.len();
        let header = format!("client {} message {}/{LOG_BUDGET}: {} lines, {} not included, {} lost before\n", b.client, b.sent + 1, chosen.len(), dropped, skipped);
        let mut z = flate2::write::ZlibEncoder::new(Vec::new(), flate2::Compression::best());
        z.write_all(header.as_bytes()).and_then(|_| z.write_all(chosen.join("\n").as_bytes())).map_err(|e| e.to_string())?;
        let packed = z.finish().map_err(|e| e.to_string())?;
        if packed.len() <= MAX_COMPRESSED || chosen.len() <= 1 {
            return Ok((crate::gnosis::backup::seal(&packed)?, dropped));
        }
        // too big: halve the ordinary lines first, then the important ones
        if keep_other > 0 {
            keep_other /= 2;
        } else {
            keep_important /= 2;
        }
    }
}
