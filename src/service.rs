//! Windows service mode: the Gnosis VPN core and the tunnel run here, as SYSTEM, and the
//! (unelevated) WinUI app drives them over a named pipe.
//!   gvpndows.exe --service   started by the Service Control Manager (service "GnosisVPN")
//!   gvpndows.exe --serve     the same in the foreground (an elevated console, for tests)
//! Pipe \\.\pipe\gnosisvpn, one JSON request per line, one JSON reply per line:
//!   any WorkerCommand ({"type":"status"}, {"type":"connect","destination":"<id>"}, {"type":"disconnect"},
//!   {"type":"balance"}, ...)  -> the core's Response, or {"error":"..."}
//!   {"type":"x_state"}        -> {"core": "<running|starting ...>", "started": bool, "status": <last status>}
//! State lives in %ProgramData%\GnosisVPN (identity, chain data) and %ProgramData%\gvpndows (logs).
//! The service never connects by itself: connecting is always a user action in the app.
//! Only interactively signed-in users can open the pipe, and only the commands in
//! `pure::ALLOWED_COMMANDS` pass (no funding commands). The app checks that the pipe's server is
//! the GnosisVPN service before it trusts any answer.

use crate::{gnosis, log};
use std::ffi::c_void;
use std::io::{BufRead, BufReader, Read, Write};
use std::path::Path;
use std::ptr::{null, null_mut};
use std::sync::mpsc;
use std::sync::Mutex;
use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, LocalFree, ERROR_PIPE_CONNECTED, HANDLE, INVALID_HANDLE_VALUE, NO_ERROR};
use windows_sys::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, GetNamedSecurityInfoW, SetNamedSecurityInfoW, SE_FILE_OBJECT,
};
use windows_sys::Win32::Security::{
    AdjustTokenPrivileges, GetSecurityDescriptorDacl, GetSecurityDescriptorOwner, LookupPrivilegeValueW, ACL, DACL_SECURITY_INFORMATION,
    OWNER_SECURITY_INFORMATION, PROTECTED_DACL_SECURITY_INFORMATION, PSECURITY_DESCRIPTOR, PSID, SECURITY_ATTRIBUTES, SE_PRIVILEGE_ENABLED,
    TOKEN_ADJUST_PRIVILEGES, TOKEN_PRIVILEGES, TOKEN_QUERY,
};
use windows_sys::Win32::Storage::FileSystem::{FlushFileBuffers, ReadFile, WriteFile, FILE_FLAG_FIRST_PIPE_INSTANCE, PIPE_ACCESS_DUPLEX};
use windows_sys::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PIPE_READMODE_BYTE, PIPE_REJECT_REMOTE_CLIENTS, PIPE_TYPE_BYTE, PIPE_UNLIMITED_INSTANCES, PIPE_WAIT,
};
use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
use windows_sys::Win32::System::Services::{
    RegisterServiceCtrlHandlerExW, SetServiceStatus, StartServiceCtrlDispatcherW, SERVICE_ACCEPT_SHUTDOWN, SERVICE_ACCEPT_STOP,
    SERVICE_CONTROL_SHUTDOWN, SERVICE_CONTROL_STOP, SERVICE_RUNNING, SERVICE_START_PENDING, SERVICE_STATUS, SERVICE_STATUS_HANDLE,
    SERVICE_STOPPED, SERVICE_STOP_PENDING, SERVICE_TABLE_ENTRYW, SERVICE_WIN32_OWN_PROCESS,
};

pub const PIPE: &str = r"\\.\pipe\gnosisvpn";
const SERVICE_NAME: &str = "GnosisVPN";
/// SYSTEM and Administrators: full; interactively signed-in users: read plus FILE_WRITE_DATA only
/// (0x12008b = FILE_GENERIC_READ | FILE_WRITE_DATA). Generic write would include
/// FILE_CREATE_PIPE_INSTANCE, letting users add server instances of the pipe.
const PIPE_SDDL: &str = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x12008b;;;IU)";

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// Owner SYSTEM; only SYSTEM and Administrators get access, inherited by everything inside.
const DIR_SDDL: &str = "O:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
/// The same for files that already exist (no inheritance flags on a file's own ACEs).
const FILE_SDDL: &str = "O:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)";

