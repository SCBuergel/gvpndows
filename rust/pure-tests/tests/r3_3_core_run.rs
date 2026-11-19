//! R3-3: requests and replies of an older core run never reach the next run's slot.
use gvpndows_pure_tests::same_run;

#[test]
fn only_the_current_run_matches() {
    assert!(same_run(Some(2), 2));
    assert!(!same_run(Some(2), 1), "a thread of run 1 outliving it must not touch run 2");
    assert!(!same_run(Some(1), 2));
    assert!(!same_run(None, 1), "nothing is running");
}
