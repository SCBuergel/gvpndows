//! Plays the desktop client's privileged "root" process for the in-process core: Wintun adapter,
//! routes, DNS and the in-tunnel ping. Routing mirrors gnosis_vpn-root's static router:
//! - bypass routes via the physical gateway for the HOPR peers, Blokli and private LAN ranges
//!   (the core's own HOPR traffic must not enter the tunnel it carries);
//! - 0.0.0.0/1, 128.0.0.0/1 and the VPN subnet 10.128.0.0/9 into the tunnel;
//! - ::/1 and 8000::/1 into the tunnel as an IPv6 leak guard (the tunnel carries IPv4 only);
//! - DNS servers on the tunnel interface.
//! The adapter stays up across the core's reconnects; only routes change.

use crate::log;
use crate::win::net;
use std::collections::BTreeSet;
use std::io;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::sync::mpsc::{sync_channel, Receiver, SyncSender, TrySendError};
use std::sync::atomic::{AtomicU64, Ordering::Relaxed};
use std::sync::{Arc, Mutex};
use std::time::Duration;

const ADAPTER_NAME: &str = "GnosisVPN";
const ADAPTER_GUID: u128 = 0x6776706e_646f_7773_8000_676e6f736973;
const RFC1918: [(Ipv4Addr, u8); 4] = [
    (Ipv4Addr::new(10, 0, 0, 0), 8),
    (Ipv4Addr::new(172, 16, 0, 0), 12),
    (Ipv4Addr::new(192, 168, 0, 0), 16),
    (Ipv4Addr::new(169, 254, 0, 0), 16),
];

/// Packet counters between Windows and the core, sampled by `telemetry` (1 Hz).
/// up = Windows -> core (what apps send), down = core -> Windows (what apps receive).
pub struct TunCounters {
    pub up_pkts: AtomicU64,
    pub up_bytes: AtomicU64,
    pub up_taken: AtomicU64,
    pub down_pkts: AtomicU64,
    pub down_bytes: AtomicU64,
    pub dropped: AtomicU64,
    /// Packets from this process's own UDP sockets (the HOPR node) that Windows routed into the
    /// tunnel; dropped (see `leak_check`).
    pub leaked: AtomicU64,
}
pub static TUN: TunCounters = TunCounters {
    up_pkts: AtomicU64::new(0),
    up_bytes: AtomicU64::new(0),
    up_taken: AtomicU64::new(0),
    down_pkts: AtomicU64::new(0),
    down_bytes: AtomicU64::new(0),
    dropped: AtomicU64::new(0),
    leaked: AtomicU64::new(0),
};

/// Local UDP ports owned by this process (the HOPR node's sockets), refreshed every 5 s.
static OWN_UDP_PORTS: Mutex<Vec<u16>> = Mutex::new(Vec::new());
fn own_udp_ports() -> Vec<u16> {
    use windows_sys::Win32::NetworkManagement::IpHelper::{GetExtendedUdpTable, MIB_UDPROW_OWNER_PID, UDP_TABLE_OWNER_PID};
    let pid = std::process::id();
    let mut size = 0u32;
    unsafe {
        GetExtendedUdpTable(std::ptr::null_mut(), &mut size, 0, 2 /* AF_INET */, UDP_TABLE_OWNER_PID, 0);
        let mut buf = vec![0u32; (size as usize / 4) + 64];
        size = (buf.len() * 4) as u32;
        if GetExtendedUdpTable(buf.as_mut_ptr().cast(), &mut size, 0, 2, UDP_TABLE_OWNER_PID, 0) != 0 {
            return Vec::new();
        }
        let n = buf[0] as usize;
        let rows = std::slice::from_raw_parts(buf.as_ptr().add(1) as *const MIB_UDPROW_OWNER_PID, n);
        rows.iter().filter(|r| r.dwOwningPid == pid).map(|r| u16::from_be(r.dwLocalPort as u16)).collect()
    }
}

fn port_refresher() {
    loop {
        let ports = own_udp_ports();
        *OWN_UDP_PORTS.lock().unwrap() = ports;
        std::thread::sleep(Duration::from_secs(5));
    }
}

/// True if this packet is the HOPR node's own UDP traffic, which must never enter the tunnel it
/// carries: it is dropped. No route is added for its destination: announced addresses are chosen
/// by third parties, and a route would send every app's traffic to that address outside the VPN.
/// (The process-scoped fix is to bind HOPR's sockets to the WAN address.)
fn leak_check(p: &[u8]) -> bool {
    let Some(dst) = OWN_UDP_PORTS.lock().ok().and_then(|ports| crate::pure::hopr_leak_dst(p, &ports)) else { return false };
    let n = TUN.leaked.fetch_add(1, Relaxed) + 1;
    if n.is_power_of_two() {
        log::line(format!("tunnel: {n} HOPR packets dropped that Windows routed into the tunnel (latest to {dst})"));
    }
    true
}

