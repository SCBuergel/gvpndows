//! Plain WireGuard on Windows: Wintun adapter + neptun (same WireGuard code as gnosis_vpn-client)
//! + a UDP socket to the endpoint. Three threads: TUN->UDP, UDP->TUN, timers/stats.

use super::{net, wfp};
use crate::log;
use crate::tunnel::{ConnectParams, Status, Tunnel};
use crate::wgconf::WgConfig;
use neptun::noise::{Tunn, TunnResult};
use neptun::x25519::{PublicKey, StaticSecret};
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr, UdpSocket};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::Sender;
use std::sync::{Arc, Mutex, OnceLock};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

const ADAPTER_NAME: &str = "gvpndows";
// Fixed GUID so Windows reuses one network profile instead of creating "Network 2, 3, ...".
const ADAPTER_GUID: u128 = 0x6776706e_646f_7773_8000_74756e6e656c;
const WINTUN_DLL: &[u8] = include_bytes!(concat!(env!("CARGO_MANIFEST_DIR"), "/vendor/wintun/bin/amd64/wintun.dll"));
const BUF: usize = 65_536 + 256;

static WINTUN: OnceLock<Result<wintun::Wintun, String>> = OnceLock::new();

fn wintun() -> Result<wintun::Wintun, String> {
    WINTUN
        .get_or_init(|| {
            let path = log::dir().join("wintun.dll");
            if std::fs::read(&path).ok().as_deref() != Some(WINTUN_DLL) {
                std::fs::write(&path, WINTUN_DLL).map_err(|e| format!("cannot write {}: {e}", path.display()))?;
            }
            let w = unsafe { wintun::load_from_path(&path) }.map_err(|e| format!("loading wintun.dll: {e}"))?;
            match wintun::get_running_driver_version(&w) {
                Ok(v) => log::line(format!("wintun driver running, version {v:?}")),
                Err(_) => log::line("wintun driver not loaded yet (it loads with the first adapter)"),
            }
            Ok(w)
        })
        .clone()
}

struct Running {
    stop: Arc<AtomicBool>,
    session: Arc<wintun::Session>,
    threads: Vec<JoinHandle<()>>,
    routes: Vec<net::Route>,
    killswitch: Option<wfp::KillSwitch>,
    _adapter: Arc<wintun::Adapter>,
}

pub struct WgTunnel {
    events: Sender<Status>,
    running: Arc<Mutex<Option<Running>>>,
}

impl WgTunnel {
    pub fn new(events: Sender<Status>) -> Self {
        WgTunnel { events, running: Arc::new(Mutex::new(None)) }
    }
}

impl Tunnel for WgTunnel {
    fn name(&self) -> &'static str {
        "WireGuard (Wintun + neptun)"
    }

    fn connect(&mut self, p: ConnectParams) {
        let (events, running) = (self.events.clone(), self.running.clone());
        std::thread::spawn(move || {
            let _ = events.send(Status::Connecting);
            let mut slot = running.lock().unwrap();
            if slot.is_some() {
                return;
            }
            let mut cleanup = Cleanup::default();
            match up(&p, &events, &mut cleanup) {
                Ok(r) => *slot = Some(r),
                Err(e) => {
                    log::line(format!("connect failed: {e}"));
                    cleanup.undo();
                    let _ = events.send(Status::Failed(e));
                }
            }
        });
    }

    fn shutdown(&mut self) {
        if let Some(r) = self.running.lock().unwrap().take() {
            log::line("app closing: tearing the tunnel down");
            down(r);
        }
    }

    fn disconnect(&mut self) {
        let (events, running) = (self.events.clone(), self.running.clone());
        std::thread::spawn(move || {
            let _ = events.send(Status::Disconnecting);
            if let Some(r) = running.lock().unwrap().take() {
                down(r);
            }
            let _ = events.send(Status::Disconnected);
        });
    }
}

/// What `up` changed so far, undone if a later step fails.
#[derive(Default)]
struct Cleanup {
    routes: Vec<net::Route>,
}

