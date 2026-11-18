using System;
using System.Collections.Generic;
using System.Linq;
using static GnosisVpn.Engine.FakeEngine;

namespace GnosisVpn.Engine;

/// <summary>A message on the Home page (an InfoBar). Text may contain the live token {retry}.</summary>
public sealed record Moment(string Tone, string Icon, string Lead, string Text, List<Btn>? Btns, bool Own = false)
{
    public Moment WithOwn(bool own) => this with { Own = own };
}

/// <summary>Port of the message logic in the prototype's src/shared/common.js: moment(), moments(),
/// simpleLine() and screenCode().</summary>
public static class Messages
{
    public static readonly Dictionary<string, string> ToneIcon = new() { ["ok"] = "check_circle", ["warn"] = "warning", ["bad"] = "pause_circle", ["info"] = "info", ["lav"] = "system_update" };
    static List<Btn> B(params (string l, string a)[] x) => x.Select(t => new Btn(t.l, t.a)).ToList();

    public static Moment? Moment(FakeEngine g)
    {
        var S = g.S; var e = g.Ex(S.Loc); var a = g.Left();
        if (S.Conn == "paused")
        {
            if (S.Pause == "nodata") return new("bad", "pause_circle", "Internet paused.", $"No data left at {e.City}. Your traffic stays private while paused.", null);
            if (S.Pause == "offline") return new("bad", "cloud_off", $"{e.City} went offline.", $"The {Math.Round(S.Lost)} MB left there is lost, even if {e.City} comes back.", null);
            if (S.Pause == "balance") return new("bad", "pause_circle", "Internet paused.", S.Lockdown ? $"No data at {e.City} and none in your balance. Paying needs the VPN and Lockdown off for a moment." : $"No data at {e.City} and none in your balance. Add data to continue.", null);
            if (S.Pause == "renewfail") return new("bad", "sync_problem", "Couldn’t renew.", "Trying again in {retry}. Traffic stays blocked meanwhile, so nothing leaks.", null);
        }
        if (S.Conn == "reconnecting") return S.Lockdown
            ? new("lav", "lock", "Lockdown on.", $"Internet is blocked while Gnosis VPN reconnects to {e.City}.", null)
            : new("warn", "warning", "Reconnecting.", "Lockdown is off, so traffic isn’t protected until the connection is back.", null);
        if (S.Conn == "connecting") return new("info", "info", $"Connecting to {e.City}.", a > 0 ? $"Uses the {Math.Round(a)} MB left here." : S.Free ? $"Setting aside your free 600 MB at {e.City}." : $"Setting aside 600 MB at {e.City}.", null);
        if (S.Flash != null) return new(S.Flash.Tone, ToneIcon.GetValueOrDefault(S.Flash.Tone, "info"), S.Flash.Lead, S.Flash.Text, S.Flash.Btns.Count > 0 ? S.Flash.Btns : null);
        if (S.Conn == "idle" && S.LdOffPay && S.Pay != null) return new("info", "hourglass_top", "Waiting for payment…", "Usually about 2 minutes. Connect becomes available once it arrives.", null);
        if (S.Conn == "connected")
        {
            if (a <= LOW && !S.AutoRenew) return new("warn", "warning", $"{Math.Round(a)} MB left here.", $"About {Dur(g.MinsLeft())} at this rate.", B(("Renew here · 600 MB", "renew")));
            if (a <= LOW && S.Balance < COST) return new("warn", "warning", $"{Math.Round(a)} MB left here.", "Your balance is empty, so the internet pauses when this runs out.", B(("Add data", "addData")));
            if (S.Balance > 0 && S.Balance <= COST) return new("warn", "warning", "Balance running low.", $"{Mb(S.Balance)} left, enough for one more renewal.", B(("Add data", "addData")));
            if (S.Better != null)
            {
                var b = g.Ex(S.Better);
                return new("info", "info", $"Better exit now: {b.City}.", $"{b.Ping} ms, faster than {e.City}.", B((g.Left(b.Id) > 0 ? "Switch" : "Switch · 600 MB", "switchBetter"), ("Not now", "notNow")));
            }
            return null;
        }
        if (S.Conn == "idle")
        {
            if (S.Checking) return new("info", "radar", "Checking exits…", "Auto keeps the best location up to date until you connect.", null);
            if (S.Balance < COST && !(a > 0)) return new("warn", "warning", "Balance empty.", "Add data to connect again.", B(("Add data", "addData")));
            if (S.Mode == "manual") return new("info", "push_pin", "Selected Location.", "Your pick stays until you change it.", null);
        }
        return null;
    }

    public static List<Moment> Moments(FakeEngine g)
    {
        var S = g.S;
        var outp = new List<Moment>(); var seen = new HashSet<string>();
        void Add(Moment? m, bool own) { if (m != null && seen.Add(m.Lead)) outp.Add(m.WithOwn(own)); }
        if (S.Topup) Add(new("info", "account_balance_wallet", "Please fund your VPN.", "", B(("Fund", "fundScreen"))), true);
        Add(Moment(g), S.Flash != null);
        if (S.Flash != null) { var keep = S.Flash; S.Flash = null; Add(Moment(g), false); S.Flash = keep; }
        foreach (var x in S.Fq) Add(new(x.Tone, ToneIcon.GetValueOrDefault(x.Tone, "info"), x.Lead, x.Text, x.Btns.Count > 0 ? x.Btns : null), true);
        if (outp.Count > 0 && S.Flash != null) outp[0] = outp[0].WithOwn(outp[0].Lead == S.Flash.Lead || outp[0].Lead == "Please fund your VPN.");
        int Rank(string t) => t == "bad" ? 0 : t == "warn" ? 1 : 2;
        return outp.Select((m, i) => (m, i)).OrderBy(x => Rank(x.m.Tone)).ThenBy(x => x.i).Select(x => x.m).ToList();
    }

