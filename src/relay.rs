//! "Send to developer": posts the app's public key or its log to a private ntfy.sh topic that the
//! developer machine reads. Only on a button click. The topic name is set at build time
//! (GVPNDOWS_NTFY_TOPIC) and is not in the repository; builds without it hide the buttons.

const TOPIC: Option<&str> = option_env!("GVPNDOWS_NTFY_TOPIC");
const CHUNK: usize = 3500; // ntfy turns bodies over 4 KB into attachments; stay below

pub fn available() -> bool {
    TOPIC.is_some_and(|t| !t.is_empty())
}

fn post(title: &str, body: &str) -> Result<(), String> {
    let topic = TOPIC.ok_or("no relay topic in this build")?;
    ureq::post(&format!("https://ntfy.sh/{topic}"))
        .timeout(std::time::Duration::from_secs(20))
        .set("Title", title)
        .send_string(body)
        .map(|_| ())
        .map_err(|e| e.to_string())
}

pub fn send_key(public_key: String) {
    std::thread::spawn(move || match post("gvpndows public key", &format!("pubkey {public_key}")) {
        Ok(()) => crate::log::line("public key sent to the developer"),
        Err(e) => crate::log::line(format!("sending the public key failed: {e}")),
    });
}

/// Sends the in-memory log (this run) in numbered chunks.
pub fn send_log() {
    std::thread::spawn(|| {
        let text = crate::log::snapshot().join("\n");
        let mut parts: Vec<String> = Vec::new();
        let mut cur = String::new();
        for line in text.lines() {
            if cur.len() + line.len() + 1 > CHUNK && !cur.is_empty() {
                parts.push(std::mem::take(&mut cur));
            }
            cur.extend(line.chars().take(CHUNK));
            cur.push('\n');
        }
        if !cur.is_empty() {
            parts.push(cur);
        }
        let n = parts.len();
        for (i, p) in parts.iter().enumerate() {
            if let Err(e) = post(&format!("gvpndows log {}/{n}", i + 1), p) {
                crate::log::line(format!("sending the log failed at part {}/{n}: {e}", i + 1));
                return;
            }
        }
        crate::log::line(format!("log sent to the developer ({n} parts)"));
    });
}

/// Blocking post used by --selftest to prove the relay works from the Windows binary.
pub fn selftest() -> Result<(), String> {
    post("gvpndows selftest", "relay selftest from the Windows binary")
}
