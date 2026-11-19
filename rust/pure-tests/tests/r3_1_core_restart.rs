//! R3-1: the core is restarted after it ends on its own, with a bounded backoff.
use gvpndows_pure_tests::core_restart;

#[test]
fn core_restart_backs_off_and_resets_after_a_long_run() {
    let mut a = 0;
    let mut waits = vec![];
    for _ in 0..9 {
        let (n, d) = core_restart(a, 30);
        assert_eq!(n, a + 1);
        a = n;
        waits.push(d);
    }
    assert_eq!(waits, [5, 10, 20, 40, 80, 160, 300, 300, 300]);
    assert_eq!(core_restart(a, 600), (1, 5));
    assert_eq!(core_restart(u32::MAX, 0), (u32::MAX, 300));
}
