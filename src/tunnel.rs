//! The one interface the UI talks to. Milestone 1 ships a stub backend; later milestones add
//! plain WireGuard (Wintun + neptun) and the Gnosis VPN core behind the same trait.

use std::sync::mpsc::{channel, Receiver, Sender};
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

pub trait Tunnel: Send {
    fn name(&self) -> &'static str;
    /// Starts connecting; status changes arrive through the `Sender` given at creation.
    fn connect(&mut self);
    fn disconnect(&mut self);
}

/// Stub backend: pretends to connect after a short delay. Proves the UI, threading and log path.
pub struct StubTunnel {
    events: Sender<Status>,
    cancel: Option<Sender<()>>,
}

impl StubTunnel {
    pub fn new() -> (Self, Receiver<Status>) {
        let (tx, rx) = channel();
        (StubTunnel { events: tx, cancel: None }, rx)
    }
}

impl Tunnel for StubTunnel {
    fn name(&self) -> &'static str {
        "stub"
    }

    fn connect(&mut self) {
        let events = self.events.clone();
        let (cancel_tx, cancel_rx) = channel::<()>();
        self.cancel = Some(cancel_tx);
        thread::spawn(move || {
            let _ = events.send(Status::Connecting);
            crate::log::line("stub: connecting (simulated, 2 s)");
            // recv_timeout returns Ok or Disconnected when cancelled, Timeout when not.
            if cancel_rx.recv_timeout(Duration::from_secs(2)).is_err_and(|e| e.is_timeout()) {
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
