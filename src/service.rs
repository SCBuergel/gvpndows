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

use crate::{gnosis, log};
use std::ffi::c_void;
use std::io::{BufRead, BufReader, Read, Write};
use std::ptr::{null, null_mut};
use std::sync::mpsc;
use std::sync::Mutex;
use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, LocalFree, ERROR_PIPE_CONNECTED, HANDLE, INVALID_HANDLE_VALUE, NO_ERROR};
use windows_sys::Win32::Security::Authorization::ConvertStringSecurityDescriptorToSecurityDescriptorW;
use windows_sys::Win32::Security::{PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES};
use windows_sys::Win32::Storage::FileSystem::{FlushFileBuffers, ReadFile, WriteFile, PIPE_ACCESS_DUPLEX};
use windows_sys::Win32::System::Pipes::{ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE, PIPE_UNLIMITED_INSTANCES, PIPE_WAIT};
use windows_sys::Win32::System::Services::{
    RegisterServiceCtrlHandlerExW, SetServiceStatus, StartServiceCtrlDispatcherW, SERVICE_ACCEPT_SHUTDOWN, SERVICE_ACCEPT_STOP,
    SERVICE_CONTROL_SHUTDOWN, SERVICE_CONTROL_STOP, SERVICE_RUNNING, SERVICE_START_PENDING, SERVICE_STATUS, SERVICE_STATUS_HANDLE,
    SERVICE_STOPPED, SERVICE_STOP_PENDING, SERVICE_TABLE_ENTRYW, SERVICE_WIN32_OWN_PROCESS,
};

pub const PIPE: &str = r"\\.\pipe\gnosisvpn";
const SERVICE_NAME: &str = "GnosisVPN";
/// SYSTEM and Administrators: full; interactively signed-in users: read and write (they run the app).
const PIPE_SDDL: &str = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)";

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// Service and foreground server keep their state out of any user profile.
pub fn use_program_data() {
    let pd = std::env::var_os("ProgramData").unwrap_or_else(|| "C:\\ProgramData".into());
    // SAFETY: called first thing in main, before any other thread exists.
    unsafe { std::env::set_var("LOCALAPPDATA", pd) };
}

// ---------------------------------------------------------------- pipe server

/// One request line -> one reply line.
fn handle(line: &str) -> String {
    let v: serde_json::Value = match serde_json::from_str(line) {
        Ok(v) => v,
        Err(e) => return serde_json::json!({ "error": format!("bad request: {e}") }).to_string(),
    };
    match v["type"].as_str() {
        Some("x_state") => serde_json::json!({
            "core": gnosis::CORE_STATE.lock().unwrap().clone(),
            "started": gnosis::running(),
            "status": gnosis::STATUS.lock().unwrap().clone(),
        })
        .to_string(),
        Some("x_start") => match gnosis::start() {
            Ok(()) => serde_json::json!({ "ok": true }).to_string(),
            Err(e) => serde_json::json!({ "error": e }).to_string(),
        },
        _ => match gnosis::command(line) {
            Ok(r) => r.to_string(),
            Err(e) => serde_json::json!({ "error": e }).to_string(),
        },
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
    let reader = BufReader::new(conn);
    for line in reader.lines() {
        let Ok(line) = line else { break };
        if line.trim().is_empty() {
            continue;
        }
        let mut reply = handle(&line);
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
    unsafe { ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.as_ptr(), 1, &mut sd, null_mut()) };
    let sa = SECURITY_ATTRIBUTES { nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32, lpSecurityDescriptor: sd, bInheritHandle: 0 };
    let name = wide(PIPE);
    log::line(format!("service: listening on {PIPE}"));
    loop {
        let h = unsafe {
            CreateNamedPipeW(name.as_ptr(), PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, PIPE_UNLIMITED_INSTANCES, 65536, 65536, 0, &sa)
        };
        if h == INVALID_HANDLE_VALUE {
            log::line(format!("service: CreateNamedPipe failed: {}", std::io::Error::last_os_error()));
            std::thread::sleep(std::time::Duration::from_secs(2));
            continue;
        }
        let ok = unsafe { ConnectNamedPipe(h, null_mut()) } != 0 || unsafe { GetLastError() } == ERROR_PIPE_CONNECTED;
        if !ok {
            unsafe { CloseHandle(h) };
            continue;
        }
        let conn = PipeConn(h);
        std::thread::spawn(move || client(conn));
    }
    #[allow(unreachable_code)]
    unsafe {
        LocalFree(sd as _);
    }
}

/// Starts the core and the pipe server (background threads).
fn start_all() {
    log::line(format!("service: starting (pid {}), data in {}", std::process::id(), gnosis::user_dirs().data.display()));
    gnosis::AUTO_CHECK.store(false, std::sync::atomic::Ordering::SeqCst);
    if let Err(e) = gnosis::start() {
        log::line(format!("service: core start failed: {e}"));
    }
    std::thread::Builder::new().name("pipe".into()).spawn(serve_pipe).ok();
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
