//! Wintun, the driver behind the tunnel's network adapter: its DLL is built into gvpndows.exe and
//! loaded once from the service's folder. The adapter itself belongs to gnosis/root.rs.

use crate::log;
use std::sync::OnceLock;

const WINTUN_DLL: &[u8] = include_bytes!(concat!(env!("CARGO_MANIFEST_DIR"), "/vendor/wintun/bin/amd64/wintun.dll"));

static WINTUN: OnceLock<Result<wintun::Wintun, String>> = OnceLock::new();

pub(crate) fn wintun() -> Result<wintun::Wintun, String> {
    WINTUN
        .get_or_init(|| {
            let path = log::dir().join("wintun.dll");
            if std::fs::read(&path).ok().as_deref() != Some(WINTUN_DLL) {
                std::fs::write(&path, WINTUN_DLL).map_err(|e| format!("cannot write {}: {e}", path.display()))?;
            }
            let w = unsafe { wintun::load_from_path(&path) }.map_err(|e| format!("loading wintun.dll: {e}"))?;
            match wintun::get_running_driver_version(&w) {
                Ok(v) => log::line(format!("wintun driver running, version {v:?}")),
                Err(_) => log::line("wintun driver not loaded yet (it loads with the first adapter)"),
            }
            Ok(w)
        })
        .clone()
}