/// The Wintun session as the core's packet device. One permanent reader thread drains the
/// ring into a channel, so any number of consumers (one per core reconnect) can poll it.
struct WintunDevice {
    session: Arc<wintun::Session>,
    rx: Mutex<Receiver<Vec<u8>>>,
}

impl gnosis_vpn_lib::wg_tunnel::PacketDevice for WintunDevice {
    fn recv_timeout(&self, timeout: Duration) -> io::Result<Option<Vec<u8>>> {
        let rx = self.rx.lock().map_err(|_| io::Error::other("tun channel poisoned"))?;
        match rx.recv_timeout(timeout) {
            Ok(p) => {
                TUN.up_taken.fetch_add(1, Relaxed);
                Ok(Some(p))
            }
            Err(std::sync::mpsc::RecvTimeoutError::Timeout) => Ok(None),
            Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => Err(io::Error::other("tunnel adapter closed")),
        }
    }

    fn send(&self, packet: &[u8]) -> io::Result<()> {
        let len = u16::try_from(packet.len()).map_err(|_| io::Error::other("packet too large"))?;
        let mut p = self.session.allocate_send_packet(len).map_err(|e| io::Error::other(e.to_string()))?;
        p.bytes_mut().copy_from_slice(packet);
        self.session.send_packet(p);
        TUN.down_pkts.fetch_add(1, Relaxed);
        TUN.down_bytes.fetch_add(packet.len() as u64, Relaxed);
        Ok(())
    }
}

fn reader_thread(session: Arc<wintun::Session>, tx: SyncSender<Vec<u8>>) {
    let mut dropped = 0u64;
    loop {
        let Ok(pkt) = session.receive_blocking() else { break }; // session shut down
        let len = pkt.bytes().len() as u64;
        if leak_check(pkt.bytes()) {
            continue;
        }
        match tx.try_send(pkt.bytes().to_vec()) {
            Ok(()) => {
                TUN.up_pkts.fetch_add(1, Relaxed);
                TUN.up_bytes.fetch_add(len, Relaxed);
            }
            Err(TrySendError::Full(_)) => {
                dropped += 1;
                TUN.dropped.fetch_add(1, Relaxed);
                if dropped.is_power_of_two() {
                    log::line(format!("tunnel: {dropped} packets dropped (core not reading)"));
                }
            }
            Err(TrySendError::Disconnected(_)) => break,
        }
    }
}

struct Adapter {
    _adapter: Arc<wintun::Adapter>,
    session: Arc<wintun::Session>,
    luid: u64,
    address: Option<(IpAddr, u8)>,
}

#[derive(Default)]
pub struct Root {
    adapter: Option<Adapter>,
    /// WAN interface and gateway, learned before our routes existed.
    wan: Option<(u64, IpAddr)>,
    bypass: Vec<net::Route>,
    bypass_peers: BTreeSet<Ipv4Addr>,
    tunnel_routes: Vec<net::Route>,
    pub blokli_ip: Option<Ipv4Addr>,
}

impl Root {
    pub fn new(blokli_ip: Ipv4Addr) -> Self {
        Root { blokli_ip: Some(blokli_ip), ..Default::default() }
    }

    fn ensure_adapter(&mut self) -> Result<u64, String> {
        if let Some(a) = &self.adapter {
            return Ok(a.luid);
        }
        let w = crate::win::tunnel::wintun()?;
        let adapter = match wintun::Adapter::create(&w, ADAPTER_NAME, "GnosisVPN", Some(ADAPTER_GUID)) {
            Ok(a) => a,
            Err(e) => {
                log::line(format!("adapter create failed ({e}); opening the existing one"));
                wintun::Adapter::open(&w, ADAPTER_NAME).map_err(|e| format!("Wintun adapter: {e}"))?
            }
        };
        let luid = unsafe { adapter.get_luid().Value };
        let session = Arc::new(adapter.start_session(wintun::MAX_RING_CAPACITY).map_err(|e| format!("Wintun session: {e}"))?);
        let (tx, rx) = sync_channel(4096);
        let s = session.clone();
        static PORTS: std::sync::Once = std::sync::Once::new();
        PORTS.call_once(|| {
            *OWN_UDP_PORTS.lock().unwrap() = own_udp_ports();
            let _ = std::thread::Builder::new().name("udp-ports".into()).spawn(port_refresher);
        });
        std::thread::Builder::new().name("wintun-reader".into()).spawn(move || reader_thread(s, tx)).map_err(|e| e.to_string())?;
        gnosis_vpn_lib::wg_tunnel::set_packet_device(Arc::new(WintunDevice { session: session.clone(), rx: Mutex::new(rx) }));
        log::line(format!("tunnel adapter {ADAPTER_NAME} created (luid {luid:#x})"));
        self.adapter = Some(Adapter { _adapter: adapter, session, luid, address: None });
        Ok(luid)
    }

