//! Interface address, MTU/metric and routes through the IP Helper API (no netsh).

use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use windows_sys::Win32::NetworkManagement::IpHelper::{
    CreateIpForwardEntry2, CreateUnicastIpAddressEntry, DeleteIpForwardEntry2, DeleteUnicastIpAddressEntry, GetBestRoute2, GetIpInterfaceEntry,
    InitializeIpForwardEntry, InitializeIpInterfaceEntry, InitializeUnicastIpAddressEntry, SetIpInterfaceEntry,
    MIB_IPFORWARD_ROW2, MIB_IPINTERFACE_ROW, MIB_UNICASTIPADDRESS_ROW,
};
use windows_sys::Win32::NetworkManagement::Ndis::NET_LUID_LH;
use windows_sys::Win32::Networking::WinSock::{AF_INET, AF_INET6, SOCKADDR_INET};

const ERROR_OBJECT_ALREADY_EXISTS: u32 = 5010;
const ERROR_NOT_FOUND: u32 = 1168;

pub fn luid(value: u64) -> NET_LUID_LH {
    NET_LUID_LH { Value: value }
}

fn sockaddr(ip: IpAddr) -> SOCKADDR_INET {
    let mut sa: SOCKADDR_INET = unsafe { std::mem::zeroed() };
    match ip {
        IpAddr::V4(v4) => {
            sa.Ipv4.sin_family = AF_INET;
            sa.Ipv4.sin_addr.S_un.S_addr = u32::from_ne_bytes(v4.octets());
        }
        IpAddr::V6(v6) => {
            sa.Ipv6.sin6_family = AF_INET6;
            sa.Ipv6.sin6_addr.u.Byte = v6.octets();
        }
    }
    sa
}

fn ip_of(sa: &SOCKADDR_INET) -> IpAddr {
    unsafe {
        if sa.si_family == AF_INET6 {
            IpAddr::V6(Ipv6Addr::from(sa.Ipv6.sin6_addr.u.Byte))
        } else {
            IpAddr::V4(Ipv4Addr::from(sa.Ipv4.sin_addr.S_un.S_addr.to_ne_bytes()))
        }
    }
}

fn check(what: &str, rc: u32) -> Result<(), String> {
    if rc == 0 {
        Ok(())
    } else {
        Err(format!("{what} failed: {}", std::io::Error::from_raw_os_error(rc as i32)))
    }
}

pub fn add_address(if_luid: u64, addr: IpAddr, prefix_len: u8) -> Result<(), String> {
    unsafe {
        let mut row: MIB_UNICASTIPADDRESS_ROW = std::mem::zeroed();
        InitializeUnicastIpAddressEntry(&mut row);
        row.InterfaceLuid = luid(if_luid);
        row.Address = sockaddr(addr);
        row.OnLinkPrefixLength = prefix_len;
        row.DadState = 4 as _; // IpDadStatePreferred: usable at once, no duplicate address detection wait
        let rc = CreateUnicastIpAddressEntry(&row);
        if rc == ERROR_OBJECT_ALREADY_EXISTS {
            return Ok(());
        }
        check(&format!("adding address {addr}/{prefix_len}"), rc)
    }
}

pub fn delete_address(if_luid: u64, addr: IpAddr, prefix_len: u8) -> Result<(), String> {
    unsafe {
        let mut row: MIB_UNICASTIPADDRESS_ROW = std::mem::zeroed();
        InitializeUnicastIpAddressEntry(&mut row);
        row.InterfaceLuid = luid(if_luid);
        row.Address = sockaddr(addr);
        row.OnLinkPrefixLength = prefix_len;
        let rc = DeleteUnicastIpAddressEntry(&row);
        if rc == ERROR_NOT_FOUND {
            return Ok(());
        }
        check(&format!("deleting address {addr}/{prefix_len}"), rc)
    }
}

/// Sets MTU and a low fixed metric (preferred interface, also for DNS ordering) for one family.
pub fn set_interface(if_luid: u64, v6: bool, mtu: u32, metric: u32) -> Result<(), String> {
    unsafe {
        let mut row: MIB_IPINTERFACE_ROW = std::mem::zeroed();
        InitializeIpInterfaceEntry(&mut row);
        row.Family = if v6 { AF_INET6 } else { AF_INET };
        row.InterfaceLuid = luid(if_luid);
        check("reading interface settings", GetIpInterfaceEntry(&mut row))?;
        row.NlMtu = mtu;
        row.UseAutomaticMetric = 0;
        row.Metric = metric;
        if !v6 {
            row.SitePrefixLength = 0; // must be 0 for IPv4 or SetIpInterfaceEntry fails
        }
        check("setting MTU/metric", SetIpInterfaceEntry(&mut row))
    }
}

#[derive(Clone, Debug)]
pub struct Route {
    pub if_luid: u64,
    pub dest: IpAddr,
    pub prefix_len: u8,
    pub next_hop: IpAddr,
}

impl std::fmt::Display for Route {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}/{} via {} (if luid {:#x})", self.dest, self.prefix_len, self.next_hop, self.if_luid)
    }
}

fn route_row(r: &Route) -> MIB_IPFORWARD_ROW2 {
    unsafe {
        let mut row: MIB_IPFORWARD_ROW2 = std::mem::zeroed();
        InitializeIpForwardEntry(&mut row);
        row.InterfaceLuid = luid(r.if_luid);
        row.DestinationPrefix.Prefix = sockaddr(r.dest);
        row.DestinationPrefix.PrefixLength = r.prefix_len;
        row.NextHop = sockaddr(r.next_hop);
        row.Metric = 0;
        row.Protocol = 3 as _; // MIB_IPPROTO_NETMGMT (static)
        row
    }
}

/// Adds a route. Returns Ok(false) if an identical route already existed (then it is not ours to delete).
pub fn add_route(r: &Route) -> Result<bool, String> {
    let rc = unsafe { CreateIpForwardEntry2(&route_row(r)) };
    if rc == ERROR_OBJECT_ALREADY_EXISTS {
        return Ok(false);
    }
    check(&format!("adding route {r}"), rc).map(|_| true)
}

pub fn delete_route(r: &Route) -> Result<(), String> {
    let rc = unsafe { DeleteIpForwardEntry2(&route_row(r)) };
    if rc == ERROR_NOT_FOUND {
        return Ok(());
    }
    check(&format!("deleting route {r}"), rc)
}

/// The route Windows would use for `dest` right now: (interface LUID, next hop).
pub fn best_route(dest: IpAddr) -> Result<(u64, IpAddr), String> {
    unsafe {
        let d = sockaddr(dest);
        let mut row: MIB_IPFORWARD_ROW2 = std::mem::zeroed();
        let mut src: SOCKADDR_INET = std::mem::zeroed();
        check(
            &format!("finding the route to {dest}"),
            GetBestRoute2(std::ptr::null(), 0, std::ptr::null(), &d, 0, &mut row, &mut src),
        )?;
        Ok((row.InterfaceLuid.Value, ip_of(&row.NextHop)))
    }
}
