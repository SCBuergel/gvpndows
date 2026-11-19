//! R2-4: a peer IP the core stops listing loses its bypass route after 300 s; Blokli and the relay
//! never do; a failed route add is retried on the next update.

use gvpndows_pure_tests::{reconcile_peers, PEER_IP_HYSTERESIS_MS as H};
use std::collections::{BTreeMap, BTreeSet};
use std::net::Ipv4Addr;

fn ip(n: u8) -> Ipv4Addr {
    Ipv4Addr::new(198, 51, 100, n)
}

#[test]
fn peers_expire_after_the_hysteresis_and_always_kept_stay() {
    let (blokli, a, b) = (ip(1), ip(2), ip(3));
    let mut seen = BTreeMap::new();
    let mut bypassed = BTreeSet::new();
    let (add, remove) = reconcile_peers(&mut seen, &[a, b], &[blokli], &bypassed, 0);
    assert_eq!((add.clone(), remove), (vec![blokli, a, b], vec![]));
    bypassed.extend(add);
    // b is no longer listed: kept up to the hysteresis, removed after it
    assert_eq!(reconcile_peers(&mut seen, &[a], &[blokli], &bypassed, H), (vec![], vec![]));
    let (add, remove) = reconcile_peers(&mut seen, &[a], &[blokli], &bypassed, H + 1);
    assert_eq!((add, remove.clone()), (vec![], vec![b]));
    for r in remove {
        bypassed.remove(&r);
    }
    // blokli is never listed and stays; a, listed again, was refreshed and stays
    assert_eq!(reconcile_peers(&mut seen, &[], &[blokli], &bypassed, 2 * H), (vec![], vec![]));
    assert_eq!(reconcile_peers(&mut seen, &[], &[blokli], &bypassed, 2 * H + 2), (vec![], vec![a]));
}

#[test]
fn a_failed_add_is_retried_and_a_returning_peer_is_added_again() {
    let a = ip(2);
    let mut seen = BTreeMap::new();
    let bypassed = BTreeSet::new();
    assert_eq!(reconcile_peers(&mut seen, &[a], &[], &bypassed, 0).0, vec![a]);
    // the add failed, so `a` is not bypassed: the next update asks again, even without a new listing
    assert_eq!(reconcile_peers(&mut seen, &[], &[], &bypassed, 10).0, vec![a]);
    // gone after expiry, back once listed again
    assert_eq!(reconcile_peers(&mut seen, &[], &[], &bypassed, H + 1).0, Vec::<Ipv4Addr>::new());
    assert_eq!(reconcile_peers(&mut seen, &[a], &[], &bypassed, H + 2).0, vec![a]);
}
