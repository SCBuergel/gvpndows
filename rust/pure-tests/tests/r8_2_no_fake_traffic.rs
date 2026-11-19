//! R8-2: the app showed a "Live traffic" page with invented bandwidth and invented "not via VPN"
//! endpoints. The service reports no throughput, so the page must not come back on fake data.
use std::{fs, path::Path};

fn sources(dir: &Path, out: &mut Vec<(String, String)>) {
    for e in fs::read_dir(dir).unwrap() {
        let p = e.unwrap().path();
        if p.is_dir() {
            sources(&p, out);
        } else if matches!(p.extension().and_then(|x| x.to_str()), Some("cs" | "xaml")) {
            out.push((p.display().to_string(), fs::read_to_string(&p).unwrap()));
        }
    }
}

#[test]
fn app_has_no_simulated_traffic_page() {
    let app = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../winui/GnosisVpn");
    let mut files = Vec::new();
    sources(&app, &mut files);
    assert!(files.iter().any(|(f, _)| f.ends_with("Pages.cs")), "app sources not found");
    for (f, s) in &files {
        for bad in ["Live traffic", "not via VPN", "Traffic.Build", "\"traffic\""] {
            assert!(!s.contains(bad), "{f} contains {bad:?}");
        }
    }
}
