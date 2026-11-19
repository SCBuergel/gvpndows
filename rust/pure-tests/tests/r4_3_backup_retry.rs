//! R4-3: a failed identity backup is retried with a bounded backoff.
use gvpndows_pure_tests::backup_retry_secs;

#[test]
fn backup_retry_backs_off_to_ten_minutes() {
    let waits: Vec<u64> = (1..=8).map(backup_retry_secs).collect();
    assert_eq!(waits, [30, 60, 120, 240, 480, 600, 600, 600]);
    assert_eq!(backup_retry_secs(0), 30);
    assert_eq!(backup_retry_secs(u32::MAX), 600);
}
