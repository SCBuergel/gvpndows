//! WireGuard settings: the app's own key pair (generated on this machine, private key never shown)
//! and the peer config (wg-quick style text, pasted by the user, private key optional).

use base64::engine::general_purpose::STANDARD as B64;
use base64::Engine;
use neptun::x25519::{PublicKey, StaticSecret};
use std::net::{IpAddr, SocketAddr, ToSocketAddrs};
use std::path::PathBuf;

#[derive(Clone, Debug, PartialEq)]
pub struct Prefix {
    pub addr: IpAddr,
    pub len: u8,
}

impl std::fmt::Display for Prefix {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}/{}", self.addr, self.len)
    }
}

fn parse_prefix(s: &str) -> Result<Prefix, String> {
    let (a, l) = match s.split_once('/') {
        Some((a, l)) => (a, Some(l)),
        None => (s, None),
    };
    let addr: IpAddr = a.trim().parse().map_err(|_| format!("bad address {s:?}"))?;
    let max = if addr.is_ipv4() { 32 } else { 128 };
    let len = match l {
        Some(l) => l.trim().parse::<u8>().ok().filter(|&n| n <= max).ok_or(format!("bad prefix length in {s:?}"))?,
        None => max,
    };
    Ok(Prefix { addr, len })
}

#[derive(Clone, Debug)]
pub struct WgConfig {
    pub private_key: Option<[u8; 32]>,
    pub addresses: Vec<Prefix>,
    pub dns: Vec<IpAddr>,
    pub mtu: u16,
    pub peer_public_key: [u8; 32],
    pub endpoint: String,
    pub allowed_ips: Vec<Prefix>,
    pub keepalive: u16,
}

pub fn decode_key(s: &str) -> Result<[u8; 32], String> {
    let v = B64.decode(s.trim()).map_err(|_| "key is not valid base64".to_string())?;
    v.try_into().map_err(|_| "key is not 32 bytes".to_string())
}

impl WgConfig {
    pub fn parse(text: &str) -> Result<Self, String> {
        let mut c = WgConfig {
            private_key: None,
            addresses: vec![],
            dns: vec![],
            mtu: 1420,
            peer_public_key: [0; 32],
            endpoint: String::new(),
            allowed_ips: vec![],
            keepalive: 25,
        };
        let mut have_peer_key = false;
        for (n, raw) in text.lines().enumerate() {
            let line = raw.split('#').next().unwrap_or("").trim();
            if line.is_empty() || line.starts_with('[') {
                continue;
            }
            let Some((k, v)) = line.split_once('=') else {
                return Err(format!("line {}: expected Key = Value", n + 1));
            };
            let (k, v) = (k.trim().to_ascii_lowercase(), v.trim());
            let list = || v.split(',').map(str::trim).filter(|s| !s.is_empty());
            let err = |e: String| format!("line {}: {e}", n + 1);
            match k.as_str() {
                "privatekey" => c.private_key = Some(decode_key(v).map_err(err)?),
                "address" => {
                    for p in list() {
                        c.addresses.push(parse_prefix(p).map_err(err)?);
                    }
                }
                "dns" => {
                    for p in list() {
                        c.dns.push(p.parse().map_err(|_| err(format!("bad DNS server {p:?}")))?);
                    }
                }
                "mtu" => c.mtu = v.parse().ok().filter(|m| (1280..=1500).contains(m)).ok_or_else(|| err("MTU must be 1280..1500".into()))?,
                "publickey" => {
                    c.peer_public_key = decode_key(v).map_err(err)?;
                    have_peer_key = true;
                }
                "endpoint" => c.endpoint = v.to_string(),
                "allowedips" => {
                    for p in list() {
                        c.allowed_ips.push(parse_prefix(p).map_err(err)?);
                    }
                }
                "persistentkeepalive" => c.keepalive = v.parse().map_err(|_| err("bad PersistentKeepalive".into()))?,
                "presharedkey" => return Err(err("PresharedKey is not supported in this test app".into())),
                _ => {} // ignore other wg-quick keys (ListenPort, Table, PostUp, ...)
            }
        }
        if !have_peer_key {
            return Err("missing PublicKey (the server's key)".into());
        }
        if c.endpoint.is_empty() {
            return Err("missing Endpoint (host:port)".into());
        }
        if !c.addresses.iter().any(|a| a.addr.is_ipv4()) {
            return Err("missing IPv4 Address".into());
        }
        if c.allowed_ips.is_empty() {
            return Err("missing AllowedIPs".into());
        }
        Ok(c)
    }

    /// Resolves the endpoint to one IPv4 socket address (before routes change).
    pub fn resolve_endpoint(&self) -> Result<SocketAddr, String> {
        self.endpoint
            .to_socket_addrs()
            .map_err(|e| format!("cannot resolve endpoint {}: {e}", self.endpoint))?
            .find(|a| a.is_ipv4())
            .ok_or_else(|| format!("endpoint {} has no IPv4 address", self.endpoint))
    }
}

pub fn config_path() -> PathBuf {
    crate::log::dir().join("peer.conf")
}

fn key_path() -> PathBuf {
    crate::log::dir().join("wg-private.key")
}

/// The app's own WireGuard private key: created once with the OS random generator, kept in
/// %LOCALAPPDATA%\gvpndows\wg-private.key (only the user, SYSTEM and admins can read it).
pub fn own_private_key() -> Result<[u8; 32], String> {
    let p = key_path();
    if let Ok(s) = std::fs::read_to_string(&p) {
        return decode_key(&s).map_err(|e| format!("{}: {e}", p.display()));
    }
    let mut k = [0u8; 32];
    getrandom::getrandom(&mut k).map_err(|e| format!("random: {e}"))?;
    // Clamp like `wg genkey` so the stored key is canonical.
    k[0] &= 248;
    k[31] = (k[31] & 127) | 64;
    std::fs::create_dir_all(crate::log::dir()).map_err(|e| e.to_string())?;
    std::fs::write(&p, B64.encode(k)).map_err(|e| format!("cannot write {}: {e}", p.display()))?;
    crate::log::line(format!("generated a new WireGuard key pair ({})", p.display()));
    Ok(k)
}

pub fn public_key_b64(private: &[u8; 32]) -> String {
    B64.encode(PublicKey::from(&StaticSecret::from(*private)).as_bytes())
}

pub const TEMPLATE: &str = "[Interface]
Address = 10.0.0.2/32
DNS = 9.9.9.9
MTU = 1420

[Peer]
PublicKey = <server public key>
Endpoint = <host>:51820
AllowedIPs = 0.0.0.0/0, ::/0
PersistentKeepalive = 25
";

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn parses_router_style_config() {
        let c = WgConfig::parse(
            "[Interface]\nAddress = 10.88.88.7/32\nDNS = 9.9.9.9, 149.112.112.112\n[Peer]\nPublicKey = v99ZdpUIhbBiQrNjDy1Fu6qzXKoJ8XrWoLf7/DfELEc=\nEndpoint = 192.0.2.1:51820\nAllowedIPs = 0.0.0.0/0, ::/0\n",
        )
        .unwrap();
        assert_eq!(c.addresses[0].to_string(), "10.88.88.7/32");
        assert_eq!(c.dns.len(), 2);
        assert_eq!(c.allowed_ips[1].to_string(), "::/0");
        assert_eq!(c.mtu, 1420);
        assert!(WgConfig::parse("[Peer]\nEndpoint = x:1\n").is_err());
    }
}