    /// The WAN route, looked up towards Blokli; never our own tunnel.
    fn wan(&mut self, tun_luid: u64) -> Result<(u64, IpAddr), String> {
        let probe = IpAddr::V4(self.blokli_ip.unwrap_or(Ipv4Addr::new(9, 9, 9, 9)));
        match net::best_route(probe) {
            Ok((luid, gw)) if luid != tun_luid => {
                if self.wan != Some((luid, gw)) {
                    log::line(format!("WAN: next hop {gw}, if luid {luid:#x}"));
                }
                self.wan = Some((luid, gw));
                Ok((luid, gw))
            }
            _ => self.wan.ok_or_else(|| "cannot determine the WAN route".to_string()),
        }
    }

    fn add_bypass(&mut self, dest: IpAddr, len: u8, wan: (u64, IpAddr)) {
        let r = net::Route { if_luid: wan.0, dest, prefix_len: len, next_hop: wan.1 };
        match net::add_route(&r) {
            Ok(true) => self.bypass.push(r),
            Ok(false) => {} // already there (not ours)
            Err(e) => log::line(format!("bypass: {e}")),
        }
    }

    pub fn setup_tunnel(&mut self, interface_address: &str, mtu: u32, dns: Option<&str>, peer_ips: &[Ipv4Addr]) -> Result<String, String> {
        let t0 = std::time::Instant::now();
        let (ip, len) = parse_cidr(interface_address)?;
        let tun_luid = self.ensure_adapter()?;
        let wan = self.wan(tun_luid)?;

        // 1. Bypass routes first, so HOPR connections never take the tunnel they carry.
        let mut peers: BTreeSet<Ipv4Addr> = peer_ips.iter().copied().collect();
        if let Some(b) = self.blokli_ip {
            peers.insert(b);
        }
        for p in peers.difference(&self.bypass_peers.clone()).copied().collect::<Vec<_>>() {
            self.add_bypass(IpAddr::V4(p), 32, wan);
        }
        self.bypass_peers.extend(peers);
        if !self.bypass.iter().any(|r| r.prefix_len < 32) {
            for (net, l) in RFC1918 {
                self.add_bypass(IpAddr::V4(net), l, wan);
            }
        }

        // 2. Address, MTU, metric.
        let adapter = self.adapter.as_mut().expect("adapter");
        if adapter.address != Some((ip, len)) {
            if let Some((old, old_len)) = adapter.address {
                let _ = net::delete_address(tun_luid, old, old_len);
            }
            net::add_address(tun_luid, ip, len)?;
            adapter.address = Some((ip, len));
        }
        net::set_interface(tun_luid, false, mtu, 5)?;
        let _ = net::set_interface(tun_luid, true, mtu.max(1280), 5);

        // 3. Routes into the tunnel.
        if self.tunnel_routes.is_empty() {
            let v4 = Ipv4Addr::UNSPECIFIED.into();
            let v6 = Ipv6Addr::UNSPECIFIED.into();
            let routes: [(IpAddr, u8, IpAddr); 5] = [
                (Ipv4Addr::new(0, 0, 0, 0).into(), 1, v4),
                (Ipv4Addr::new(128, 0, 0, 0).into(), 1, v4),
                (Ipv4Addr::new(10, 128, 0, 0).into(), 9, v4),
                (Ipv6Addr::UNSPECIFIED.into(), 1, v6),
                (Ipv6Addr::new(0x8000, 0, 0, 0, 0, 0, 0, 0).into(), 1, v6),
            ];
            for (dest, l, nh) in routes {
                let r = net::Route { if_luid: tun_luid, dest, prefix_len: l, next_hop: nh };
                match net::add_route(&r) {
                    Ok(_) => self.tunnel_routes.push(r),
                    Err(e) if dest.is_ipv6() => log::line(format!("{e} (IPv6 leak guard incomplete)")),
                    Err(e) => return Err(e),
                }
            }
        }

        // 4. DNS (comma-separated list from the core).
        if let Some(dns) = dns {
            let servers: Vec<IpAddr> = dns.split(',').filter_map(|s| s.trim().parse().ok()).collect();
            if !servers.is_empty() {
                let a = self.adapter.as_ref().expect("adapter");
                a._adapter.set_dns_servers(&servers).map_err(|e| format!("DNS: {e}"))?;
            }
        }
        log::line(format!(
            "tunnel ready: {interface_address}, mtu {mtu}, dns {dns:?}, {} bypass routes, in {} ms",
            self.bypass.len(),
            t0.elapsed().as_millis()
        ));
        Ok(ADAPTER_NAME.to_string())
    }

