//! Plays the desktop client's privileged "root" process for the in-process core: Wintun adapter,
//! routes, DNS and the in-tunnel ping. Routing mirrors gnosis_vpn-root's static router:
//! - bypass routes via the physical gateway for the HOPR peers, Blokli and private LAN ranges
//!   (the core's own HOPR traffic must not enter the tunnel it carries);
//! - 0.0.0.0/1, 128.0.0.0/1 and the VPN subnet 10.128.0.0/9 into the tunnel;
//! - ::/1 and 8000::/1 into the tunnel as an IPv6 leak guard (the tunnel carries IPv4 only);
//! - DNS servers on the tunnel interface, and the WFP DNS guard (port 53 only through the tunnel
//!   or loopback) while the routes are up, as upstream diverts all queries with resolvectl `~.`.
//! The adapter stays up across the core's reconnects; only routes change.

use crate::log;
use crate::win::{net, wfp};
use std::collections::BTreeSet;
use std::io;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::sync::mpsc::{sync_channel, Receiver, SyncSender, TrySendError};
use std::sync::atomic::{AtomicU64, Ordering::Relaxed};
use std::sync::{Arc, Mutex};
use std::time::Duration;

const ADAPTER_NAME: &str = "GnosisVPN";
const ADAPTER_GUID: u128 = 0x6776706e_646f_7773_8000_676e6f736973;

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
    /// When the core last listed each peer IP (ms, see `now_ms`); unlisted for 300 s, its route goes.
    peer_seen: std::collections::BTreeMap<Ipv4Addr, u64>,
    /// Recorded bypass routes of an earlier run that could not be removed at start: ours.
    stale_bypass: Vec<net::Route>,
    tunnel_routes: Vec<net::Route>,
    /// Blocks DNS outside the tunnel (adapter LUID) while the routes are up; dropping it removes
    /// the filters.
    dns_guard: Option<(u64, wfp::KillSwitch)>,
    pub blokli_ip: Option<Ipv4Addr>,
    /// DEBUG FEATURE ONLY: the developer log relay's addresses, routed outside the tunnel so logs
    /// still arrive while the exit holds the tunnel's traffic. Empty in release builds. Everything
    /// else (including the Curvy relayer) goes through the tunnel, as on macOS and Linux.
    pub service_ips: Vec<Ipv4Addr>,
}

fn resolve_v4(host: &str) -> Vec<Ipv4Addr> {
    use std::net::ToSocketAddrs;
    match (host, 443).to_socket_addrs() {
        Ok(a) => a.filter_map(|s| match s.ip() { IpAddr::V4(v) => Some(v), _ => None }).collect(),
        Err(e) => {
            log::line(format!("gnosis: cannot resolve {host} for the tunnel bypass: {e}"));
            Vec::new()
        }
    }
}

