//! App log: kept in memory for the log pane and appended to a file the user can send back.
//! Windows: %LOCALAPPDATA%\gvpndows\gvpndows.log

use std::fs::{File, OpenOptions};
use std::io::Write;
use std::path::PathBuf;
use std::sync::{Mutex, OnceLock};
use std::time::{SystemTime, UNIX_EPOCH};

const MAX_LINES: usize = 5000;

struct Log {
    lines: Mutex<Vec<String>>,
    file: Mutex<Option<File>>,
}

static LOG: OnceLock<Log> = OnceLock::new();
/// Lines ever logged in this process (the in-memory buffer keeps the newest MAX_LINES).
static TOTAL: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

pub fn dir() -> PathBuf {
    let base = std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(std::env::temp_dir);
    base.join("gvpndows")
}

pub fn path() -> PathBuf {
    dir().join("gvpndows.log")
}

/// Opens the log file (append). Returns an error text if the file cannot be opened;
/// logging then continues in memory only.
pub fn init() -> Result<PathBuf, String> {
    let p = path();
    let file = std::fs::create_dir_all(dir())
        .and_then(|_| OpenOptions::new().create(true).append(true).open(&p));
    let (file, res) = match file {
        Ok(f) => (Some(f), Ok(p)),
        Err(e) => (None, Err(format!("cannot open {}: {e}", p.display()))),
    };
    let _ = LOG.set(Log { lines: Mutex::new(Vec::new()), file: Mutex::new(file) });
    res
}

pub fn line(msg: impl AsRef<str>) {
    let Some(log) = LOG.get() else { return };
    let text = format!("{} {}", timestamp(), msg.as_ref());
    if let Ok(mut f) = log.file.lock() {
        if let Some(f) = f.as_mut() {
            let _ = writeln!(f, "{text}");
            let _ = f.flush();
        }
    }
    if let Ok(mut l) = log.lines.lock() {
        l.push(text);
        TOTAL.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        let n = l.len();
        if n > MAX_LINES {
            l.drain(..n - MAX_LINES);
        }
    }
}

/// Lines logged after line number `cursor`: (lines, new cursor, lines already dropped from the buffer).
pub fn since(cursor: u64) -> (Vec<String>, u64, u64) {
    let Some(log) = LOG.get() else { return (Vec::new(), cursor, 0) };
    let Ok(l) = log.lines.lock() else { return (Vec::new(), cursor, 0) };
    let total = TOTAL.load(std::sync::atomic::Ordering::SeqCst);
    let new = (total - cursor.min(total)) as usize;
    let take = new.min(l.len());
    (l[l.len() - take..].to_vec(), total, (new - take) as u64)
}

pub fn snapshot() -> Vec<String> {
    LOG.get().and_then(|l| l.lines.lock().ok().map(|v| v.clone())).unwrap_or_default()
}

/// UTC time as "YYYY-MM-DD HH:MM:SS.mmmZ" without a date crate.
pub fn timestamp() -> String {
    let d = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default();
    let secs = d.as_secs() as i64;
    let (days, rem) = (secs.div_euclid(86400), secs.rem_euclid(86400));
    // Civil-from-days (H. Hinnant)
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = yoe + era * 400 + i64::from(month <= 2);
    format!(
        "{year:04}-{month:02}-{day:02} {:02}:{:02}:{:02}.{:03}Z",
        rem / 3600,
        rem % 3600 / 60,
        rem % 60,
        d.subsec_millis()
    )
}