/// Service and foreground server keep their state out of any user profile, in folders only SYSTEM
/// and Administrators can read: they hold the node identity and its password (the wallet key).
/// A folder someone else created first is taken over (owner reset to SYSTEM). Fails closed: if the
/// folders cannot be secured, the service does not start.
pub fn use_program_data() -> Result<(), String> {
    let pd = std::env::var_os("ProgramData").unwrap_or_else(|| "C:\\ProgramData".into());
    // SAFETY: called first thing in main, before any other thread exists.
    unsafe { std::env::set_var("LOCALAPPDATA", &pd) };
    enable_privileges(&["SeTakeOwnershipPrivilege", "SeRestorePrivilege"]);
    for name in ["GnosisVPN", "gvpndows"] {
        let dir = Path::new(&pd).join(name);
        // A folder (or a link) someone else made is never adopted: whatever is inside it, such as
        // a planted hopr.id/hopr.pass, could be theirs. It is moved aside and the service starts fresh.
        if let Ok(meta) = std::fs::symlink_metadata(&dir) {
            let link = is_reparse_point(&meta);
            let owner = owner_sid(&dir);
            if link || !owner.as_deref().is_some_and(crate::pure::owner_trusted) {
                let aside = Path::new(&pd).join(format!("{name}.untrusted-{}", std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)));
                std::fs::rename(&dir, &aside).map_err(|e| format!("moving aside {} (owner {owner:?}, link {link}): {e}", dir.display()))?;
                eprintln!("gvpndows: {} was not created by the service (owner {owner:?}, link {link}); moved to {}", dir.display(), aside.display());
            }
        }
        create_private_dir(&dir)?;
        secure_tree(&dir)?;
    }
    Ok(())
}

/// Creates the folder with its final security descriptor in one step (owner SYSTEM, SYSTEM and
/// Administrators only), so there is no moment in which other users could put files into it.
/// Creation is always attempted: if the folder exists by now, it is used only if it is still not a
/// link and still owned by SYSTEM or Administrators (someone may have created it since the check
/// above and kept a handle to it); otherwise the service fails closed.
fn create_private_dir(dir: &Path) -> Result<(), String> {
    use windows_sys::Win32::Foundation::ERROR_ALREADY_EXISTS;
    use windows_sys::Win32::Storage::FileSystem::CreateDirectoryW;
    unsafe {
        let mut sd: PSECURITY_DESCRIPTOR = null_mut();
        if ConvertStringSecurityDescriptorToSecurityDescriptorW(wide(DIR_SDDL).as_ptr(), 1, &mut sd, null_mut()) == 0 {
            return Err(format!("security descriptor: {}", std::io::Error::last_os_error()));
        }
        let sa = SECURITY_ATTRIBUTES { nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32, lpSecurityDescriptor: sd, bInheritHandle: 0 };
        let ok = CreateDirectoryW(wide(&dir.to_string_lossy()).as_ptr(), &sa) != 0;
        let err = std::io::Error::last_os_error();
        LocalFree(sd as _);
        if ok {
            return Ok(());
        }
        if err.raw_os_error() != Some(ERROR_ALREADY_EXISTS as i32) {
            return Err(format!("creating {}: {err}", dir.display()));
        }
    }
    let link = std::fs::symlink_metadata(dir).map(|m| is_reparse_point(&m)).unwrap_or(true);
    let owner = owner_sid(dir);
    if link || !owner.as_deref().is_some_and(crate::pure::owner_trusted) {
        return Err(format!("{} appeared while the service was starting (owner {owner:?}, link {link}); not using it", dir.display()));
    }
    Ok(())
}

fn is_reparse_point(meta: &std::fs::Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    meta.file_attributes() & 0x400 != 0 // FILE_ATTRIBUTE_REPARSE_POINT (junctions, symlinks)
}

