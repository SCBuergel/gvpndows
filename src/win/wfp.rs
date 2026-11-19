//! Kill switch with the Windows Filtering Platform: block everything except the tunnel interface,
//! loopback, DHCP and the WireGuard endpoint; and the DNS guard: DNS (remote port 53) only through
//! the tunnel interface or loopback. The filters live in a *dynamic* WFP session, so Windows removes
//! them when the engine handle closes, including when this process crashes.

use std::net::SocketAddrV4;
use windows_sys::core::GUID;
use windows_sys::Win32::Foundation::HANDLE;
use windows_sys::Win32::NetworkManagement::WindowsFilteringPlatform::*;

// Our own sublayer key (random, fixed so the filters are easy to recognise with `netsh wfp show state`).
const SUBLAYER: GUID = GUID::from_u128(0x6776706e_646f_7773_8000_6b696c6c7377);
const DNS_SUBLAYER: GUID = GUID::from_u128(0x6776706e_646f_7773_8000_646e73677264);

pub struct KillSwitch {
    engine: HANDLE,
    sublayer: GUID,
}

// The engine handle is only used from the thread that owns the KillSwitch at the time.
unsafe impl Send for KillSwitch {}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn check(what: &str, rc: u32) -> Result<(), String> {
    if rc == 0 {
        Ok(())
    } else {
        Err(format!("WFP {what} failed: 0x{rc:08x}"))
    }
}

/// One filter condition; the values it points to live in `Values` until the filter is added.
enum Cond {
    LocalInterface(u64),
    Loopback,
    Protocol(u8),
    RemoteAddrV4(u32),
    RemotePort(u16),
    LocalPort(u16),
}

impl KillSwitch {
    pub fn enable(tunnel_luid: u64, endpoint: SocketAddrV4) -> Result<Self, String> {
        unsafe { Self::open("gvpndows kill switch", SUBLAYER, |ks| ks.add_all(tunnel_luid, endpoint)) }
    }

    /// The DNS guard (the Windows analogue of upstream's resolvectl `~.`): blocks remote port 53
    /// (UDP and TCP, IPv4 and IPv6) except through the tunnel interface and loopback, so the DNS
    /// Client cannot fall back to the WAN adapter's servers.
    pub fn dns_guard(tunnel_luid: u64) -> Result<Self, String> {
        unsafe {
            Self::open("gvpndows DNS guard", DNS_SUBLAYER, |ks| {
                for layer in [&FWPM_LAYER_ALE_AUTH_CONNECT_V4, &FWPM_LAYER_ALE_AUTH_CONNECT_V6] {
                    ks.add(layer, "permit DNS in the tunnel", 15, true, &[Cond::RemotePort(53), Cond::LocalInterface(tunnel_luid)])?;
                    ks.add(layer, "permit DNS on loopback", 15, true, &[Cond::RemotePort(53), Cond::Loopback])?;
                    ks.add(layer, "block DNS outside the tunnel", 0, false, &[Cond::RemotePort(53)])?;
                }
                Ok(())
            })
        }
    }

    /// Opens a dynamic session and adds a sublayer and `fill`'s filters in one transaction.
    unsafe fn open(name: &str, sublayer: GUID, fill: impl FnOnce(&Self) -> Result<(), String>) -> Result<Self, String> {
        let mut session: FWPM_SESSION0 = std::mem::zeroed();
        let mut wname = wide(name);
        session.displayData.name = wname.as_mut_ptr();
        session.flags = FWPM_SESSION_FLAG_DYNAMIC;
        let mut engine: HANDLE = std::ptr::null_mut();
        check(
            "engine open",
            FwpmEngineOpen0(std::ptr::null(), 10 /* RPC_C_AUTHN_WINNT */, std::ptr::null(), &session, &mut engine),
        )?;
        let ks = KillSwitch { engine, sublayer };
        check("transaction begin", FwpmTransactionBegin0(engine, 0))?;
        let mut sl: FWPM_SUBLAYER0 = std::mem::zeroed();
        sl.subLayerKey = sublayer;
        sl.displayData.name = wname.as_mut_ptr();
        sl.weight = 0xFFFF;
        if let Err(e) = check("sublayer add", FwpmSubLayerAdd0(engine, &sl, std::ptr::null_mut())).and_then(|_| fill(&ks)) {
            FwpmTransactionAbort0(engine);
            return Err(e); // Drop closes the engine
        }
        check("transaction commit", FwpmTransactionCommit0(engine))?;
        Ok(ks)
    }

