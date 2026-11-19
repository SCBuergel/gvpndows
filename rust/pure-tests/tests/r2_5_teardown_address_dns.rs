//! R2-5: after a disconnect the kept GnosisVPN adapter kept the tunnel's address (on-link
//! 10.128.0.0/9) and DNS servers at metric 5, so Windows kept using them with the VPN off.
use std::path::Path;

fn body<'a>(src: &'a str, f: &str) -> &'a str {
    let s = &src[src.find(&format!("pub fn {f}(")).unwrap()..];
    &s[..s.find("\n    }\n").unwrap()]
}

#[test]
fn teardown_clears_the_tunnel_address_and_dns() {
    let src = std::fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../src/gnosis/root.rs")).unwrap();
    let td = body(&src, "teardown_routes");
    for n in ["a.address.take()", "net::delete_address(a.luid, ip, len)", "a._adapter.set_dns_servers(&[])"] {
        assert!(td.contains(n), "teardown_routes lacks {n}");
    }
    assert!(!td.contains("set_dns_servers(&[]).map_err") && !td.contains("set_dns_servers(&[])?"), "a failure to clear is logged, not fatal");
    // with no recorded address, the next setup adds it again
    let setup = body(&src, "setup_tunnel");
    assert!(setup.contains("if adapter.address != Some((ip, len))") && setup.contains("net::add_address(tun_luid, ip, len)?"));
    assert!(body(&src, "shutdown").contains("self.teardown_routes()"));
}
