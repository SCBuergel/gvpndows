//! R3-2: a SetupTunnel that stopped part-way must not leave the next one skipping the missing routes.
use gvpndows_pure_tests::{missing_tunnel_routes, TUNNEL_ROUTES};

const LUID: u64 = 7;

#[test]
fn all_routes_missing_at_first_setup() {
    assert_eq!(missing_tunnel_routes(&[], LUID), TUNNEL_ROUTES.to_vec());
}

#[test]
fn partial_setup_adds_only_the_rest() {
    let first = [(LUID, TUNNEL_ROUTES[0].0, TUNNEL_ROUTES[0].1)];
    assert_eq!(missing_tunnel_routes(&first, LUID), TUNNEL_ROUTES[1..].to_vec());
}

#[test]
fn complete_setup_adds_nothing() {
    let all: Vec<_> = TUNNEL_ROUTES.iter().map(|&(d, l)| (LUID, d, l)).collect();
    assert!(missing_tunnel_routes(&all, LUID).is_empty());
}

#[test]
fn routes_on_another_adapter_do_not_count() {
    let old: Vec<_> = TUNNEL_ROUTES.iter().map(|&(d, l)| (LUID + 1, d, l)).collect();
    assert_eq!(missing_tunnel_routes(&old, LUID), TUNNEL_ROUTES.to_vec());
}
