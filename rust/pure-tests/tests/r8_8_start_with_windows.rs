//! R8-8: "Start with Windows" shows On by default, so the app writes the Run value at start when
//! the preference is on (only the Settings toggles wrote it before). The C# cannot be built here,
//! so this checks its source.
use std::{fs, path::Path};

#[test]
fn start_with_windows_on_is_applied_at_start() {
    let p = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../winui/GnosisVpn/MainWindow.xaml.cs");
    let s = fs::read_to_string(p).unwrap();
    let load = s.find("U.StartWin = Prefs.Get(\"startWin\") != \"0\";").expect("the preference is loaded");
    let apply = s.find("if (U.StartWin) Startup.Apply(U);").expect("an On preference is applied at start");
    assert!(apply > load, "applied after it is loaded");
}