    unsafe fn add_all(&self, tunnel_luid: u64, endpoint: SocketAddrV4) -> Result<(), String> {
        let ep_ip = u32::from(*endpoint.ip()); // WFP wants IPv4 addresses in host byte order
        let layers_v4 = [FWPM_LAYER_ALE_AUTH_CONNECT_V4, FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4];
        let layers_v6 = [FWPM_LAYER_ALE_AUTH_CONNECT_V6, FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6];
        for (i, layer) in layers_v4.iter().chain(layers_v6.iter()).enumerate() {
            let v4 = i < 2;
            self.add(layer, "permit tunnel interface", 15, true, &[Cond::LocalInterface(tunnel_luid)])?;
            self.add(layer, "permit loopback", 15, true, &[Cond::Loopback])?;
            if v4 {
                self.add(
                    layer,
                    "permit WireGuard endpoint",
                    14,
                    true,
                    &[Cond::Protocol(17), Cond::RemoteAddrV4(ep_ip), Cond::RemotePort(endpoint.port())],
                )?;
                self.add(layer, "permit DHCP", 14, true, &[Cond::Protocol(17), Cond::LocalPort(68), Cond::RemotePort(67)])?;
            } else {
                self.add(layer, "permit DHCPv6", 14, true, &[Cond::Protocol(17), Cond::LocalPort(546), Cond::RemotePort(547)])?;
            }
            self.add(layer, "block everything else", 0, false, &[])?;
        }
        Ok(())
    }

    unsafe fn add(&self, layer: &GUID, what: &str, weight: u8, permit: bool, conds: &[Cond]) -> Result<(), String> {
        // Backing storage for values that conditions point to.
        let mut u64s: Vec<u64> = conds
            .iter()
            .map(|c| if let Cond::LocalInterface(l) = c { *l } else { 0 })
            .collect();
        let mut fc: Vec<FWPM_FILTER_CONDITION0> = Vec::with_capacity(conds.len());
        for (i, c) in conds.iter().enumerate() {
            let mut x: FWPM_FILTER_CONDITION0 = std::mem::zeroed();
            x.matchType = FWP_MATCH_EQUAL;
            match c {
                Cond::LocalInterface(_) => {
                    x.fieldKey = FWPM_CONDITION_IP_LOCAL_INTERFACE;
                    x.conditionValue.r#type = FWP_UINT64;
                    x.conditionValue.Anonymous.uint64 = &mut u64s[i];
                }
                Cond::Loopback => {
                    x.fieldKey = FWPM_CONDITION_FLAGS;
                    x.matchType = FWP_MATCH_FLAGS_ALL_SET;
                    x.conditionValue.r#type = FWP_UINT32;
                    x.conditionValue.Anonymous.uint32 = FWP_CONDITION_FLAG_IS_LOOPBACK;
                }
                Cond::Protocol(p) => {
                    x.fieldKey = FWPM_CONDITION_IP_PROTOCOL;
                    x.conditionValue.r#type = FWP_UINT8;
                    x.conditionValue.Anonymous.uint8 = *p;
                }
                Cond::RemoteAddrV4(a) => {
                    x.fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS;
                    x.conditionValue.r#type = FWP_UINT32;
                    x.conditionValue.Anonymous.uint32 = *a;
                }
                Cond::RemotePort(p) => {
                    x.fieldKey = FWPM_CONDITION_IP_REMOTE_PORT;
                    x.conditionValue.r#type = FWP_UINT16;
                    x.conditionValue.Anonymous.uint16 = *p;
                }
                Cond::LocalPort(p) => {
                    x.fieldKey = FWPM_CONDITION_IP_LOCAL_PORT;
                    x.conditionValue.r#type = FWP_UINT16;
                    x.conditionValue.Anonymous.uint16 = *p;
                }
            }
            fc.push(x);
        }
        let mut name = wide(&format!("gvpndows: {what}"));
        let mut f: FWPM_FILTER0 = std::mem::zeroed();
        f.displayData.name = name.as_mut_ptr();
        f.layerKey = *layer;
        f.subLayerKey = self.sublayer;
        f.weight.r#type = FWP_UINT8;
        f.weight.Anonymous.uint8 = weight;
        f.numFilterConditions = fc.len() as u32;
        f.filterCondition = if fc.is_empty() { std::ptr::null_mut() } else { fc.as_mut_ptr() };
        f.action.r#type = if permit { FWP_ACTION_PERMIT } else { FWP_ACTION_BLOCK };
        let mut id = 0u64;
        check(&format!("filter add ({what})"), FwpmFilterAdd0(self.engine, &f, std::ptr::null_mut(), &mut id))
    }
}

impl Drop for KillSwitch {
    fn drop(&mut self) {
        // Closing the dynamic session removes every filter and the sublayer.
        unsafe {
            FwpmEngineClose0(self.engine);
        }
    }
}
