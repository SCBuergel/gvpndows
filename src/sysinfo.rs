//! Facts about the machine that later milestones depend on: Windows build, CPU architecture
//! (x64 or ARM64, also when the x64 .exe runs emulated), admin rights.

pub fn report() -> Vec<String> {
    let mut v = vec![format!(
        "gvpndows {} (commit {}), built for {}-{}",
        env!("CARGO_PKG_VERSION"),
        option_env!("GVPNDOWS_COMMIT").unwrap_or("unknown"),
        std::env::consts::ARCH,
        std::env::consts::OS
    )];
    v.extend(platform());
    v
}

#[cfg(not(windows))]
fn platform() -> Vec<String> {
    vec!["not Windows: no platform details".into()]
}

#[cfg(windows)]
fn platform() -> Vec<String> {
    use windows_sys::Win32::System::Registry::HKEY_LOCAL_MACHINE;
    const KEY: &str = r"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    let s = |name| reg_string(HKEY_LOCAL_MACHINE, KEY, name).unwrap_or_else(|| "?".into());
    let ubr = reg_dword(HKEY_LOCAL_MACHINE, KEY, "UBR").map(|u| u.to_string()).unwrap_or_else(|| "?".into());
    let (process, native) = machines();
    vec![
        format!(
            "windows: {} {} build {}.{} (edition id {})",
            s("ProductName"),
            s("DisplayVersion"),
            s("CurrentBuildNumber"),
            ubr,
            s("EditionID")
        ),
        format!("cpu: native {native}, this process {process}"),
        format!("admin (elevated): {}", is_admin()),
        format!("wine: {}", is_wine()),
    ]
}

#[cfg(windows)]
fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

#[cfg(windows)]
fn reg_string(root: windows_sys::Win32::System::Registry::HKEY, key: &str, name: &str) -> Option<String> {
    use windows_sys::Win32::System::Registry::{RegGetValueW, RRF_RT_REG_SZ};
    let (k, n) = (wide(key), wide(name));
    let mut buf = [0u16; 256];
    let mut size = std::mem::size_of_val(&buf) as u32;
    let rc = unsafe {
        RegGetValueW(root, k.as_ptr(), n.as_ptr(), RRF_RT_REG_SZ, std::ptr::null_mut(), buf.as_mut_ptr().cast(), &mut size)
    };
    if rc != 0 {
        return None;
    }
    let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    Some(String::from_utf16_lossy(&buf[..len]))
}

#[cfg(windows)]
fn reg_dword(root: windows_sys::Win32::System::Registry::HKEY, key: &str, name: &str) -> Option<u32> {
    use windows_sys::Win32::System::Registry::{RegGetValueW, RRF_RT_REG_DWORD};
    let (k, n) = (wide(key), wide(name));
    let mut val: u32 = 0;
    let mut size = 4u32;
    let rc = unsafe {
        RegGetValueW(root, k.as_ptr(), n.as_ptr(), RRF_RT_REG_DWORD, std::ptr::null_mut(), (&mut val as *mut u32).cast(), &mut size)
    };
    (rc == 0).then_some(val)
}

#[cfg(windows)]
fn machines() -> (String, String) {
    use windows_sys::Win32::System::Threading::{GetCurrentProcess, IsWow64Process2};
    let name = |m: u16| match m {
        0 => "native (not WOW64)".to_string(),
        0x8664 => "x64".to_string(),
        0xAA64 => "ARM64".to_string(),
        0x014c => "x86".to_string(),
        other => format!("machine 0x{other:04x}"),
    };
    let (mut p, mut n) = (0u16, 0u16);
    let ok = unsafe { IsWow64Process2(GetCurrentProcess(), &mut p, &mut n) };
    if ok == 0 {
        return ("?".into(), "? (IsWow64Process2 failed)".into());
    }
    // An x64 process emulated on ARM64 is not WOW64, so p is 0; report the build arch then.
    let process = if p == 0 { std::env::consts::ARCH.to_string() } else { name(p) };
    (process, name(n))
}

#[cfg(windows)]
fn is_admin() -> bool {
    unsafe { windows_sys::Win32::UI::Shell::IsUserAnAdmin() != 0 }
}

#[cfg(windows)]
fn is_wine() -> bool {
    use windows_sys::Win32::System::LibraryLoader::{GetModuleHandleA, GetProcAddress};
    unsafe {
        let ntdll = GetModuleHandleA(b"ntdll.dll\0".as_ptr());
        !ntdll.is_null() && GetProcAddress(ntdll, b"wine_get_version\0".as_ptr()).is_some()
    }
}

/// Shows a blocking error box (used when the window itself cannot start).
pub fn fatal_box(text: &str) {
    #[cfg(windows)]
    unsafe {
        use windows_sys::Win32::UI::WindowsAndMessaging::{MessageBoxW, MB_ICONERROR, MB_OK};
        let (t, c) = (wide(text), wide("gvpndows"));
        MessageBoxW(std::ptr::null_mut(), t.as_ptr(), c.as_ptr(), MB_OK | MB_ICONERROR);
    }
    #[cfg(not(windows))]
    eprintln!("{text}");
}
