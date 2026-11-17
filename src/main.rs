// No console window on Windows; the log pane and the log file replace stdout.
#![cfg_attr(windows, windows_subsystem = "windows")]

mod diag;
mod log;
mod relay;
mod sysinfo;
mod tunnel;
mod wgconf;
#[cfg(windows)]
mod gnosis;
#[cfg(windows)]
mod win;

use eframe::egui;
use std::sync::mpsc::Receiver;
use std::time::{Duration, Instant};
use tunnel::{ConnectParams, Status, Tunnel};

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

    // --selftest: log the facts above, exercise key handling, exit without a window
    // (used by the build's Wine check).
    if std::env::args().any(|a| a == "--selftest") {
        match wgconf::own_private_key() {
            Ok(k) => log::line(format!("selftest: public key {}", wgconf::public_key_b64(&k))),
            Err(e) => log::line(format!("selftest: key error {e}")),
        }
        match wgconf::WgConfig::parse(&wgconf::TEMPLATE.replace("<server public key>", "v99ZdpUIhbBiQrNjDy1Fu6qzXKoJ8XrWoLf7/DfELEc=")) {
            Ok(_) => log::line("selftest: config parser ok"),
            Err(e) => log::line(format!("selftest: config parser error {e}")),
        }
        #[cfg(windows)]
        if gnosis::backup::available() {
            let r = gnosis::backup::seal(b"{\"selftest\": \"c2VsZnRlc3Q=\"}").and_then(|s| relay::post_text("gvpndows backup selftest", &s));
            log::line(format!("selftest: sealed backup post {:?}", r));
        }
        if relay::available() {
            match relay::selftest() {
                Ok(()) => log::line("selftest: relay post ok"),
                Err(e) => log::line(format!("selftest: relay post failed: {e}")),
            }
        }
        log::line("selftest ok");
        return;
    }

    // --core-test: start the Gnosis VPN core headless, log its progress for 150 s, exit
    // (build check under Wine: identity, Blokli over HTTPS, HOPR node start).
    #[cfg(windows)]
    if std::env::args().any(|a| a == "--core-test") {
        if let Err(e) = gnosis::start() {
            log::line(format!("core-test: start failed: {e}"));
            return;
        }
        for _ in 0..30 {
            std::thread::sleep(std::time::Duration::from_secs(5));
            let mode = gnosis::STATUS.lock().unwrap().as_ref().map(|v| v["data"]["run_mode"]["type"].to_string());
            log::line(format!("core-test: core {:?}, run mode {:?}", gnosis::CORE_STATE.lock().unwrap(), mode));
        }
        gnosis::shutdown();
        log::line("core-test done");
        return;
    }

    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_title("Gnosis VPN (gvpndows test)")
            .with_inner_size([760.0, 640.0])
            .with_min_inner_size([480.0, 400.0]),
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
    public_key: String,
    config_text: String,
    config_note: String,
    killswitch: bool,
    show_config: bool,
    show_wg: bool,
    destination: String,
    autoconnect_at: Option<Instant>,
}

impl App {
    fn new() -> Self {
        let (t, rx) = tunnel::platform_backend();
        log::line(format!("backend: {}", t.name()));
        let public_key = match wgconf::own_private_key() {
            Ok(k) => wgconf::public_key_b64(&k),
            Err(e) => {
                log::line(format!("key error: {e}"));
                format!("(error: {e})")
            }
        };
        log::line(format!("this app's WireGuard public key: {public_key}"));
        // A peer.conf next to the .exe (shipped in the test zip) replaces the saved config, so
        // nothing has to be typed on the laptop.
        let beside_exe = std::env::current_exe().ok().and_then(|p| p.parent().map(|d| d.join("peer.conf")));
        let shipped = beside_exe.as_ref().and_then(|p| std::fs::read_to_string(p).ok().map(|s| (p.clone(), s)));
        let (config_text, config_note) = match shipped {
            Some((p, s)) if wgconf::WgConfig::parse(&s).is_ok() => {
                let _ = std::fs::write(wgconf::config_path(), &s);
                (s, format!("loaded {} (saved)", p.display()))
            }
            _ => match std::fs::read_to_string(wgconf::config_path()) {
                Ok(s) => (s, format!("loaded {}", wgconf::config_path().display())),
                Err(_) => (wgconf::TEMPLATE.to_string(), "no config: put peer.conf next to the .exe".into()),
            },
        };
        log::line(format!("config: {config_note}"));
        // GVPNDOWS_AUTOCONNECT=1 clicks Connect once after start (screenshot check under Wine).
        let auto = std::env::var("GVPNDOWS_AUTOCONNECT").is_ok_and(|v| v == "1");
        let show_config = false;
        // Start the Gnosis VPN core right away (identity, chain, HOPR node take a while).
        #[cfg(windows)]
        if let Err(e) = gnosis::start() {
            log::line(format!("gnosis: not started: {e}"));
        }
        App {
            tunnel: t,
            events: rx,
            status: Status::Disconnected,
            public_key,
            config_text,
            config_note,
            killswitch: false,
            show_config,
            show_wg: false,
            destination: String::new(),
            autoconnect_at: auto.then(|| Instant::now() + Duration::from_secs(1)),
        }
    }

