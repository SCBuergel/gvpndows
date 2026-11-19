//! R6-3: uninstall left the app's "Start with Windows" Run value (Tray.cs Startup.Apply) pointing at
//! the deleted exe. uninstall.ps1 must remove it from every loaded user hive, only when it starts this app.
use std::path::Path;

#[test]
fn uninstall_removes_the_apps_run_value_of_every_loaded_hive() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let ps = std::fs::read_to_string(root.join("dist/windows/uninstall.ps1")).unwrap();
    let tray = std::fs::read_to_string(root.join("winui/GnosisVpn/Ui/Tray.cs")).unwrap();
    // the value the app writes
    assert!(tray.contains(r#"CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")"#) && tray.contains(r#"SetValue("GnosisVPN""#));
    for needle in [
        r#"Get-ChildItem Registry::HKEY_USERS"#,
        r#"HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"#,
        r#"Join-Path $env:ProgramFiles "Gnosis VPN\GnosisVPN.exe""#,
        r#"$exe -ieq $appExe"#,
        r#"Remove-ItemProperty -LiteralPath $k -Name GnosisVPN -ErrorAction SilentlyContinue"#,
    ] {
        assert!(ps.contains(needle), "uninstall.ps1 lacks {needle}");
    }
    // after the service is gone, before the final message
    let at = |s: &str| ps.find(s).unwrap();
    assert!(at("sc.exe delete GnosisVPN") < at("Get-ChildItem Registry::HKEY_USERS"));
    assert!(at("Get-ChildItem Registry::HKEY_USERS") < at("Gnosis VPN removed in"));
}
