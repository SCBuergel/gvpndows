//! R7-9: after a service restart the app's command link still held the old pipe (IsConnected stays
//! true until I/O fails), so the first Connect/Disconnect (retry: false) was lost. A write that fails
//! on a reused connection delivered nothing and is sent once more on a new one; after the write,
//! retry: false still never resends.
use std::path::Path;

#[test]
fn a_failed_write_on_a_reused_pipe_is_sent_again_once() {
    let src = std::fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../winui/GnosisVpn/Engine/RealBackend.cs")).unwrap();
    let send = &src[src.find("public async Task<JsonNode?> Send(").unwrap()..];
    let send = &send[..send.find("\n    }\n").unwrap()];
    let at = |n: &str| send.find(n).unwrap_or_else(|| panic!("Send lacks {n}"));
    assert!(at("bool reused = Connected, written = false;") < at("if (!reused)"));
    // every new connection is still verified
    assert!(at("if (!reused)") < at("ServiceCheck.Verify(pipe);"));
    // written is set only once the write returned, before the reply is awaited
    assert!(at("await wr!.WriteLineAsync(") < at("written = true;") && at("written = true;") < at("await rd!.ReadLineAsync()"));
    assert!(send.contains("catch (Exception e) when (attempt == 0 && (retry || reused && !written))"));
    assert!(send.contains("if (!retry) { tries = 2;"), "one extra try at most");
    assert_eq!(send.matches("tries = ").count(), 2, "tries is set only at start and for the one resend");
}
