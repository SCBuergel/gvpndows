//! Service log: kept in memory (the newest lines, for the debug relay) and appended to a file.
//! Windows: %LOCALAPPDATA%\gvpndows\gvpndows.log; past pure::LOG_FILE_MAX it moves to gvpndows.old.log.

use std::fs::{File, OpenOptions};
use std::io::Write;
use std::path::PathBuf;
use std::sync::{Mutex, OnceLock};
use std::time::{SystemTime, UNIX_EPOCH};

const MAX_LINES: usize = 5000;

struct Log {
    lines: Mutex<Vec<String>>,
    /// the open file and its size
    file: Mutex<Option<(File, u64)>>,
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

fn open() -> std::io::Result<(File, u64)> {
    let f = OpenOptions::new().create(true).append(true).open(path())?;
    let len = f.metadata()?.len();
    Ok((f, len))
}

/// Moves the full log to gvpndows.old.log (replacing the older one) and opens a new one in the
/// same folder (same inherited ACL). If the move fails, the log starts over instead.
fn rotate() -> std::io::Result<(File, u64)> {
    if std::fs::rename(path(), dir().join("gvpndows.old.log")).is_err() {
        OpenOptions::new().write(true).truncate(true).open(path())?;
    }
    open()
}

/// Opens the log file (append). Returns an error text if the file cannot be opened;
/// logging then continues in memory only.
pub fn init() -> Result<PathBuf, String> {
    let p = path();
    let file = std::fs::create_dir_all(dir()).and_then(|_| open()).and_then(|f| {
        if crate::pure::log_full(f.1) {
            drop(f); // closed before the move
            rotate()
        } else {
            Ok(f)
        }
    });
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
        if let Some((file, len)) = f.as_mut() {
            let _ = writeln!(file, "{text}");
            let _ = file.flush();
            *len += text.len() as u64 + 1;
            if crate::pure::log_full(*len) {
                *f = None; // closed before the move
                *f = rotate().ok(); // on failure, the file log stops and memory goes on
            }
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
