//! R2-2: the WAN comes from the default routes, so a network switch is seen even though our own
//! routes capture every lookup towards Blokli and the peers.
use gvpndows_pure_tests::pick_wan;
use std::net::{IpAddr, Ipv4Addr};

const TUN: u64 = 1;
fn gw(last: u8) -> IpAddr {
    IpAddr::V4(Ipv4Addr::new(192, 168, 1, last))
}

#[test]
fn lowest_metric_default_route_wins() {
    let d = [(2, gw(1), 50), (3, gw(2), 25)];
    assert_eq!(pick_wan(&d, TUN), Some((3, gw(2))));
}

#[test]
fn never_the_tunnel() {
    let d = [(TUN, gw(9), 0), (2, gw(1), 50)];
    assert_eq!(pick_wan(&d, TUN), Some((2, gw(1))));
    assert_eq!(pick_wan(&d[..1], TUN), None);
}

#[test]
fn a_switch_changes_the_wan() {
    let wifi_a = [(2, gw(1), 35)];
    let ethernet = [(2, gw(1), 35), (4, gw(254), 10)];
    assert_ne!(pick_wan(&wifi_a, TUN), pick_wan(&ethernet, TUN));
    assert_eq!(pick_wan(&[], TUN), None);
}
