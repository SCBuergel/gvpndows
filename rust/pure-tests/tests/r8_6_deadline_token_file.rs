//! R8-6: the do-build deadline fires from a systemd user timer without run.sh's environment, so a
//! non-default DO_TOKEN_FILE was lost and its droplet delete failed. The armed command must carry
//! the token file's absolute path (never the token).
use std::path::Path;

#[test]
fn deadline_command_carries_the_token_file_path() {
    let s = std::fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../scripts/do-build/run.sh")).unwrap();
    let at = |n: &str| s.find(n).unwrap_or_else(|| panic!("run.sh lacks {n}"));
    let arm = &s[at("arm_deadline() {")..];
    let arm = &arm[..arm.find('\n').unwrap()];
    assert!(arm.contains(r#"-- env "DO_TOKEN_FILE=$DO_TOKEN_FILE" python3 -I "$REPO/scripts/do-build/droplet.py" delete "$id""#), "{arm}");
    // resolved to an absolute path and checked before anything is created or armed
    let resolve = at(r#"DO_TOKEN_FILE=$(realpath "$DO_TOKEN_FILE"); export DO_TOKEN_FILE"#);
    assert!(at(r#"[ -r "$DO_TOKEN_FILE" ] ||"#) < resolve);
    assert!(resolve < at(r#"cd "$(dirname "$0")/../.."#), "resolve against the caller's directory");
    assert!(resolve < at("droplet.py create"));
    // the deadline is armed only through arm_deadline
    assert_eq!(s.matches(r#"python3 "$DEADLINE" start"#).count(), 1);
    assert!(!s.contains("cat \"$DO_TOKEN_FILE\"") && !s.contains("$(< \"$DO_TOKEN_FILE\")"), "the token is never read by the script");
}
