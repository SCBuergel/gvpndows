//! R2-6: root requests are applied one at a time in the order sent, even when an earlier one is slow.
use gvpndows_pure_tests::serial_worker;
use std::sync::mpsc;
use std::time::Duration;

#[test]
fn requests_run_in_order_and_the_worker_ends_with_its_senders() {
    let (done, seen) = mpsc::channel();
    let tx = serial_worker("test-root", move |(n, slow): (u32, bool)| {
        if slow {
            std::thread::sleep(Duration::from_millis(200)); // a long UpdatePeerIps holding Root
        }
        done.send(n).unwrap();
    })
    .unwrap();
    tx.send((1, true)).unwrap(); // UpdatePeerIps
    tx.send((2, false)).unwrap(); // TearDownWg
    tx.send((3, false)).unwrap(); // the next SetupTunnel
    drop(tx);
    // `seen` ends only when the worker dropped its closure (and `done`): the thread has finished
    assert_eq!(seen.iter().collect::<Vec<_>>(), [1, 2, 3]);
}