impl Root {
    pub fn new(blokli_ip: Ipv4Addr) -> Self {
        let hosts = crate::relay::bypass_hosts(); // DEBUG FEATURE ONLY: the log relay (none in release builds)
        let mut service_ips: Vec<Ipv4Addr> = hosts.iter().flat_map(|h| resolve_v4(h)).collect();
        service_ips.sort();
        service_ips.dedup();
        if !hosts.is_empty() {
            log::line(format!("gnosis: DEBUG BUILD: the log relay {} ({}) goes outside the tunnel", hosts.join(", "), service_ips.iter().map(|i| i.to_string()).collect::<Vec<_>>().join(", ")));
        }
        Root { blokli_ip: Some(blokli_ip), service_ips, stale_bypass: sweep_bypass(), ..Default::default() }
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

    /// The WAN: the best IPv4 default route outside our tunnel, read from the route table, so our own
    /// /1 and /32 routes cannot hide a network switch (the route towards Blokli only if the table
    /// has none). When it changed, the bypass routes made for the old WAN go, to be added again.
    fn wan(&mut self, tun_luid: u64) -> Result<(u64, IpAddr), String> {
        let found = net::default_routes().ok().and_then(|d| crate::pure::pick_wan(&d, tun_luid)).or_else(|| {
            let probe = IpAddr::V4(self.blokli_ip.unwrap_or(Ipv4Addr::new(9, 9, 9, 9)));
            net::best_route(probe).ok().filter(|&(luid, _)| luid != tun_luid)
        });
        let Some((luid, gw)) = found else {
            return self.wan.ok_or_else(|| "cannot determine the WAN route".to_string());
        };
        if self.wan != Some((luid, gw)) {
            log::line(format!("WAN: next hop {gw}, if luid {luid:#x}"));
            if self.wan.is_some() && !self.bypass_peers.is_empty() {
                log::line("WAN changed: renewing the bypass routes");
                self.remove_bypass();
            }
        }
        self.wan = Some((luid, gw));
        Ok((luid, gw))
    }

    fn remove_bypass(&mut self) {
        // ours too: routes an earlier run left that this run did not add again
        let mut ours: Vec<net::Route> = self.bypass.drain(..).rev().collect();
        for s in self.stale_bypass.drain(..) {
            if !ours.iter().any(|r| rec(r) == rec(&s)) {
                ours.push(s);
            }
        }
        let mut kept = Vec::new();
        for r in ours {
            if let Err(e) = net::delete_route(&r) {
                log::line(format!("bypass: {e}"));
                kept.push(r);
            }
        }
        record_bypass(&kept, false); // retried by the next teardown or start
        self.stale_bypass = kept;
        self.bypass_peers.clear();
    }

    /// Brings the peer bypass routes in line with the core's `listed` peer IPs (plus Blokli and the
    /// debug relay, always kept): adds the missing ones and removes those of IPs unlisted for 300 s,
    /// then the LAN ranges; returns how many peer routes were added.
    fn add_bypass_peers(&mut self, listed: &[Ipv4Addr], wan: (u64, IpAddr)) -> usize {
        let always: Vec<Ipv4Addr> = self.blokli_ip.into_iter().chain(self.service_ips.iter().copied()).collect();
        let (add, gone) = crate::pure::reconcile_peers(&mut self.peer_seen, listed, &always, &self.bypass_peers, now_ms());
        if !gone.is_empty() {
            self.remove_peer_bypass(&gone);
        }
        let mut added = 0;
        for p in add {
            // a failed add stays unmarked, so the next update retries it
            if self.add_bypass(IpAddr::V4(p), 32, wan) {
                self.bypass_peers.insert(p);
                added += 1;
            }
        }
        if !self.bypass.iter().any(|r| r.prefix_len < 32) {
            for (net, l) in crate::pure::BYPASS_LANS {
                self.add_bypass(IpAddr::V4(net), l, wan);
            }
        }
        added
    }

    /// Deletes our /32 bypass routes to `ips`; one that cannot be deleted stays ours and marked,
    /// so the next update tries again.
    fn remove_peer_bypass(&mut self, ips: &[Ipv4Addr]) {
        let mut keep = Vec::new();
        for r in self.bypass.drain(..) {
            let IpAddr::V4(d) = r.dest else { keep.push(r); continue };
            if r.prefix_len != 32 || !ips.contains(&d) {
                keep.push(r);
            } else if let Err(e) = net::delete_route(&r) {
                log::line(format!("bypass: {e}"));
                keep.push(r);
            } else {
                self.bypass_peers.remove(&d);
            }
        }
        self.bypass = keep;
        let mut ours = self.bypass.clone();
        ours.extend(self.stale_bypass.iter().cloned());
        record_bypass(&ours, false);
        log::line(format!("bypass: removed the routes of {} peers no longer listed", ips.len()));
    }

    /// Adds a bypass route; true when it is in place and ours (an Err or another's route: false).
    fn add_bypass(&mut self, dest: IpAddr, len: u8, wan: (u64, IpAddr)) -> bool {
        let r = net::Route { if_luid: wan.0, dest, prefix_len: len, next_hop: wan.1 };
        match net::add_route(&r) {
            Ok(true) => {
                record_bypass(&[r.clone()], true);
                self.bypass.push(r);
                true
            }
            // already there: ours if an earlier run recorded it and the sweep could not remove it
            Ok(false) if self.stale_bypass.iter().any(|s| rec(s) == rec(&r)) => {
                self.bypass.push(r);
                true
            }
            Ok(false) => false, // not ours
            Err(e) => {
                log::line(format!("bypass: {e}"));
                false
            }
        }
    }

    pub fn setup_tunnel(&mut self, interface_address: &str, mtu: u32, dns: Option<&str>, peer_ips: &[Ipv4Addr]) -> Result<String, String> {
        let t0 = std::time::Instant::now();
        let (ip, len) = parse_cidr(interface_address)?;
        let tun_luid = self.ensure_adapter()?;
        let wan = self.wan(tun_luid)?;

        // 1. Bypass routes first, so HOPR connections never take the tunnel they carry.
        if self.service_ips.is_empty() && self.tunnel_routes.is_empty() {
            // not resolved at start (no network yet): try again while DNS still goes direct
            let hosts = crate::relay::bypass_hosts();
            self.service_ips = hosts.iter().flat_map(|h| resolve_v4(h)).collect();
        }
        self.add_bypass_peers(peer_ips, wan);

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

        // 3. Routes into the tunnel: each one not in place yet, as an earlier setup may have stopped
        // part-way. Routes recorded on another LUID (an older adapter) do not count.
        for r in self.tunnel_routes.iter().filter(|r| r.if_luid != tun_luid) {
            let _ = net::delete_route(r);
        }
        self.tunnel_routes.retain(|r| r.if_luid == tun_luid);
        let present: Vec<(u64, IpAddr, u8)> = self.tunnel_routes.iter().map(|r| (r.if_luid, r.dest, r.prefix_len)).collect();
        for (dest, l) in crate::pure::missing_tunnel_routes(&present, tun_luid) {
            let nh: IpAddr = if dest.is_ipv4() { Ipv4Addr::UNSPECIFIED.into() } else { Ipv6Addr::UNSPECIFIED.into() };
            let r = net::Route { if_luid: tun_luid, dest, prefix_len: l, next_hop: nh };
            match net::add_route(&r) {
                // Ok(false): the identical route on our own adapter already exists (left by an earlier run)
                Ok(_) => self.tunnel_routes.push(r),
                Err(e) if dest.is_ipv6() => log::line(format!("{e} (IPv6 leak guard incomplete; retried on the next setup)")),
                Err(e) => return Err(e),
            }
        }

        // 4. DNS (comma-separated list from the core), and only through the tunnel.
        let servers = crate::pure::tunnel_dns_servers(dns);
        if !servers.is_empty() {
            let a = self.adapter.as_ref().expect("adapter");
            a._adapter.set_dns_servers(&servers).map_err(|e| format!("DNS: {e}"))?;
            if self.dns_guard.as_ref().map(|g| g.0) != Some(tun_luid) {
                self.dns_guard = None; // an older adapter's guard goes first (same sublayer key)
                self.dns_guard = Some((tun_luid, wfp::KillSwitch::dns_guard(tun_luid)?));
                log::line("DNS guard on: port 53 only through the tunnel");
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
        let new = self.add_bypass_peers(peer_ips, wan);
        if new > 0 {
            log::line(format!("bypass: {new} new peer routes"));
        }
    }

    /// Removes the routes (the core disconnected); the adapter stays for the next connect.
    pub fn teardown_routes(&mut self) {
        if self.dns_guard.take().is_some() {
            log::line("DNS guard off");
        }
        for r in self.tunnel_routes.drain(..).rev() {
            if let Err(e) = net::delete_route(&r) {
                log::line(format!("teardown: {e}"));
            }
        }
        self.remove_bypass();
        self.peer_seen.clear();
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

/// Milliseconds since the first call (a monotonic clock for the peer expiry).
fn now_ms() -> u64 {
    static START: std::sync::OnceLock<std::time::Instant> = std::sync::OnceLock::new();
    START.get_or_init(std::time::Instant::now).elapsed().as_millis() as u64
}

fn rec(r: &net::Route) -> crate::pure::BypassRec {
    (r.if_luid, r.dest, r.prefix_len, r.next_hop)
}

/// The bypass routes added and not yet removed, in the service's state folder (SYSTEM and
/// Administrators only): a crash or forced stop skips teardown, and these routes outlive the process.
fn bypass_record_path() -> std::path::PathBuf {
    super::user_dirs().data.join("bypass-routes.txt")
}

/// Appends `routes` to the record, or replaces it with them.
fn record_bypass(routes: &[net::Route], append: bool) {
    use std::io::Write;
    let path = bypass_record_path();
    let text: String = routes.iter().map(|r| crate::pure::bypass_record_line(&rec(r))).collect();
    let res = std::fs::create_dir_all(path.parent().expect("data dir"))
        .and_then(|_| std::fs::OpenOptions::new().create(true).write(true).append(append).truncate(!append).open(&path))
        .and_then(|mut f| f.write_all(text.as_bytes()));
    if let Err(e) = res {
        log::line(format!("bypass: cannot record routes in {}: {e}", path.display()));
    }
}

/// Removes the bypass routes an earlier run recorded and left behind; returns those still in place.
fn sweep_bypass() -> Vec<net::Route> {
    let Ok(text) = std::fs::read_to_string(bypass_record_path()) else { return Vec::new() };
    let mut kept = Vec::new();
    let recs = crate::pure::parse_bypass_records(&text);
    for (if_luid, dest, prefix_len, next_hop) in recs.iter().copied() {
        let r = net::Route { if_luid, dest, prefix_len, next_hop };
        if let Err(e) = net::delete_route(&r) {
            log::line(format!("bypass sweep: {e}"));
            kept.push(r);
        }
    }
    if !recs.is_empty() {
        log::line(format!("bypass sweep: cleared {} routes recorded by an earlier run", recs.len() - kept.len()));
    }
    record_bypass(&kept, false);
    kept
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
