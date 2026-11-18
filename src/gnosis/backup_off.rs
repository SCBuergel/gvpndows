//! PRODUCTION (release) version of the identity backup: it does nothing.
//!
//! Builds without the Cargo feature `debug-relay` compile this file instead of backup.rs, so a
//! release never sends the node identity (or anything sealed with it) to the developer.

pub fn available() -> bool {
    false
}

pub fn send_once() {}

pub fn send() {}

pub fn identity_exists() -> bool {
    super::user_dirs().network_data(super::NETWORK).join("hopr.id").exists()
}

pub fn seal(_plaintext: &[u8]) -> Result<String, String> {
    Err("release build: no developer key".into())
}
