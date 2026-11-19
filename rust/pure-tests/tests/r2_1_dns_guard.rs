//! R2-1: with only the tunnel adapter's DNS servers set, Windows still fell back to the WAN
//! adapter's DNS servers outside the tunnel. While the routes are up, a WFP DNS guard must allow
//! port 53 only through the tunnel (and loopback), and go when the routes go.
use gvpndows_pure_tests::tunnel_dns_servers;
use std::net::IpAddr;
use std::path::Path;

#[test]
fn tunnel_dns_servers_parse_the_cores_list() {
    let ip = |s: &str| s.parse::<IpAddr>().unwrap();
    assert_eq!(tunnel_dns_servers(Some("10.128.0.1, 1.1.1.1")), vec![ip("10.128.0.1"), ip("1.1.1.1")]);
    assert_eq!(tunnel_dns_servers(Some("fd00::1,bogus")), vec![ip("fd00::1")]);
    assert!(tunnel_dns_servers(Some("")).is_empty());
    assert!(tunnel_dns_servers(None).is_empty());
}

fn body<'a>(src: &'a str, f: &str) -> &'a str {
    let s = &src[src.find(&format!("pub fn {f}(")).unwrap()..];
    &s[..s.find("\n    }\n").unwrap()]
}

#[test]
fn dns_guard_follows_the_tunnel_routes() {
    let src = std::fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../src/gnosis/root.rs")).unwrap();
    assert!(body(&src, "setup_tunnel").contains("wfp::KillSwitch::dns_guard(tun_luid)?"), "setup_tunnel must enable the guard or fail");
    assert!(body(&src, "teardown_routes").contains("self.dns_guard.take()"), "teardown_routes must remove the guard");
    assert!(body(&src, "shutdown").contains("self.teardown_routes()"));
    let wfp = std::fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../src/win/wfp.rs")).unwrap();
    let guard = body(&wfp, "dns_guard");
    for f in ["FWPM_LAYER_ALE_AUTH_CONNECT_V4", "FWPM_LAYER_ALE_AUTH_CONNECT_V6", "Cond::LocalInterface(tunnel_luid)", "Cond::Loopback", "\"block DNS outside the tunnel\", 0, false, &[Cond::RemotePort(53)]"] {
        assert!(guard.contains(f), "DNS guard lacks {f}");
    }
    assert!(wfp.contains("session.flags = FWPM_SESSION_FLAG_DYNAMIC"), "filters must die with the process");
}