impl Cleanup {
    fn undo(&mut self) {
        for r in self.routes.drain(..).rev() {
            if let Err(e) = net::delete_route(&r) {
                log::line(format!("cleanup: {e}"));
            }
        }
    }
}

fn up(p: &ConnectParams, events: &Sender<Status>, cleanup: &mut Cleanup) -> Result<Running, String> {
    let t0 = Instant::now();
    let cfg: &WgConfig = &p.config;
    let private = match cfg.private_key {
        Some(k) => k,
        None => crate::wgconf::own_private_key()?,
    };
    let endpoint = cfg.resolve_endpoint()?;
    let SocketAddr::V4(endpoint_v4) = endpoint else { unreachable!() };
    log::line(format!("endpoint {} = {endpoint}", cfg.endpoint));

    // 1. Remember how the endpoint is reached now, before our routes take over.
    let (phys_luid, phys_gw) = net::best_route(endpoint.ip())?;
    log::line(format!("physical route to endpoint: next hop {phys_gw}, if luid {phys_luid:#x}"));

    // 2. Wintun adapter + session.
    let w = wintun()?;
    let adapter = match wintun::Adapter::create(&w, ADAPTER_NAME, "GnosisVPN", Some(ADAPTER_GUID)) {
        Ok(a) => a,
        Err(e) => {
            log::line(format!("adapter create failed ({e}); trying to open an existing one"));
            wintun::Adapter::open(&w, ADAPTER_NAME).map_err(|e| format!("Wintun adapter: {e}"))?
        }
    };
    let tun_luid = unsafe { adapter.get_luid().Value };
    log::line(format!("wintun adapter up: luid {tun_luid:#x}, index {:?}", adapter.get_adapter_index()));
    let session = Arc::new(adapter.start_session(wintun::MAX_RING_CAPACITY).map_err(|e| format!("Wintun session: {e}"))?);

    // 3. Address, MTU, metric, DNS.
    for a in &cfg.addresses {
        net::add_address(tun_luid, a.addr, a.len)?;
        log::line(format!("address {a}"));
    }
    net::set_interface(tun_luid, false, cfg.mtu as u32, 5)?;
    if let Err(e) = net::set_interface(tun_luid, true, cfg.mtu.max(1280) as u32, 5) {
        log::line(format!("IPv6 interface settings: {e} (continuing)"));
    }
    if !cfg.dns.is_empty() {
        adapter.set_dns_servers(&cfg.dns).map_err(|e| format!("setting DNS: {e}"))?;
        log::line(format!("DNS {:?} on the tunnel interface", cfg.dns));
    }

    // 4. Kill switch before the routes, so nothing slips out while they change.
    let killswitch = if p.killswitch {
        let k = wfp::KillSwitch::enable(tun_luid, endpoint_v4)?;
        log::line("kill switch on (WFP: only tunnel, loopback, DHCP and the endpoint are allowed)");
        Some(k)
    } else {
        None
    };

    // 5. Routes: endpoint via the physical gateway, AllowedIPs into the tunnel
    //    (0.0.0.0/0 and ::/0 as two halves, so the system default route stays untouched).
    let bypass = net::Route { if_luid: phys_luid, dest: endpoint.ip(), prefix_len: 32, next_hop: phys_gw };
    if net::add_route(&bypass)? {
        cleanup.routes.push(bypass.clone());
    }
    log::line(format!("route {bypass} (endpoint outside the tunnel)"));
    for a in &cfg.allowed_ips {
        let halves: Vec<(IpAddr, u8)> = match (a.addr, a.len) {
            (IpAddr::V4(_), 0) => vec![(Ipv4Addr::UNSPECIFIED.into(), 1), (Ipv4Addr::new(128, 0, 0, 0).into(), 1)],
            (IpAddr::V6(_), 0) => vec![(Ipv6Addr::UNSPECIFIED.into(), 1), (Ipv6Addr::new(0x8000, 0, 0, 0, 0, 0, 0, 0).into(), 1)],
            (ip, len) => vec![(ip, len)],
        };
        for (dest, len) in halves {
            let next_hop = if dest.is_ipv4() { Ipv4Addr::UNSPECIFIED.into() } else { Ipv6Addr::UNSPECIFIED.into() };
            let r = net::Route { if_luid: tun_luid, dest, prefix_len: len, next_hop };
            match net::add_route(&r) {
                Ok(_) => log::line(format!("route {r}")),
                // IPv6 without a tunnel IPv6 address may be refused: then v6 is simply not routed here.
                Err(e) if dest.is_ipv6() => log::line(format!("{e} (continuing; IPv6 is blocked only with the kill switch)")),
                Err(e) => return Err(e),
            }
        }
    }

    // 6. WireGuard over UDP.
    let sock = UdpSocket::bind("0.0.0.0:0").map_err(|e| format!("UDP socket: {e}"))?;
    sock.connect(endpoint).map_err(|e| format!("UDP connect: {e}"))?;
    sock.set_read_timeout(Some(Duration::from_millis(250))).ok();
    let index = {
        let mut b = [0u8; 4];
        getrandom::getrandom(&mut b).map_err(|e| e.to_string())?;
        u32::from_ne_bytes(b) & 0x00ff_ffff
    };
    let tunn = Tunn::new(StaticSecret::from(private), PublicKey::from(cfg.peer_public_key), None, Some(cfg.keepalive), index, None)
        .map_err(|e| format!("WireGuard: {e}"))?;
    let tunn = Arc::new(Mutex::new(tunn));
    let sock = Arc::new(sock);
    let stop = Arc::new(AtomicBool::new(false));
    {
        let mut buf = vec![0u8; BUF];
        if let TunnResult::WriteToNetwork(b) = tunn.lock().unwrap().format_handshake_initiation(&mut buf, false) {
            let n = sock.send(b).map_err(|e| format!("sending handshake: {e}"))?;
            log::line(format!(
                "handshake initiation sent ({n} bytes from {} to {endpoint})",
                sock.local_addr().map(|a| a.to_string()).unwrap_or_default()
            ));
        }
    }

    let mut threads = Vec::new();
    // TUN -> UDP
    {
        let (session, tunn, sock, stop) = (session.clone(), tunn.clone(), sock.clone(), stop.clone());
        threads.push(std::thread::spawn(move || {
            let mut buf = vec![0u8; BUF];
            let mut last_err = Instant::now() - Duration::from_secs(60);
            while !stop.load(Ordering::Relaxed) {
                let pkt = match session.receive_blocking() {
                    Ok(p) => p,
                    Err(_) => break, // session shut down
                };
                if let TunnResult::WriteToNetwork(b) = tunn.lock().unwrap().encapsulate(pkt.bytes(), &mut buf) {
                    if let Err(e) = sock.send(b) {
                        if last_err.elapsed() > Duration::from_secs(10) {
                            log::line(format!("tun->udp: send failed: {e}"));
                            last_err = Instant::now();
                        }
                    }
                }
            }
        }));
    }
    // UDP -> TUN
    {
        let (session, tunn, sock, stop) = (session.clone(), tunn.clone(), sock.clone(), stop.clone());
        threads.push(std::thread::spawn(move || {
            let (mut rx, mut out) = (vec![0u8; BUF], vec![0u8; BUF]);
            while !stop.load(Ordering::Relaxed) {
                let n = match sock.recv(&mut rx) {
                    Ok(n) => n,
                    Err(e) if matches!(e.kind(), std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut) => continue,
                    Err(e) => {
                        // ICMP port unreachable shows up as ConnectionReset on Windows UDP sockets.
                        log::line(format!("udp recv: {e}"));
                        std::thread::sleep(Duration::from_millis(200));
                        continue;
                    }
                };
                let mut t = tunn.lock().unwrap();
                let mut datagram: &[u8] = &rx[..n];
                loop {
                    match t.decapsulate(None, datagram, &mut out) {
                        TunnResult::WriteToNetwork(b) => {
                            let _ = sock.send(b);
                            datagram = &[]; // flush packets queued during the handshake
                        }
                        TunnResult::WriteToTunnel(b, _src) => {
                            if !b.is_empty() {
                                if let Ok(mut p) = session.allocate_send_packet(b.len() as u16) {
                                    p.bytes_mut().copy_from_slice(b);
                                    session.send_packet(p);
                                }
                            }
                            break;
                        }
                        _ => break,
                    }
                }
            }
        }));
    }
    // Timers + status
    {
        let (tunn, sock, stop, events) = (tunn.clone(), sock.clone(), stop.clone(), events.clone());
        let started = Instant::now();
        threads.push(std::thread::spawn(move || {
            let mut buf = vec![0u8; BUF];
            let (mut connected, mut last_log) = (false, Instant::now());
            let mut last_err = Instant::now() - Duration::from_secs(60);
            let mut timer_sends = 0u64;
            while !stop.load(Ordering::Relaxed) {
                std::thread::sleep(Duration::from_millis(250));
                let mut t = tunn.lock().unwrap();
                match t.update_timers(&mut buf) {
                    TunnResult::WriteToNetwork(b) => {
                        let len = b.len();
                        let res = sock.send(b);
                        timer_sends += 1;
                        if !connected || res.is_err() {
                            match res {
                                // 148 bytes = handshake initiation; log each retry until connected
                                Ok(_) => log::line(format!("timer: sent {len} bytes (#{timer_sends}, handshake retry or keepalive)")),
                                Err(e) => log::line(format!("timer: UDP send failed: {e}")),
                            }
                        }
                    }
                    TunnResult::Err(e) => {
                        if last_err.elapsed() > Duration::from_secs(10) {
                            log::line(format!("wireguard timer: {e:?}"));
                            last_err = Instant::now();
                        }
                    }
                    _ => {}
                }
                let (hs, tx, rx, _loss, _rtt) = t.stats();
                drop(t);
                if let Some(hs) = hs {
                    if !connected {
                        connected = true;
                        log::line(format!("handshake complete after {:.1} s", started.elapsed().as_secs_f32()));
                    }
                    let _ = events.send(Status::Connected {
                        detail: format!("handshake {} s ago, sent {} KB, received {} KB", hs.as_secs(), tx / 1024, rx / 1024),
                    });
                } else if !connected && started.elapsed() > Duration::from_secs(10) && last_log.elapsed() > Duration::from_secs(10) {
                    log::line("no handshake yet: is the peer's public key registered on the server, and is UDP to the endpoint open?");
                    last_log = Instant::now();
                }
                if connected && last_log.elapsed() > Duration::from_secs(30) {
                    log::line(format!("stats: sent {tx} B, received {rx} B, last handshake {:?} ago", hs));
                    last_log = Instant::now();
                }
            }
        }));
    }

    log::line(format!("tunnel set up in {:.1} s; waiting for the handshake", t0.elapsed().as_secs_f32()));
    let routes = std::mem::take(&mut cleanup.routes);
    Ok(Running { stop, session, threads, routes, killswitch, _adapter: adapter })
}

fn down(r: Running) {
    let t0 = Instant::now();
    r.stop.store(true, Ordering::Relaxed);
    let _ = r.session.shutdown();
    for t in r.threads {
        let _ = t.join();
    }
    for route in r.routes.iter().rev() {
        match net::delete_route(route) {
            Ok(()) => log::line(format!("removed route {route}")),
            Err(e) => log::line(format!("disconnect: {e}")),
        }
    }
    drop(r.session);
    // Dropping the last adapter reference closes it, and Wintun deletes the adapter with its
    // addresses, routes and DNS settings. Then the kill switch goes (after the tunnel is gone).
    drop(r._adapter);
    if r.killswitch.is_some() {
        drop(r.killswitch);
        log::line("kill switch off");
    }
    log::line(format!("disconnected in {:.1} s", t0.elapsed().as_secs_f32()));
}
