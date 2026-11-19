//! R3-4: the service remembers the connect target from accepted connect replies, so a core that
//! restarts after ending on its own resumes the connection (upstream root's target_dest_id).
use gvpndows_pure_tests::{target_change, TargetChange};
use serde_json::json;

fn connect(kind: &str) -> serde_json::Value {
    json!({"type": "connect", "data": {"type": kind, "destination": {"id": "ex1", "meta": {}}}})
}

#[test]
fn accepted_connects_set_the_target() {
    for kind in ["connecting", "waiting_to_connect", "already_connected"] {
        assert_eq!(target_change("connect", Some(&connect(kind))), TargetChange::Set("ex1".into()), "{kind}");
    }
}

#[test]
fn refusals_and_errors_keep_it() {
    for kind in ["unable_to_connect", "destination_not_found", "destination_ambiguous"] {
        assert_eq!(target_change("connect", Some(&connect(kind))), TargetChange::Keep, "{kind}");
    }
    assert_eq!(target_change("connect", None), TargetChange::Keep);
    assert_eq!(target_change("connect", Some(&json!({"error": "x"}))), TargetChange::Keep);
}

#[test]
fn disconnect_clears_it_whatever_the_reply() {
    assert_eq!(target_change("disconnect", None), TargetChange::Clear);
    assert_eq!(target_change("disconnect", Some(&json!({"type": "disconnect", "data": {}}))), TargetChange::Clear);
    assert_eq!(target_change("status", Some(&connect("connecting"))), TargetChange::Keep);
}