    fn connect(&mut self) {
        match wgconf::WgConfig::parse(&self.config_text) {
            Ok(config) => {
                log::line(format!("user: connect (kill switch {})", if self.killswitch { "on" } else { "off" }));
                self.tunnel.connect(ConnectParams { config, killswitch: self.killswitch });
            }
            Err(e) => {
                log::line(format!("config error: {e}"));
                self.status = Status::Failed(format!("config: {e}"));
                self.show_config = true;
            }
        }
    }
}

#[cfg(windows)]
impl App {
    fn gnosis_panel(&mut self, ui: &mut egui::Ui, ctx: &egui::Context) {
        ui.add_space(6.0);
        ui.heading("Gnosis VPN (prod, 1-hop)");
        let state = gnosis::CORE_STATE.lock().unwrap().clone();
        let status = gnosis::STATUS.lock().unwrap().clone();
        ui.horizontal(|ui| {
            if !gnosis::running() {
                if ui.button("Start core").clicked() {
                    if let Err(e) = gnosis::start() {
                        log::line(format!("gnosis: start failed: {e}"));
                    }
                }
            }
            ui.label(format!("Core: {}", if state.is_empty() { "not started" } else { &state }));
        });
        let Some(v) = status else { return };
        let data = &v["data"];
        let mode = data["run_mode"]["type"].as_str().unwrap_or("?");
        if mode == "preparing_safe" {
            let rm = &data["run_mode"];
            let addr = rm["node_address"].as_str().unwrap_or("?").to_string();
            ui.horizontal(|ui| {
                ui.label("Waiting for funds. Node address:");
                ui.label(egui::RichText::new(&addr).monospace());
                if ui.small_button("Copy").clicked() {
                    ctx.copy_text(addr.clone());
                }
            });
            ui.label(format!(
                "Needs {} wxHOPR and {} xDAI (has {} / {}).",
                wei(&rm["balance_recommendation"]["wxhopr"]),
                wei(&rm["balance_recommendation"]["xdai"]),
                wei(&rm["node_wxhopr"]),
                wei(&rm["node_xdai"])
            ));
            if gnosis::backup::available() && gnosis::backup::identity_exists() {
                if ui.button("Back up node identity to developer (encrypted)").clicked() {
                    gnosis::backup::send();
                }
            }
        } else {
            ui.label(format!("Run mode: {mode}"));
        }
        let dests: Vec<(String, String)> = data["destinations"]
            .as_array()
            .map(|a| {
                a.iter()
                    .map(|d| {
                        let x = &d["destination"];
                        let id = x["id"].as_str().unwrap_or("?").to_string();
                        let loc = x["meta"]["other"]["location"].as_str().unwrap_or("");
                        let label = format!(
                            "{} {} {} ({})",
                            x["meta"]["name"].as_str().unwrap_or(&id),
                            x["geo"]["country"].as_str().unwrap_or(""),
                            loc,
                            d["route_health"]["state"]["type"].as_str().unwrap_or("?")
                        );
                        (id, label)
                    })
                    .collect()
            })
            .unwrap_or_default();
        let conn = data["connection"]["type"].as_str().unwrap_or("?").to_string();
        ui.horizontal(|ui| {
            if self.destination.is_empty() {
                if let Some((id, _)) = dests.iter().find(|(id, _)| id.starts_with("gvpn-ger")).or(dests.first()) {
                    self.destination = id.clone();
                }
            }
            let selected = dests.iter().find(|(id, _)| *id == self.destination).map(|(_, l)| l.clone()).unwrap_or_default();
            egui::ComboBox::from_label("").selected_text(selected).width(360.0).show_ui(ui, |ui| {
                for (id, label) in &dests {
                    ui.selectable_value(&mut self.destination, id.clone(), label);
                }
            });
            if ui.add_enabled(!self.destination.is_empty() && mode == "running", egui::Button::new("Connect")).clicked() {
                log::line(format!("user: gnosis connect {}", self.destination));
                gnosis::connect(&self.destination);
            }
            if ui.button("Disconnect").clicked() {
                log::line("user: gnosis disconnect");
                gnosis::disconnect();
            }
            if ui.button("Check connection").clicked() {
                std::thread::spawn(diag::run_checks);
            }
        });
        ui.label(format!("Connection: {conn} {}", data["connection"]["destination"].as_str().unwrap_or("")));
        ui.add_space(4.0);
    }
}

