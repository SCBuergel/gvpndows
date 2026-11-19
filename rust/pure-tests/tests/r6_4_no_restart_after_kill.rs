//! R6-4: when Stop-GnosisService must kill a hung service, the SCM counted that as a failure and
//! restarted the old service (install.ps1's recovery actions) while the update replaced its files.
use std::path::Path;

fn read(f: &str) -> String {
    std::fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../dist/windows").join(f)).unwrap()
}

#[test]
fn recovery_actions_are_off_before_the_kill_and_set_again_after_the_copy() {
    let common = read("common.ps1");
    let f = &common[common.find("function Stop-GnosisService").unwrap()..];
    let f = &f[..f.find("\n}\n").unwrap()];
    let at = |s: &str| f.find(s).unwrap_or_else(|| panic!("Stop-GnosisService lacks {s}"));
    // verbatim argument string: PowerShell 5.1 would drop a bare "" for a native command
    let clear = at(r#"Start-Process sc.exe -ArgumentList 'failure GnosisVPN reset= 0 actions= ""' -Wait -NoNewWindow -PassThru"#);
    let checked = at("if ($sc.ExitCode) { throw");
    let kill = at("Get-ServiceProcess | Stop-Process -Force");
    let wait = at("for ($i = 0; $i -lt 15");
    let fail = at(r#"throw "the service did not stop even after its process was ended (its restart on failure is off until an install succeeds)""#);
    assert!(clear < checked && checked < kill && kill < wait && wait < fail);

    let install = read("install.ps1");
    let i = |s: &str| install.find(s).unwrap_or_else(|| panic!("install.ps1 lacks {s}"));
    assert!(i("Stop-GnosisService 30") < i(r#"Copy-Item (Join-Path $here "service\gvpndows.exe")"#));
    assert!(i(r#"Copy-Item (Join-Path $here "service\gvpndows.exe")"#) < i("sc.exe failure GnosisVPN reset= 86400 actions= restart/"));
}