/// The owner of a file or folder as a SID string ("S-1-5-18" for SYSTEM).
fn owner_sid(path: &Path) -> Option<String> {
    unsafe {
        let p = wide(&path.to_string_lossy());
        let (mut owner, mut sd): (PSID, PSECURITY_DESCRIPTOR) = (null_mut(), null_mut());
        if GetNamedSecurityInfoW(p.as_ptr(), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, &mut owner, null_mut(), null_mut(), null_mut(), &mut sd) != 0 {
            return None;
        }
        let mut s: *mut u16 = null_mut();
        let ok = ConvertSidToStringSidW(owner, &mut s) != 0;
        let out = ok.then(|| {
            let len = (0..).take_while(|&i| *s.add(i) != 0).count();
            String::from_utf16_lossy(std::slice::from_raw_parts(s, len))
        });
        if !s.is_null() {
            LocalFree(s as _);
        }
        LocalFree(sd as _);
        out
    }
}

fn enable_privileges(names: &[&str]) {
    unsafe {
        let mut token: HANDLE = null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &mut token) == 0 {
            return;
        }
        for n in names {
            let mut tp: TOKEN_PRIVILEGES = std::mem::zeroed();
            tp.PrivilegeCount = 1;
            tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
            if LookupPrivilegeValueW(null(), wide(n).as_ptr(), &mut tp.Privileges[0].Luid) != 0 {
                AdjustTokenPrivileges(token, 0, &tp, 0, null_mut(), null_mut());
            }
        }
        CloseHandle(token);
    }
}

fn set_security(path: &Path, sddl: &str) -> Result<(), String> {
    unsafe {
        let mut sd: PSECURITY_DESCRIPTOR = null_mut();
        if ConvertStringSecurityDescriptorToSecurityDescriptorW(wide(sddl).as_ptr(), 1, &mut sd, null_mut()) == 0 {
            return Err(format!("security descriptor: {}", std::io::Error::last_os_error()));
        }
        let (mut owner, mut def): (PSID, i32) = (null_mut(), 0);
        let (mut dacl, mut present): (*mut ACL, i32) = (null_mut(), 0);
        GetSecurityDescriptorOwner(sd, &mut owner, &mut def);
        GetSecurityDescriptorDacl(sd, &mut present, &mut dacl, &mut def);
        let p = wide(&path.to_string_lossy());
        let rc = SetNamedSecurityInfoW(
            p.as_ptr(),
            SE_FILE_OBJECT,
            OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
            owner,
            null_mut(),
            dacl,
            null(),
        );
        LocalFree(sd as _);
        if rc != 0 {
            return Err(format!("securing {}: {}", path.display(), std::io::Error::from_raw_os_error(rc as i32)));
        }
        Ok(())
    }
}

/// The folder and everything already in it: owner SYSTEM, access for SYSTEM and Administrators only.
fn secure_tree(dir: &Path) -> Result<(), String> {
    set_security(dir, DIR_SDDL)?;
    let mut stack = vec![dir.to_path_buf()];
    while let Some(d) = stack.pop() {
        for e in std::fs::read_dir(&d).map_err(|e| format!("{}: {e}", d.display()))?.flatten() {
            let p = e.path();
            // never follow a junction or symlink: securing its target would lock others out of it
            if std::fs::symlink_metadata(&p).map(|m| is_reparse_point(&m)).unwrap_or(true) {
                std::fs::remove_file(&p).or_else(|_| std::fs::remove_dir(&p)).map_err(|e| format!("removing link {}: {e}", p.display()))?;
                continue;
            }
            // never adopt what another user put here (a planted key): remove it
            let owner = owner_sid(&p);
            if !owner.as_deref().is_some_and(crate::pure::owner_trusted) {
                let r = if p.is_dir() { std::fs::remove_dir_all(&p) } else { std::fs::remove_file(&p) };
                r.map_err(|e| format!("removing {} (owner {owner:?}): {e}", p.display()))?;
                eprintln!("gvpndows: removed {} (owner {owner:?}): not created by the service", p.display());
                continue;
            }
            if e.file_type().map(|t| t.is_dir()).unwrap_or(false) {
                set_security(&p, DIR_SDDL)?;
                stack.push(p);
            } else {
                set_security(&p, FILE_SDDL)?;
            }
        }
    }
    Ok(())
}

// ---------------------------------------------------------------- pipe server