    /// <summary>Shorter copy for the simple view, as in the demo.</summary>
    public static Moment SimpleLine(FakeEngine g, Moment m)
    {
        var S = g.S; var c = S.Conn; var a = g.Left();
        string lead = m.Lead, text = m.Text; var btns = m.Btns;
        static string First(string t)
        {
            var i = t.IndexOf(". ", StringComparison.Ordinal);
            return i >= 0 ? t[..(i + 1)] : t;
        }
        if (c == "paused" && lead != S.Flash?.Lead)
        {
            text = S.Pause switch { "nodata" => "Your traffic stays private.", "offline" => "The data left there is lost.", "balance" => "Your balance is empty.", "renewfail" => "Next try in {retry}.", _ => text };
            btns = null;
        }
        else if (c == "connected" && a <= LOW && !S.AutoRenew && lead.Contains("MB left")) { lead = "Data running low."; text = $"About {Dur(g.MinsLeft())} left."; }
        else if (lead.StartsWith("Better exit now") && S.Better != null) { lead = $"{g.Ex(S.Better).City} is faster now."; text = ""; }
        else text = First(text);
        if (btns != null) btns = btns.Take(1).ToList();
        return new Moment(m.Tone, m.Icon, lead, text, btns, m.Own);
    }

    /// <summary>Screen code and name for the current state (the prototype's screenCode(), Windows codes included).</summary>
    public static (string Code, string Name) ScreenCode(FakeEngine g, string? platformFly = null, string winState = "open")
    {
        var S = g.S;
        if (S.Fail != null) return ("E1", "Failure page");
        if (S.SyncT != null) return ("I0", "Discovering the network");
        var pg = g.Page;
        if (platformFly == "tray") return ("TRAY", "Notification-area flyout");
        if (platformFly == "ctx") return ("TRAY·M", "Notification-area menu");
        if (platformFly == "nc") return ("NC", "Notification Center");
        if (winState == "closed") return ("BG", "Running in the notification area");
        if (pg == "payout") return ("S18a", "Pay outside the VPN");
        if (S.Sheet != null && !(S.Sheet.Type == "quota" && pg != null))
            return S.Sheet.Type switch
            {
                "quota" => S.Sheet.Check ? ("S09q", "Auto-renew check-in after 4 renewals") : S.Sheet.Intro ? ("S04i", "Auto-renew explained, once per install") : S.Sheet.Renew ? ("S08q", "600 MB used up, ask to renew") : ("S04q", "Data use, Auto-renew off"),
                "switch" => ("S04", "Switch, first time"),
                "balance" => ("—", "Balance flyout"),
                _ => ("S15", "Not enough data to switch"),
            };
        if (pg == "pick") return S.FullMsg != null ? ("S11", "Exit just filled up") : ("S03s", "Change exit");
        if (pg == "settings") return S.Stab == "general" ? ("S19n", "Settings, General") : ("S06n", "Settings, Usage");
        if (pg == "add") return ("S18", "Add data");
        if (pg == "direct") return ("I-1d", "Send funds directly");
        if (pg == "traffic") return ("—", "Live traffic");
        if (pg == "demo") return ("—", "Demo controls");
        var c = S.Conn; var a = g.Left(); var f = S.Flash?.Lead ?? "";
        if (S.Gate) return S.Pay != null ? ("I-1w", "Fund first, waiting for payment") : ("I-1", "Fund first");
        if (c == "connecting") return ("S00b", "Connecting");
        if (c == "reconnecting") return S.Lockdown ? ("L1", "Lockdown, reconnecting") : ("L1*", "Reconnecting, Lockdown off");
        if (c == "paused") return S.OffConfirm ? ("S08c", "Turning the VPN off") : S.Pause switch
        {
            "nodata" => ("S08", "Paused, no data left"), "offline" => ("S10", "Paused, exit went offline"),
            "balance" => ("S14", "Paused, balance empty"), _ => ("S16", "Paused, renewal failed"),
        };
        if (c == "connected")
        {
            if (f.StartsWith("Payment")) return S.AutoRenew ? ("P1", "Paid, connected, Auto-renew on") : ("P2", "Paid, connected, Auto-renew off");
            if (f.StartsWith("Renewed auto")) return ("S05", "Renewed automatically");
            if (f.StartsWith("Back online")) return ("S09", "Back online");
            if (S.LdOffPay) return ("S18d", "Connected, Lockdown still off");
            if (a <= LOW && !S.AutoRenew) return ("S07", "Data low, Auto-renew off");
            if (S.Balance > 0 && S.Balance <= COST) return ("S13", "Balance running low");
            return S.Details && !S.Simple ? ("S02", "Connected, details open") : ("S01", "Connected");
        }
        if (f.StartsWith("Payment")) return S.LdOffPay ? ("S18e", "Payment arrived, Lockdown off") : S.AutoRenew ? ("P3", "Paid, disconnected, Auto-renew on") : ("P4", "Paid, disconnected, Auto-renew off");
        if (S.LdOffPay && S.Pay != null) return ("S18c", "Waiting for payment, Lockdown off");
        if (S.Checking) return ("F3", "Interval passed: checking");
        if (f.Contains("now the best")) return ("F4 → F5", "New best found");
        if (S.Mode == "manual") return ("F1", "You picked an exit");
        return ("F2", "Auto: Current Best Location");
    }
}
