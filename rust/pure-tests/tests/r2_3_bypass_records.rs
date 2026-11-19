//! R2-3: the bypass routes the service records are read back exactly, and nothing it cannot have
//! added (a default route, a foreign prefix, an on-link route, a malformed line) is ever swept.

use gvpndows_pure_tests::{bypass_record_line, parse_bypass_records, BypassRec, BYPASS_LANS};
use std::net::{IpAddr, Ipv4Addr};

fn v4(a: u8, b: u8, c: u8, d: u8) -> IpAddr {
    IpAddr::V4(Ipv4Addr::new(a, b, c, d))
}

#[test]
fn records_round_trip() {
    let gw = v4(192, 168, 1, 1);
    let mut recs: Vec<BypassRec> = vec![(0x1234_5678_9abc, v4(34, 1, 2, 3), 32, gw)];
    recs.extend(BYPASS_LANS.iter().map(|&(d, l)| (0x8000_0000_0000_0001u64, IpAddr::V4(d), l, gw)));
    let text: String = recs.iter().map(bypass_record_line).collect();
    assert_eq!(parse_bypass_records(&text), recs);
}

#[test]
fn only_routes_the_service_adds_are_read() {
    let text = "0x1 0.0.0.0/0 192.168.1.1\n\
                0x1 10.0.0.0/9 192.168.1.1\n\
                0x1 34.1.2.3/32 0.0.0.0\n\
                0x1 2001:db8::1/128 fe80::1\n\
                1 34.1.2.3/32 192.168.1.1\n\
                0x1 34.1.2.3/32 192.168.1.1 extra\n\
                garbage\n\
                0x2 34.1.2.4/32 10.0.0.1\n";
    assert_eq!(parse_bypass_records(text), vec![(2, v4(34, 1, 2, 4), 32, v4(10, 0, 0, 1))]);
}
