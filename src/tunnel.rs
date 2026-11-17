//! The one interface the UI talks to. Backends: plain WireGuard (Windows, milestone 2), a stub
//! (other systems), later the Gnosis VPN core.

use std::sync::mpsc::{channel, Receiver, RecvTimeoutError, Sender};
use std::thread;
use std::time::Duration;

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Status {
    Disconnected,
    Connecting,
    Connected { detail: String },
    Disconnecting,
    Failed(String),
}

impl Status {
    pub fn label(&self) -> String {
        match self {
            Status::Disconnected => "Disconnected".into(),
            Status::Connecting => "Connecting…".into(),
            Status::Connected { detail } => format!("Connected ({detail})"),
            Status::Disconnecting => "Disconnecting…".into(),
            Status::Failed(e) => format!("Failed: {e}"),
        }
    }
}

pub struct ConnectParams {
    pub config: crate::wgconf::WgConfig,
    pub killswitch: bool,
}

pub trait Tunnel: Send {
    fn name(&self) -> &'static str;
    /// Starts connecting; status changes arrive through the `Sender` given at creation.
    fn connect(&mut self, params: ConnectParams);
    fn disconnect(&mut self);
    /// Blocking teardown when the app exits.
    fn shutdown(&mut self) {}
}

/// Stub backend: pretends to connect after a short delay. Proves the UI, threading and log path.
#[allow(dead_code)] // used on non-Windows builds only
pub struct StubTunnel {
    events: Sender<Status>,
    cancel: Option<Sender<()>>,
}

#[allow(dead_code)]
impl StubTunnel {
    pub fn new(events: Sender<Status>) -> Self {
        StubTunnel { events, cancel: None }
    }
}

/// The backend for this platform, and the channel its status changes arrive on.
pub fn platform_backend() -> (Box<dyn Tunnel>, Receiver<Status>) {
    let (tx, rx) = channel();
    #[cfg(windows)]
    let t: Box<dyn Tunnel> = Box::new(crate::win::tunnel::WgTunnel::new(tx));
    #[cfg(not(windows))]
    let t: Box<dyn Tunnel> = Box::new(StubTunnel::new(tx));
    (t, rx)
}

impl Tunnel for StubTunnel {
    fn name(&self) -> &'static str {
        "stub"
    }

    fn connect(&mut self, _params: ConnectParams) {
        let events = self.events.clone();
        let (cancel_tx, cancel_rx) = channel::<()>();
        self.cancel = Some(cancel_tx);
        thread::spawn(move || {
            let _ = events.send(Status::Connecting);
            crate::log::line("stub: connecting (simulated, 2 s)");
            // Ok or Disconnected means cancelled; Timeout means the simulated connect finished.
            if matches!(cancel_rx.recv_timeout(Duration::from_secs(2)), Err(RecvTimeoutError::Timeout)) {
                crate::log::line("stub: connected (no traffic is routed)");
                let _ = events.send(Status::Connected { detail: "stub, no tunnel".into() });
            }
        });
    }

    fn disconnect(&mut self) {
        if let Some(c) = self.cancel.take() {
            let _ = c.send(());
        }
        let _ = self.events.send(Status::Disconnecting);
        crate::log::line("stub: disconnected");
        let _ = self.events.send(Status::Disconnected);
    }
}
