//! R8-5: the NOTIFYICON_VERSION_4 tray icon gets WM_LBUTTONUP then NIN_SELECT for one left click and
//! WM_RBUTTONUP then WM_CONTEXTMENU for one right click; acting on both toggled the flyout twice and
//! opened the menu twice. The C# cannot be built here, so this checks its source.
use std::{fs, path::Path};

#[test]
fn tray_acts_once_per_click() {
    let p = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../winui/GnosisVpn/Ui/NotifyIconNative.cs");
    let s = fs::read_to_string(p).unwrap();
    assert!(s.contains("NOTIFYICON_VERSION_4"), "the checks below assume a version-4 icon");
    for gone in ["WM_LBUTTONUP", "WM_RBUTTONUP"] {
        assert!(!s.contains(gone), "{gone} duplicates a version-4 notification");
    }
    assert!(s.contains("if (ev is NIN_SELECT or NIN_KEYSELECT)") && s.contains("else if (ev == WM_CONTEXTMENU) me.ShowMenu(pt);"));
    assert!(s.contains("if (menuOpen) return;") && s.contains("finally { menuOpen = false; }"), "ShowMenu re-entry guard");
}