/// One request line -> one reply line.
fn handle(line: &str) -> String {
    use crate::pure::PipeRoute;
    match crate::pure::route_request(line) {
        PipeRoute::State => serde_json::json!({
            "core": gnosis::CORE_STATE.lock().unwrap().clone(),
            "started": gnosis::running(),
            "status": gnosis::STATUS.lock().unwrap().clone(),
        })
        .to_string(),
        PipeRoute::Start => match gnosis::start() {
            Ok(()) => serde_json::json!({ "ok": true }).to_string(),
            Err(e) => serde_json::json!({ "error": e }).to_string(),
        },
        PipeRoute::Command => match gnosis::command(line) {
            Ok(r) => r.to_string(),
            Err(e) => serde_json::json!({ "error": e }).to_string(),
        },
        PipeRoute::Rejected(why) => serde_json::json!({ "error": why }).to_string(),
    }
}

/// A named-pipe client as std::io Read/Write.
struct PipeConn(HANDLE);
unsafe impl Send for PipeConn {}
impl Read for PipeConn {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        let mut n = 0u32;
        let ok = unsafe { ReadFile(self.0, buf.as_mut_ptr(), buf.len() as u32, &mut n, null_mut()) };
        if ok == 0 {
            // broken pipe = the client went away: report end of stream
            return Ok(0);
        }
        Ok(n as usize)
    }
}
impl Write for PipeConn {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        let mut n = 0u32;
        let ok = unsafe { WriteFile(self.0, buf.as_ptr(), buf.len() as u32, &mut n, null_mut()) };
        if ok == 0 {
            return Err(std::io::Error::last_os_error());
        }
        Ok(n as usize)
    }
    fn flush(&mut self) -> std::io::Result<()> {
        unsafe { FlushFileBuffers(self.0) };
        Ok(())
    }
}
impl Drop for PipeConn {
    fn drop(&mut self) {
        unsafe {
            DisconnectNamedPipe(self.0);
            CloseHandle(self.0);
        }
    }
}

fn client(conn: PipeConn) {
    let mut w = PipeConn(conn.0);
    let mut reader = BufReader::new(conn);
    loop {
        // at most MAX_REQUEST + 1 bytes per line: a longer one is refused and the client dropped
        let mut buf = Vec::new();
        let n = match (&mut reader).take(crate::pure::MAX_REQUEST as u64 + 1).read_until(b'\n', &mut buf) {
            Ok(n) => n,
            Err(_) => break,
        };
        if n == 0 {
            break;
        }
        if buf.last() != Some(&b'\n') && buf.len() > crate::pure::MAX_REQUEST {
            let _ = w.write_all(b"{\"error\":\"request too long\"}\n");
            break;
        }
        let line = String::from_utf8_lossy(&buf);
        if line.trim().is_empty() {
            continue;
        }
        let mut reply = handle(line.trim_end());
        reply.push('\n');
        if w.write_all(reply.as_bytes()).is_err() {
            break;
        }
    }
    std::mem::forget(w); // the reader's PipeConn closes the handle
}

fn serve_pipe() {
    let sddl = wide(PIPE_SDDL);
    let mut sd: PSECURITY_DESCRIPTOR = null_mut();
    if unsafe { ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.as_ptr(), 1, &mut sd, null_mut()) } == 0 {
        // fail closed: never serve the pipe with the default DACL
        log::line(format!("service: pipe security descriptor failed ({}); not serving the pipe", std::io::Error::last_os_error()));
        return;
    }
    let sa = SECURITY_ATTRIBUTES { nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32, lpSecurityDescriptor: sd, bInheritHandle: 0 };
    let name = wide(PIPE);
    log::line(format!("service: listening on {PIPE}"));
    let mut first = true;
    loop {
        // The first instance must be ours (FILE_FLAG_FIRST_PIPE_INSTANCE): if another process already
        // created the name, the service stops serving rather than share it. Remote clients are refused.
        let open = PIPE_ACCESS_DUPLEX | if first { FILE_FLAG_FIRST_PIPE_INSTANCE } else { 0 };
        let h = unsafe {
            CreateNamedPipeW(name.as_ptr(), open, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, PIPE_UNLIMITED_INSTANCES, 65536, 65536, 0, &sa)
        };
        if h == INVALID_HANDLE_VALUE {
            let e = std::io::Error::last_os_error();
            if first {
                log::line(format!("service: {PIPE} exists already or cannot be created ({e}); not serving it"));
                unsafe { LocalFree(sd as _) };
                return;
            }
            log::line(format!("service: CreateNamedPipe failed: {e}"));
            std::thread::sleep(std::time::Duration::from_secs(2));
            continue;
        }
        first = false;
        let ok = unsafe { ConnectNamedPipe(h, null_mut()) } != 0 || unsafe { GetLastError() } == ERROR_PIPE_CONNECTED;
        if !ok {
            unsafe { CloseHandle(h) };
            continue;
        }
        let conn = PipeConn(h);
        std::thread::spawn(move || client(conn));
    }
}

