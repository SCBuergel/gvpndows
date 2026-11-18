//! "Send to developer": posts the app's public key or its log to a private ntfy.sh topic that the
//! developer machine reads. Only on a button click. The topic name is set at build time
//! (GVPNDOWS_NTFY_TOPIC) and is not in the repository; builds without it hide the buttons.

const TOPIC: Option<&str> = option_env!("GVPNDOWS_NTFY_TOPIC");
const CHUNK: usize = 3500; // ntfy turns bodies over 4 KB into attachments; stay below

pub fn available() -> bool {
    TOPIC.is_some_and(|t| !t.is_empty())
}

pub fn post_text(title: &str, body: &str) -> Result<(), String> {
    post(title, body)
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
        let parts = crate::pure::chunk_log(&text, CHUNK);
        // A stalled tunnel also stalls DNS and HTTPS, so failed parts are retried every 30 s for
        // up to 15 minutes (e.g. until the user disconnects); sent parts are not sent again.
        let n = parts.len();
        let mut i = 0;
        let mut tries = 0;
        while i < n {
            match post(&format!("gvpndows log {}/{n}", i + 1), &parts[i]) {
                Ok(()) => i += 1,
                Err(e) => {
                    tries += 1;
                    if tries > 30 {
                        crate::log::line(format!("sending the log gave up at part {}/{n}: {e}", i + 1));
                        return;
                    }
                    if tries == 1 {
                        crate::log::line(format!("sending the log failed at part {}/{n}: {e}; retrying every 30 s", i + 1));
                    }
                    std::thread::sleep(std::time::Duration::from_secs(30));
                }
            }
        }
        crate::log::line(format!("log sent to the developer ({n} parts{})", if tries > 0 { ", after retries" } else { "" }));
    });
}

/// Blocking post used by --selftest to prove the relay works from the Windows binary.
pub fn selftest() -> Result<(), String> {
    post("gvpndows selftest", "relay selftest from the Windows binary")
}
