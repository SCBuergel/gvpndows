//! PRODUCTION (release) version of the developer relay: it does nothing.
//!
//! Builds without the Cargo feature `debug-relay` compile this file instead of src/relay.rs, so a
//! release contains no code that sends logs, keys or the node identity anywhere. The functions
//! exist only so the callers stay the same; every one is a no-op or reports "not in this build".

/// False: this build never sends anything to the developer.
pub const ACTIVE: bool = false;

pub fn available() -> bool {
    false
}

pub fn post_text(_title: &str, _body: &str) -> Result<(), String> {
    Err("release build: no developer relay".into())
}

pub fn send_key(_public_key: String) {}

pub fn send_log() {}

pub fn selftest() -> Result<(), String> {
    Err("release build: no developer relay".into())
}

pub fn start_stream() {}

pub fn flush_final(_max: std::time::Duration) {}

pub fn flush_now() -> Result<(), String> {
    Err("release build: logs are never sent".into())
}
