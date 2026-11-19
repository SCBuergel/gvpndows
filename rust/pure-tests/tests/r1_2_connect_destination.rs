//! R1-2: a pipe connect reaches the core (which logs the destination) only with a short,
//! control-character-free destination string.
use gvpndows_pure_tests::{route_request, PipeRoute, MAX_DESTINATION};

fn connect(dest: &str) -> PipeRoute {
    route_request(&format!(r#"{{"type":"connect","destination":{dest}}}"#))
}

#[test]
fn connect_destination_must_be_a_short_plain_string() {
    assert_eq!(connect(r#""GVPN-GER-1""#), PipeRoute::Command);
    assert_eq!(connect(r#""0xD9c11f07BfBC1914877d7395459223aFF9Dc2739""#), PipeRoute::Command);
    assert_eq!(connect(r#""Zürich 2""#), PipeRoute::Command);
    assert_eq!(connect(&format!(r#""{}""#, "a".repeat(MAX_DESTINATION))), PipeRoute::Command);
    for bad in [
        r#""x\n2026-11-19 10:00:00.000Z service: stopping""#,
        r#""x\rgnosis: forged""#,
        r#""x\tforged""#,
        r#""x\u001b[2Jforged""#,
        r#""x\u0085forged""#,
        r#""x\u007fforged""#,
        "17",
        r#"{"id":"x"}"#,
        "null",
    ] {
        assert!(matches!(connect(bad), PipeRoute::Rejected(_)), "{bad}");
    }
    assert!(matches!(connect(&format!(r#""{}""#, "a".repeat(MAX_DESTINATION + 1))), PipeRoute::Rejected(_)));
    assert!(matches!(route_request(r#"{"type":"connect"}"#), PipeRoute::Rejected(_)));
    assert_eq!(route_request(r#"{"type":"disconnect"}"#), PipeRoute::Command);
}
