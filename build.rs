// Embed a manifest that asks for admin rights (UAC prompt at start): Wintun, routes and the WFP
// kill switch need them. A Windows Service split comes with the Gnosis VPN core (milestone 3).
fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    if std::env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc") {
        println!("cargo:rustc-link-arg-bins=/MANIFEST:EMBED");
        println!("cargo:rustc-link-arg-bins=/MANIFESTUAC:level='requireAdministrator' uiAccess='false'");
    }
}
