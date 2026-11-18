using System;
using System.Linq;
using GnosisVpn.Engine;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static GnosisVpn.Engine.FakeEngine;
using static GnosisVpn.Ui.Kit;

namespace GnosisVpn.Ui;

/// <summary>Demo controls (debug builds): scenario presets, settings, balance, session, network
/// events, time speed, the Windows section, and the notification log (ctlHTML / ntfHTML).</summary>
public static class Demo
{
    static Button Chip(string label, bool on, Action click, string? code = null)
    {
        var row = HS(6);
        if (on) row.Children.Add(Icons.Icon("check", 14, Theme.Br("on-accent")));
        if (code != null) row.Children.Add(new TextBlock { Text = code, FontFamily = Theme.Mono, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Theme.Br(on ? "on-accent" : "text-2"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Theme.Br(on ? "on-accent" : "text"), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = row, Height = 30, Padding = T(10, 0, 10, 0) };
        if (on) { b.Background = Theme.Br("accent"); b.BorderThickness = new Thickness(0); }
        b.Click += (_, _) => click();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, label);
        return b;
    }
    static UIElement Chips(params UIElement[] kids) { var wp = new WrapPanelLike(6); foreach (var k in kids) wp.Add(k); wp.Panel.Margin = T(0, 0, 0, 6); return wp.Panel; }
    static TextBlock H3(string s) { var t = new TextBlock { Text = s.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, CharacterSpacing = 80, Foreground = Theme.Br("text-2"), Margin = T(0, 0, 0, 8) }; return t; }
    static TextBlock Tiny(string s) => Txt(s, 12, "text-2");

    public static UIElement Build(MainWindow w)
    {
        var G = w.G; var S = G.S; var U = w.U;
        var tabs = Kit.Selector(new[] { ("scenario", "Scenario", (string?)null), ("ntf", "Notifications", null) }, U.CtlTab, v => { U.CtlTab = v; w.Render(); });
        var title = HS(8, Title("Demo controls"), Pill("Prototype")); ((FrameworkElement)title.Children[1]).VerticalAlignment = VerticalAlignment.Center;
        var page = V(12, title, tabs, U.CtlTab == "scenario" ? Scenario(w) : Log(w));
        page.Padding = w.PaneMode == "x" ? T(40, 20, 40, 32) : T(24, 20, 24, 32); page.MaxWidth = 1060; page.HorizontalAlignment = HorizontalAlignment.Left;
        return page;
    }

    static UIElement Scenario(MainWindow w)
    {
        var G = w.G; var S = G.S; var U = w.U;
        void A(string a, string? v = null, string? k = null) => G.Act(a, v: v, k: k);
        Button Pre(string k, string label, string code) => Chip(label, false, () => A("preset", k), code);
        Button Tg(string k, string label) => Chip(label, G.Get(k), () => A("tgl", k: k));
        var (code, name) = Messages.ScreenCode(G, U.Fly, U.WinState);
        var now = Box(V(4, Txt("On screen", 12, "text-2"), Rich(code, name)), "card", 12, 10);
        var p = V(14, now);
        p.Children.Add(V(0, H3("Jump to"), Chips(
            Chip("Fund first", S.Gate, () => A("gateRestart"), "I-1"), Chip("Syncing", S.SyncT != null, () => A("restart"), "I0"), Chip("Failure", S.Fail != null, () => A("failShow"), "E1"),
            Pre("s00", "Fresh start", "S00"), Pre("s01", "Connected", "S01"), Pre("s07", "Data low", "S07"), Pre("s08", "No data left", "S08"),
            Pre("s10", "Exit offline", "S10"), Pre("s13", "Balance low", "S13"), Pre("s14", "Balance empty", "S14"), Pre("s18c", "Waiting for payment", "S18c"), Pre("l1", "Connection drops", "L1"), Pre("arc", "Auto-renew check-in", "S09")),
            Tiny("Each jump keeps the notification log, so you can compare what fired.")));
        p.Children.Add(V(0, H3("Settings"), Chips(Tg("autoRenew", "Auto-renew"), Tg("topup", "Top-up message"), Tg("warm", "Fresh launch (measuring)"), Tg("popover", "Show data use before connecting"), Tg("lockdown", "Lockdown"), Tg("startConnect", "Connect at start")),
            Chips(Chip(S.ArIntro ? "Auto-renew intro: seen · show again" : "Auto-renew intro: armed", !S.ArIntro, () => A("arReset"))),
            Tiny("The intro shows once per install, the next time 600 MB gets set aside with Auto-renew on and data use shown.")));
        p.Children.Add(V(0, H3("Balance"), Chips(new[] { 4800, 1200, 600, 0 }.Select(v => (UIElement)Chip(Mb(v), S.Balance == v, () => A("bal", v.ToString()))).ToArray())));
        p.Children.Add(V(0, H3("This session"), Chips(new[] { 600, 300, 120, 60, 20, 5 }.Select(v => (UIElement)Chip(v + " MB", false, () => A("sess", v.ToString()))).ToArray()),
            Tiny($"Sets what’s left of the current 600 MB at this exit (connects if needed). Runs out at {RATE} MB/s, faster with ×10 / ×60. Turn Auto-renew off to see the prompts.")));
        p.Children.Add(V(0, H3("Network events"), Chips(Chip("Exit goes offline", false, () => A("evOffline")), Chip("Connection drops", false, () => A("evDrop")), Chip("Payment arrives", false, () => A("evPay")), Chip("Update available", false, () => A("evUpdate")), Chip("Two messages", false, () => A("evTwo"))),
            Chips(Tg("nextFull", "Next pick fills up"), Tg("failRenew", "Renewals fail"))));
        var timeH = HS(8, H3("Time"), w.Live("clock", Mono("", 13)));
        p.Children.Add(V(0, timeH, Chips(new[] { 1, 10, 60 }.Select(v => (UIElement)Chip("×" + v, S.Speed == v, () => A("speed", v.ToString()))).Append(Chip("+5 min", false, () => A("skip"))).ToArray()),
            Tiny("No better-exit suggestions while connected. Auto refreshes the best location every minute while idle.")));
        p.Children.Add(V(0, H3("Windows"),
            Chips(new[] { ("system", "System"), ("light", "Light"), ("dark", "Dark") }.Select(m => (UIElement)Chip(m.Item2, U.Mode == m.Item1, () => { U.Mode = m.Item1; Prefs.Save("mode", m.Item1); w.Render(); })).ToArray()),
            Chips(new[] { ("wide", "Wide 1180"), ("std", "Standard 960"), ("compact", "Compact 720"), ("narrow", "Narrow 520"), ("max", "Maximized") }.Select(z => (UIElement)Chip(z.Item2, w.WinPreset() == z.Item1, () => w.SetWin(z.Item1))).ToArray()),
            Chips(Chip("Detailed metrics", !S.Simple, () => A("view")), Chip("Screen codes in window", U.Codes, () => { U.Codes = !U.Codes; w.Render(); }), Chip("Rule notes in Notification Center", U.Rules, () => { U.Rules = !U.Rules; w.Render(); })),
            Chips(Chip("Close window to tray", U.WinState == "closed", () => w.Close()), Chip("Quit app", false, () => w.Quit())),
            Chips(Chip("Reset everything", false, () => A("reset")), Chip("Clear notification log", false, () => A("clearLog")))));
        return p;
    }

    static UIElement Log(MainWindow w)
    {
        var G = w.G; var S = G.S;
        var p = V(10, Tiny("What Windows shows as a toast at the moment the app sends it, and the rule behind it. Missed toasts wait in Notification Center: click the clock in the taskbar."));
        var logs = V(8);
        foreach (var n in S.Notes)
        {
            var r = RULES.FirstOrDefault(x => x.K == n.Rule); var code = n.Code ?? r?.Code.Split(" · ").Last() ?? "";
            var meta = HS(8, Mono(n.Silent ? r?.Code ?? "" : n.Proposed ? "Proposed" : code, 11, "accent-t", true), Mono(FmtT(n.At), 11, "text-2"));
            if (n.Gone) meta.Children.Add(Txt("dismissed", 11, "text-2"));
            var body = V(2, meta, Txt(n.Silent ? "No notification · " + n.Title : n.Title, 13, bold: true), Txt(n.Body, 12));
            if (!n.Silent && r != null) body.Children.Add(Txt("Why: " + r.Text, 12, "text-2"));
            var box = Box(body, "card", 12, 10); if (n.Silent) { box.Opacity = 0.7; box.Background = null; }
            logs.Children.Add(box);
        }
        if (S.Notes.Count == 0) logs.Children.Add(Box(Tiny("Nothing sent yet. With Auto-renew on and enough balance the app stays quiet. Try Data low or Balance low in Jump to, or run time at ×60."), "card", 12, 10));
        p.Children.Add(logs);
        p.Children.Add(Sub2("When notifications show up"));
        var tbl = V(0);
        void Row(string c1, string name, string rule, string st, string stColor)
        {
            var g = new Grid { ColumnSpacing = 8, Padding = T(4, 6, 4, 6), BorderBrush = Theme.Br("card-bd"), BorderThickness = T(0, 1, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Mono(c1, 10, "text-2"));
            var mid = V(0, Txt(name, 12), Txt(rule, 11, "text-2")); Grid.SetColumn(mid, 1); g.Children.Add(mid);
            var s = Txt(st, 11, stColor, true, false); Grid.SetColumn(s, 2); g.Children.Add(s);
            tbl.Children.Add(g);
        }
        foreach (var r in RULES)
        {
            var keys = S.Fired.Keys.Where(k => k.Split(':')[0] == r.K).ToList();
            var (st, col) = (r.K is "u3" or "a3") && S.AutoRenew ? ("Muted · Auto-renew on", "text-2") : r.Silent ? ("Silent", "text-2") : keys.Count > 0 ? ("Sent " + FmtT(S.Fired[keys[^1]]), "sev-ok") : ("Armed", "accent-t");
            Row(r.Code, r.Name, r.Text, st, col);
        }
        Row("Tray", "Notification-area icon, always", "Status at a glance; left click opens the flyout, right click the menu. Takes the place of Android’s ongoing notification.", w.U.WinState == "quit" ? "Hidden" : "Shown", "sev-ok");
        Row("In-app", "Lockdown reconnecting, better exit, renewal failed", "Main screen only. No system notification on the canvas.", "App only", "text-2");
        p.Children.Add(tbl);
        return p;
    }
}