/// Wei (as JSON string or number) to a decimal token amount with 4 decimals.
#[cfg(windows)]
fn wei(v: &serde_json::Value) -> String {
    let s = match v {
        serde_json::Value::String(s) => s.clone(),
        other => other.to_string(),
    };
    match s.parse::<u128>() {
        Ok(w) => format!("{:.4}", w as f64 / 1e18),
        Err(_) => s,
    }
}

impl eframe::App for App {
    fn on_exit(&mut self, _gl: Option<&eframe::glow::Context>) {
        self.tunnel.shutdown();
        #[cfg(windows)]
        gnosis::shutdown();
    }

    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        while let Ok(s) = self.events.try_recv() {
            self.status = s;
        }
        if self.autoconnect_at.is_some_and(|t| Instant::now() >= t) {
            self.autoconnect_at = None;
            log::line("autoconnect (GVPNDOWS_AUTOCONNECT=1)");
            self.connect();
        }
        let active = !matches!(self.status, Status::Disconnected | Status::Failed(_));

        #[cfg(windows)]
        egui::TopBottomPanel::top("gnosis").show(ctx, |ui| self.gnosis_panel(ui, ctx));

        egui::TopBottomPanel::top("controls").show(ctx, |ui| {
            ui.add_space(4.0);
            ui.toggle_value(&mut self.show_wg, "WireGuard test peer (milestone 2)");
            if !self.show_wg {
                ui.add_space(4.0);
                return;
            }
            ui.add_space(6.0);
            ui.horizontal(|ui| {
                let busy = matches!(self.status, Status::Disconnecting);
                let label = if active { "Disconnect" } else { "Connect" };
                if ui.add_enabled(!busy, egui::Button::new(label).min_size([110.0, 28.0].into())).clicked() {
                    if active {
                        log::line("user: disconnect");
                        self.tunnel.disconnect();
                    } else {
                        self.connect();
                    }
                }
                ui.label(egui::RichText::new(self.status.label()).strong());
            });
            ui.add_space(4.0);
            ui.horizontal(|ui| {
                ui.add_enabled(!active, egui::Checkbox::new(&mut self.killswitch, "Kill switch (block all traffic outside the tunnel)"));
                if ui.button("Check connection").on_hover_text("public IP, DNS resolver, 10 MB download").clicked() {
                    std::thread::spawn(diag::run_checks);
                }
            });
            ui.add_space(4.0);
            ui.horizontal(|ui| {
                ui.label("This app's public key:");
                ui.label(egui::RichText::new(&self.public_key).monospace());
                if ui.small_button("Copy").clicked() {
                    ctx.copy_text(self.public_key.clone());
                    log::line("public key copied to clipboard");
                }
                if relay::available() && ui.button("Send key to developer").on_hover_text("posts the public key to the developer's ntfy.sh topic").clicked() {
                    relay::send_key(self.public_key.clone());
                }
            });
            ui.horizontal(|ui| {
                ui.toggle_value(&mut self.show_config, "WireGuard config");
                ui.label(format!("Log: {}", log::path().display()));
                if ui.small_button("Copy log").clicked() {
                    ctx.copy_text(log::snapshot().join("\n"));
                    log::line("log copied to clipboard");
                }
                if relay::available() && ui.button("Send log to developer").on_hover_text("posts this run's log to the developer's ntfy.sh topic").clicked() {
                    relay::send_log();
                }
            });
            if self.show_config {
                ui.add_enabled(
                    !active,
                    egui::TextEdit::multiline(&mut self.config_text).code_editor().desired_rows(10).desired_width(f32::INFINITY),
                );
                ui.horizontal(|ui| {
                    if ui.add_enabled(!active, egui::Button::new("Save")).clicked() {
                        self.config_note = match wgconf::WgConfig::parse(&self.config_text) {
                            Err(e) => format!("not saved: {e}"),
                            Ok(_) => match std::fs::write(wgconf::config_path(), &self.config_text) {
                                Ok(()) => format!("saved to {}", wgconf::config_path().display()),
                                Err(e) => format!("not saved: {e}"),
                            },
                        };
                        log::line(format!("config: {}", self.config_note));
                    }
                    ui.label(&self.config_note);
                });
            }
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
