//! R5-1: the service log file is bounded on disk: past LOG_FILE_MAX it moves to gvpndows.old.log
//! (replacing the older one) and a new file starts, at init and while logging.
#[path = "../../../src/log.rs"]
#[allow(dead_code)]
mod log;
use gvpndows_pure_tests as pure;
use pure::LOG_FILE_MAX;
use std::fs;

#[test]
fn log_file_rotates_at_the_limit() {
    assert!(!pure::log_full(LOG_FILE_MAX - 1) && pure::log_full(LOG_FILE_MAX));
    let base = std::path::Path::new(env!("CARGO_TARGET_TMPDIR")).join("r5_1");
    let _ = fs::remove_dir_all(&base);
    std::env::set_var("LOCALAPPDATA", &base);
    let (cur, old) = (log::path(), log::dir().join("gvpndows.old.log"));
    fs::create_dir_all(log::dir()).unwrap();
    fs::write(&cur, vec![b'x'; LOG_FILE_MAX as usize]).unwrap();

    log::init().unwrap();
    assert_eq!(fs::metadata(&old).unwrap().len(), LOG_FILE_MAX, "a full log moves at init");
    assert_eq!(fs::metadata(&cur).unwrap().len(), 0);

    let mb = "y".repeat(1 << 20);
    for _ in 0..30 {
        log::line(&mb);
    }
    let (c, o) = (fs::metadata(&cur).unwrap().len(), fs::metadata(&old).unwrap().len());
    assert!(c < LOG_FILE_MAX && o >= LOG_FILE_MAX && o < LOG_FILE_MAX + (2 << 20), "current {c}, old {o}");
    assert!(fs::read_to_string(&old).unwrap().contains(&mb), "the old file is the rotated log, not the init one");
    assert_eq!(log::since(0).0.len(), 30, "the in-memory log is untouched");
    log::line("after");
    assert!(fs::read_to_string(&cur).unwrap().ends_with("after\n"));
}
