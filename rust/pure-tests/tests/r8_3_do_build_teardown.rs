//! R8-3: scripts/do-build/run.sh deletes a droplet whose create never reported an id (a POST that
//! failed client-side after DigitalOcean accepted it), found by its name, and warns loudly when the
//! lookup itself fails. Runs run.sh against a fake droplet.py and deadline in a scratch tree.
use std::{fs, path::Path, process::Command};

const FAKE: &str = r#"import os, sys
log = open(os.environ["FAKE_LOG"], "a")
cmd = sys.argv[1]
log.write(" ".join(sys.argv[1:]) + "\n")
if cmd == "create":
    if os.environ["FAKE_MODE"] == "slow":  # the id comes late; run.sh gets TERM before
        import time; time.sleep(2); print(4243, flush=True); time.sleep(30)
    sys.exit("create: timed out")  # prints no id
if cmd == "find":
    if os.environ["FAKE_MODE"] == "lookupfail":
        sys.exit("list failed: 500")
    print(4242)
if cmd == "delete":
    if os.environ["FAKE_MODE"] == "slow":
        sys.exit("NOT verified gone")
    print("droplet", sys.argv[2], "verified gone")
"#;

fn run(mode: &str) -> (bool, String, String) {
    run_term(mode, None)
}

fn run_term(mode: &str, term_after_ms: Option<u64>) -> (bool, String, String) {
    let repo = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let t = Path::new(env!("CARGO_TARGET_TMPDIR")).join(format!("r8_3_{mode}"));
    let _ = fs::remove_dir_all(&t);
    let d = t.join("tree/scripts/do-build");
    fs::create_dir_all(&d).unwrap();
    fs::copy(repo.join("scripts/do-build/run.sh"), d.join("run.sh")).unwrap();
    fs::write(d.join("droplet.py"), FAKE).unwrap();
    let deadline = "import os, sys\nopen(os.environ[\"FAKE_LOG\"], \"a\").write(\"deadline \" + \" \".join(sys.argv[1:]) + \"\\n\")\n";
    fs::write(t.join("deadline"), deadline).unwrap();
    let st = t.join("home/.local/state/gvpndows-do");
    fs::create_dir_all(&st).unwrap();
    for k in ["id_ed25519", "id_ed25519.pub"] {
        fs::write(st.join(k), "fake").unwrap();
    }
    fs::write(t.join("home/do-api-token"), "fake").unwrap(); // run.sh refuses to start without one (R8-6)
    let log = t.join("calls.log");
    let out = Command::new("bash")
        .arg(d.join("run.sh"))
        .env("HOME", t.join("home"))
        .env("DEADLINE", t.join("deadline"))
        .env("FAKE_LOG", &log)
        .env("FAKE_MODE", mode)
        .stderr(std::process::Stdio::piped())
        .stdout(std::process::Stdio::null())
        .spawn()
        .unwrap();
    if let Some(ms) = term_after_ms {
        std::thread::sleep(std::time::Duration::from_millis(ms));
        Command::new("kill").args(["-TERM", &out.id().to_string()]).status().unwrap();
    }
    let out = out.wait_with_output().unwrap();
    (out.status.success(), fs::read_to_string(&log).unwrap_or_default(), String::from_utf8_lossy(&out.stderr).into_owned())
}

#[test]
fn droplet_without_an_id_is_found_by_name_and_deleted() {
    let (ok, calls, err) = run("found");
    assert!(!ok, "{err}");
    assert!(calls.contains("find gvpndows-build-") && calls.contains("delete 4242"), "calls: {calls}\nstderr: {err}");
}

#[test]
fn failed_lookup_warns_instead_of_claiming_no_droplet() {
    let (ok, calls, err) = run("lookupfail");
    assert!(!ok);
    assert!(!calls.contains("delete"), "calls: {calls}");
    assert!(err.contains("cannot look up droplet") && err.contains("cloud.digitalocean.com"), "stderr: {err}");
}

#[test]
fn term_during_create_arms_the_deadline_and_deletes_the_droplet() {
    let (ok, calls, err) = run_term("slow", Some(500));
    assert!(!ok);
    let (deadline, delete) = (calls.find("deadline start gvpndows-build-"), calls.find("\ndelete 4243"));
    assert!(deadline.is_some() && delete.is_some() && deadline < delete, "calls: {calls}\nstderr: {err}");
    // R8-6: the armed delete names the token file by its absolute path; the timer has no run.sh environment
    assert!(calls.contains(" -- env DO_TOKEN_FILE=/") && calls.contains("/home/do-api-token python3 -I "), "calls: {calls}");
    assert!(calls.contains("delete 4243\n") && err.contains("deadline stays armed"), "calls: {calls}\nstderr: {err}");
}