/// Starts the core and the pipe server (background threads).
fn start_all() {
    log::line(format!("service: starting (pid {}), data in {}", std::process::id(), gnosis::user_dirs().data.display()));
    gnosis::AUTO_CHECK.store(false, std::sync::atomic::Ordering::SeqCst);
    std::thread::Builder::new().name("pipe".into()).spawn(serve_pipe).ok();
    // The core needs the network at start (Blokli). At boot, after waking up or on a brief DNS
    // failure it can fail; keep retrying instead of leaving the app waiting for a core forever.
    std::thread::Builder::new()
        .name("core-start".into())
        .spawn(|| {
            for n in 0u32.. {
                match gnosis::start() {
                    Ok(()) => {
                        if n > 0 {
                            log::line(format!("service: core started after {n} retries"));
                        }
                        return;
                    }
                    Err(e) if e.contains("already running") => return,
                    Err(e) if n % 6 == 0 => log::line(format!("service: core start failed, retrying every 10 s: {e}")),
                    Err(_) => {}
                }
                std::thread::sleep(std::time::Duration::from_secs(10));
            }
        })
        .ok();
}

/// --serve: in the foreground until the process is ended.
pub fn run_console() {
    start_all();
    loop {
        std::thread::sleep(std::time::Duration::from_secs(3600));
    }
}

// ---------------------------------------------------------------- Service Control Manager

static STOP: Mutex<Option<mpsc::Sender<()>>> = Mutex::new(None);
static HANDLE_: Mutex<usize> = Mutex::new(0);

fn set_state(state: u32, accept: u32) {
    let h = *HANDLE_.lock().unwrap() as SERVICE_STATUS_HANDLE;
    let st = SERVICE_STATUS {
        dwServiceType: SERVICE_WIN32_OWN_PROCESS,
        dwCurrentState: state,
        dwControlsAccepted: accept,
        dwWin32ExitCode: NO_ERROR,
        dwServiceSpecificExitCode: 0,
        dwCheckPoint: 0,
        dwWaitHint: 10_000,
    };
    unsafe { SetServiceStatus(h, &st) };
}

unsafe extern "system" fn ctrl(control: u32, _ev: u32, _data: *mut c_void, _ctx: *mut c_void) -> u32 {
    if control == SERVICE_CONTROL_STOP || control == SERVICE_CONTROL_SHUTDOWN {
        set_state(SERVICE_STOP_PENDING, 0);
        if let Some(tx) = STOP.lock().unwrap().as_ref() {
            let _ = tx.send(());
        }
    }
    NO_ERROR
}

unsafe extern "system" fn service_main(_argc: u32, _argv: *mut *mut u16) {
    let name = wide(SERVICE_NAME);
    let h = RegisterServiceCtrlHandlerExW(name.as_ptr(), Some(ctrl), null());
    *HANDLE_.lock().unwrap() = h as usize;
    set_state(SERVICE_START_PENDING, 0);
    let (tx, rx) = mpsc::channel();
    *STOP.lock().unwrap() = Some(tx);
    start_all();
    set_state(SERVICE_RUNNING, SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN);
    let _ = rx.recv();
    log::line("service: stopping");
    gnosis::shutdown();
    std::thread::sleep(std::time::Duration::from_secs(1));
    set_state(SERVICE_STOPPED, 0);
}

/// --service: hands the process to the Service Control Manager; returns false if not started by it.
pub fn run_service() -> bool {
    let name = wide(SERVICE_NAME);
    let table = [
        SERVICE_TABLE_ENTRYW { lpServiceName: name.as_ptr() as *mut u16, lpServiceProc: Some(service_main) },
        SERVICE_TABLE_ENTRYW { lpServiceName: null_mut(), lpServiceProc: None },
    ];
    unsafe { StartServiceCtrlDispatcherW(table.as_ptr()) != 0 }
}