    pub fn update_peer_ips(&mut self, peer_ips: &[Ipv4Addr]) {
        if self.tunnel_routes.is_empty() {
            return; // applied with the next SetupTunnel
        }
        let Some(tun_luid) = self.adapter.as_ref().map(|a| a.luid) else { return };
        let Ok(wan) = self.wan(tun_luid) else { return };
        let new: Vec<Ipv4Addr> = peer_ips.iter().filter(|p| !self.bypass_peers.contains(p)).copied().collect();
        for p in &new {
            self.add_bypass(IpAddr::V4(*p), 32, wan);
            self.bypass_peers.insert(*p);
        }
        if !new.is_empty() {
            log::line(format!("bypass: {} new HOPR peer routes", new.len()));
        }
    }

    /// Removes the routes (the core disconnected); the adapter stays for the next connect.
    pub fn teardown_routes(&mut self) {
        for r in self.tunnel_routes.drain(..).rev() {
            if let Err(e) = net::delete_route(&r) {
                log::line(format!("teardown: {e}"));
            }
        }
        for r in self.bypass.drain(..).rev() {
            if let Err(e) = net::delete_route(&r) {
                log::line(format!("teardown: {e}"));
            }
        }
        self.bypass_peers.clear();
        log::line("tunnel routes removed");
    }

    /// Everything, including the adapter (app exit or core stop).
    pub fn shutdown(&mut self) {
        self.teardown_routes();
        gnosis_vpn_lib::wg_tunnel::clear_packet_device();
        if let Some(a) = self.adapter.take() {
            let _ = a.session.shutdown();
            log::line("tunnel adapter removed");
        }
    }
}

fn parse_cidr(s: &str) -> Result<(IpAddr, u8), String> {
    let (a, l) = s.split_once('/').unwrap_or((s, ""));
    let ip: IpAddr = a.trim().parse().map_err(|_| format!("bad interface address {s:?}"))?;
    let max = if ip.is_ipv4() { 32 } else { 128 };
    let len = if l.is_empty() { max } else { l.parse().map_err(|_| format!("bad prefix in {s:?}"))? };
    Ok((ip, len))
}

/// In-tunnel ping with Windows' ICMP API (no ping.exe, so no locale-dependent output).
pub fn icmp_ping(opts: &gnosis_vpn_lib::ping::Options) -> Result<Duration, String> {
    use windows_sys::Win32::NetworkManagement::IpHelper::{IcmpCloseHandle, IcmpCreateFile, IcmpSendEcho, ICMP_ECHO_REPLY, IP_OPTION_INFORMATION};
    let IpAddr::V4(dst) = opts.address else { return Err("IPv6 ping not supported".into()) };
    unsafe {
        let h = IcmpCreateFile();
        if h.is_null() || h as isize == -1 {
            return Err(format!("IcmpCreateFile: {}", io::Error::last_os_error()));
        }
        let mut total = Duration::ZERO;
        let mut ok = 0u32;
        let payload = *b"gnosisvpn-ping-gvpndows-";
        let mut reply = vec![0u8; std::mem::size_of::<ICMP_ECHO_REPLY>() + payload.len() + 64];
        let mut opt: IP_OPTION_INFORMATION = std::mem::zeroed();
        opt.Ttl = opts.ttl.min(255) as u8;
        for _ in 0..opts.seq_count.max(1) {
            let n = IcmpSendEcho(
                h,
                u32::from_ne_bytes(dst.octets()),
                payload.as_ptr().cast(),
                payload.len() as u16,
                &opt,
                reply.as_mut_ptr().cast(),
                reply.len() as u32,
                opts.timeout.as_millis().min(u32::MAX as u128) as u32,
            );
            if n > 0 {
                let r = &*(reply.as_ptr() as *const ICMP_ECHO_REPLY);
                if r.Status == 0 {
                    total += Duration::from_millis(r.RoundTripTime as u64);
                    ok += 1;
                }
            }
        }
        IcmpCloseHandle(h);
        if ok == 0 {
            Err(format!("no echo reply from {dst}"))
        } else {
            Ok(total / ok)
        }
    }
}
