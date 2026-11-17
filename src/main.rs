// No console window on Windows; the log pane and the log file replace stdout.
#![cfg_attr(windows, windows_subsystem = "windows")]

mod log;
mod sysinfo;
mod tunnel;

use eframe::egui;
use std::sync::mpsc::Receiver;
use std::time::{Duration, Instant};
use tunnel::{Status, StubTunnel, Tunnel};

fn main() {
    let log_file = log::init();
    std::panic::set_hook(Box::new(|info| {
        log::line(format!("PANIC: {info}"));
    }));
    log::line("---- start ----");
    match &log_file {
        Ok(p) => log::line(format!("log file: {}", p.display())),
        Err(e) => log::line(format!("log file unavailable: {e}")),
    }
    for l in sysinfo::report() {
        log::line(l);
    }

    // --selftest: log the facts above and exit without a window (used by the build's Wine check).
    if std::env::args().any(|a| a == "--selftest") {
        log::line("selftest ok");
        return;
    }

    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_title("Gnosis VPN (gvpndows test)")
            .with_inner_size([640.0, 460.0])
            .with_min_inner_size([420.0, 300.0]),
        ..Default::default()
    };
    if let Err(e) = eframe::run_native("gvpndows", options, Box::new(|_cc| Ok(Box::new(App::new())))) {
        let msg = format!("The window could not start: {e}\n\nLog: {}", log::path().display());
        log::line(&msg);
        sysinfo::fatal_box(&msg);
    }
    log::line("---- exit ----");
}

struct App {
    tunnel: Box<dyn Tunnel>,
    events: Receiver<Status>,
    status: Status,
    autoconnect_at: Option<Instant>,
}

impl App {
    fn new() -> Self {
        let (t, rx) = StubTunnel::new();
        log::line(format!("backend: {}", t.name()));
        // GVPNDOWS_AUTOCONNECT=1 clicks Connect once after start (screenshot check under Wine).
        let auto = std::env::var("GVPNDOWS_AUTOCONNECT").is_ok_and(|v| v == "1");
        App {
            tunnel: Box::new(t),
            events: rx,
            status: Status::Disconnected,
            autoconnect_at: auto.then(|| Instant::now() + Duration::from_secs(1)),
        }
    }
}

impl eframe::App for App {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        while let Ok(s) = self.events.try_recv() {
            self.status = s;
        }
        if self.autoconnect_at.is_some_and(|t| Instant::now() >= t) {
            self.autoconnect_at = None;
            log::line("autoconnect (GVPNDOWS_AUTOCONNECT=1)");
            self.tunnel.connect();
        }

        egui::TopBottomPanel::top("controls").show(ctx, |ui| {
            ui.add_space(6.0);
            ui.horizontal(|ui| {
                let busy = matches!(self.status, Status::Connecting | Status::Disconnecting);
                let connected = matches!(self.status, Status::Connected { .. } | Status::Connecting);
                let label = if connected { "Disconnect" } else { "Connect" };
                if ui.add_enabled(!busy || connected, egui::Button::new(label).min_size([110.0, 28.0].into())).clicked() {
                    if connected {
                        log::line("user: disconnect");
                        self.tunnel.disconnect();
                    } else {
                        log::line("user: connect");
                        self.tunnel.connect();
                    }
                }
                ui.label(egui::RichText::new(self.status.label()).strong());
            });
            ui.add_space(4.0);
            ui.horizontal(|ui| {
                ui.label(format!("Log file: {}", log::path().display()));
                if ui.small_button("Copy log").clicked() {
                    ctx.copy_text(log::snapshot().join("\n"));
                    log::line("log copied to clipboard");
                }
            });
            ui.add_space(4.0);
        });

        egui::CentralPanel::default().show(ctx, |ui| {
            egui::ScrollArea::vertical().stick_to_bottom(true).auto_shrink([false, false]).show(ui, |ui| {
                for l in log::snapshot() {
                    ui.label(egui::RichText::new(l).monospace().size(12.0));
                }
            });
        });

        // Status changes come from other threads; poll a few times per second.
        ctx.request_repaint_after(Duration::from_millis(250));
    }
}
