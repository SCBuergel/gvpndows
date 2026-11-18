using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CommunityToolkit.WinUI.Controls;
using GnosisVpn.Engine;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using static GnosisVpn.Engine.FakeEngine;
using static GnosisVpn.Ui.Kit;

namespace GnosisVpn.Ui;

/// <summary>The pages in the NavigationView (homeHTML, locationsHTML, balanceHTML, directHTML,
/// settingsHTML, trafficHTML, demoHTML in the prototype's src/windows/ui.js).</summary>
public sealed class Pages
{
    readonly MainWindow w;
    FakeEngine G => w.G;
    State S => w.G.S;
    public Pages(MainWindow w) { this.w = w; }

    void Act(string a, string? id = null, string? v = null, string? k = null) => G.Act(a, id, v, k);
    string StatusTxt() => S.Conn switch { "idle" => "Not connected", "connecting" => "Connecting", "connected" => "Connected", "reconnecting" => "Reconnecting", _ => "Paused" };

    /// <summary>Everything a page shows except live values; the page is rebuilt when this changes.</summary>
    public string Signature(string route)
    {
        var sb = new StringBuilder(route).Append('|');
        void A(object? o) => sb.Append(o).Append(';');
        A(S.Conn); A(S.Pause); A(S.Loc); A(S.Mode); A(Mb(S.Balance)); A(S.Simple); A(S.Details); A(S.Icons); A(S.Checking); A(S.SwapAnim); A(G.Warm);
        A(S.AutoRenew); A(S.Lockdown); A(S.LdOffPay); A(S.Pay != null); A(S.Popover); A(S.ArPast4); A(S.StartConnect); A(S.Free); A(S.Topup);
        A(S.Q); A(S.Ptab); A(S.Pall); A(S.FullMsg?.Id); A(S.Update); A(S.Better); A(w.U.MoreMsgs); A(w.U.Mode); A(w.U.KeepTray); A(w.U.StartWin); A(w.U.StartMin);
        A(w.U.CtlTab); A(S.Speed); A(G.ExOrNull(S.Loc)?.Ping);
        foreach (var kv in S.Alloc.OrderBy(k => k.Key)) A(kv.Key + "=" + (kv.Key == S.Loc ? (kv.Value > 0 ? (kv.Value <= LOW ? "low" + Math.Round(kv.Value) : "y") : "0") : Math.Round(kv.Value).ToString()));
        foreach (var e in G.EX) A(e.Full + "" + e.Offline);
        foreach (var r in S.Recent) A(r.Id);
        foreach (var m in Messages.Moments(G)) A(m.Lead + m.Text + m.Btns?.Count + m.Own);
        if (route == "demo") { A(S.Notes.Count); A(string.Join(",", S.Fired.Keys)); A(Messages.ScreenCode(G).Code); A(S.NextFull); A(S.FailRenew); A(S.Warm); A(w.U.Codes); A(w.U.Rules); A(S.ArIntro); }
        if (route == "home" && S.Conn == "connecting") A((int)Math.Min(3, Math.Floor((Now - S.CStart) / Math.Max(1, S.CDur) * 4)));
        if (route == "direct" || route == "balance") A(Now - S.CopiedAt < 2200);
        return sb.ToString();
    }

    public UIElement Build(string route) => route switch
    {
        "home" => Home(),
        "locations" => Locations(),
        "balance" => Balance(),
        "direct" => Direct(),
        "settings" => Settings(),
        "traffic" => Traffic.Build(w),
        "demo" => Demo.Build(w),
        _ => Home(),
    };

    StackPanel Page(params UIElement?[] kids)
    {
        var p = V(12, kids);
        p.Padding = w.PaneMode == "x" ? T(40, 20, 40, 32) : T(24, 20, 24, 32);
        p.MaxWidth = 1060; p.HorizontalAlignment = HorizontalAlignment.Left;
        return p;
    }
    static UIElement Section(string title) { var t = Sub2(title); t.Margin = T(0, 16, 0, 0); return t; }
    static StackPanel Grp(params UIElement[] kids) { var p = V(4, kids); p.Margin = T(0, -4, 0, 0); return p; }

    // =============================== HOME ===============================
    string StatusSub()
    {
        var e = G.Ex(S.Loc);
        return S.Conn switch
        {
            "idle" => S.Lockdown && !S.LdOffPay ? "Lockdown is on: nothing leaves this PC until you connect." : "Your traffic goes direct, outside Gnosis VPN.",
            "connecting" => $"Setting up a route to {e.City}.",
            "connected" => $"Through {e.City} · rotating relays · ",
            "reconnecting" => "Finding a new relay…",
            _ => S.Lockdown ? "Traffic stays blocked, so nothing leaks." : "No traffic goes through the VPN.",
        };
    }

    FrameworkElement StatusHeader()
    {
        var c = S.Conn;
        var icon = c switch { "idle" => S.Lockdown ? "shield" : "shield_off", "connecting" or "reconnecting" => "autorenew", "connected" => "shield_ok", _ => "shield_off" };
        var (bg, fg) = c switch
        {
            "connected" => ((Brush)Theme.Mix("sync", 0.22), "sync-hi"),
            "connecting" or "reconnecting" => (Theme.Br("ib-warn"), "sev-warn"),
            "paused" => (Theme.Br("ib-bad"), "sev-bad"),
            _ => (Theme.Br("sel"), "text-2"),
        };
        var ic = Icons.Icon(icon, 28, Theme.Br(fg), true);
        if (c is "connecting" or "reconnecting") Anim.Spin(ic);
        var circle = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Background = bg, Child = ic };
        var sub = new TextBlock { FontSize = 14, Foreground = Theme.Br("text-2"), TextWrapping = TextWrapping.WrapWholeWords };
        sub.Inlines.Add(new Run { Text = StatusSub() });
        if (c == "connected") sub.Inlines.Add(w.Live("timer", new Run { FontFamily = Theme.Mono }));
        var txt = V(2, Title(StatusTxt()), sub);
        txt.VerticalAlignment = VerticalAlignment.Center;
        var row = HS(16, circle, txt);
        row.Padding = T(0, 4, 0, 8);
        return row;
    }

    FrameworkElement LocationCard()
    {
        var e = G.Ex(S.Loc); var c = S.Conn; bool simple = S.Simple, chk = S.Checking && c == "idle";
        UIElement? label = c == "paused" ? Pill("Last location", "history", "bad")
            : c == "connected" ? (simple ? null : Pill("Current location", "check", "ok"))
            : S.Mode == "auto" ? Pill("Current Best Location", "auto_awesome") : Pill("Selected Location", "push_pin", "sel");
        var name = new TextBlock { FontSize = 16, Foreground = TextBrush, TextWrapping = TextWrapping.NoWrap };
        name.Inlines.Add(new Run { Text = e.Country });
        if (!simple) name.Inlines.Add(new Run { Text = " #" + Xid(e.Id), FontFamily = Theme.Mono, FontSize = 12.8, Foreground = Theme.Br("text-2") });
        var ql = HS(6, Bars(G, e, chk), chk ? Txt("Checking…", 12, "text-2") : QLine(G, e));
        var col = V(2, name, ql);
        if (label != null) { label.SetValue(FrameworkElement.MarginProperty, T(0, 4, 0, 0)); col.Children.Add(label); }
        // a clickable card (.sc.click.loc): icon, name and quality, "Change ›" on the right
        var grid = new Grid { ColumnSpacing = 16, MinHeight = 60 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (ExitIcons.For(e.Id, S.Icons, true) is FrameworkElement xi) grid.Children.Add(xi);
        col.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(col, 1); grid.Children.Add(col);
        var chg = HS(8, Txt("Change", 14, "text-2"), Icons.Icon("chevron_right", 16, Theme.Br("text-2"))); chg.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(chg, 2); grid.Children.Add(chg);
        var card = new Button
        {
            Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = T(16, 12, 16, 12), MinHeight = 84, CornerRadius = new CornerRadius(6),
            Background = Theme.Br("card"), BorderBrush = Theme.Br("card-bd"), BorderThickness = new Thickness(1),
        };
        if (S.SwapAnim && !Anim.Off) Anim.Pulse(card, 1, .4, .3);
        card.Click += (_, _) => Act("pick");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, $"Change location, now {e.Country}");
        return card;
    }

    FrameworkElement ConnectingStage()
    {
        var e = G.Ex(S.Loc);
        var prog = Math.Min(1, (Now - S.CStart) / Math.Max(1, S.CDur)); var st = (int)Math.Min(3, Math.Floor(prog * 4));
        string[] steps = { "Finding relays", "One hop, a new relay per packet", "Securing the tunnel", $"Reaching the exit in {e.City}" };
        var dots = HS(4);
        for (int i = 0; i < 4; i++)
        {
            var d = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Theme.Br(i <= st ? "sync" : "track") };
            if (i == st) Anim.Pulse(d, 1, 0.3, 0.45);
            dots.Children.Add(d);
        }
        var cap = new Grid { ColumnSpacing = 10 };
        cap.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cap.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t1 = Txt(steps[st], 13); var t2 = Txt("4 relays in use", 13, "text-2", wrap: false);
        dots.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(t1, 1); Grid.SetColumn(t2, 2);
        cap.Children.Add(dots); cap.Children.Add(t1); cap.Children.Add(t2);
        var stage = V(8, ConnectAnim.Build(w), cap, w.Live("cprog", Bar(0, "accent")));
        return Box(stage, pad: 14);
    }

    UIElement Actions()
    {
        var c = S.Conn; var a = G.Left();
        bool blocked = !(a > 0) && !S.Free && S.Balance < COST;
        var row = new CommunityToolkit.WinUI.Controls.WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8, Padding = T(0, 4, 0, 4) };
        if (c == "idle")
            row.Children.Add(S.Topup || (blocked && S.LdOffPay && S.Pay != null) ? Btn(G.ConnLbl, null, "accent lg", "power_settings_new", disabled: true)
                : blocked ? Btn("Add data", () => Act("addData"), "accent lg", "add") : Btn(G.ConnLbl, () => Act("connect"), "accent lg", "power_settings_new"));
        else if (c == "connecting") row.Children.Add(Btn("Disconnect", () => Act("cancel"), "lg", "power_settings_new"));
        else if (c is "connected" or "reconnecting") row.Children.Add(Btn("Disconnect", () => Act("disconnect"), "lg", "power_settings_new"));
        else
        {
            row.Children.Add(S.Pause switch
            {
                "nodata" => Btn("Renew and reconnect · 600 MB", () => Act("renew"), "accent lg", "autorenew"),
                "offline" => Btn("Connect to best available · 600 MB", () => Act("bestAvail"), "accent lg", "travel_explore"),
                "balance" => S.Lockdown ? Btn("Turn off Lockdown and pay", () => Act("payOut"), "accent lg", "lock_open") : Btn("Add data", () => Act("addData"), "accent lg", "add"),
                _ => Btn("Retry now", () => Act("renew"), "accent lg", "refresh"),
            });
            row.Children.Add(Btn("Turn off VPN", () => Act("offAsk"), "lg"));
        }
        return row;
    }

    static readonly Dictionary<string, int> LOAD = new() { ["nl"] = 24, ["at"] = 31, ["de"] = 46, ["pt"] = 38, ["ch"] = 88, ["se"] = 20, ["br"] = 30 };

    UIElement? Details()
    {
        var c = S.Conn; var a = G.Left(); var p = S.Pause; var e = G.Ex(S.Loc);
        if (S.Simple || c == "connecting") return null;
        FrameworkElement Kv(string k, UIElement v) => Box(V(2, Cap(k), v), pad: 12);
        TextBlock Val(string s, string? color = null) => Mono(s, 16, color, true);
        var kvs = new List<FrameworkElement>();
        if (c == "paused")
        {
            if (p == "offline") { kvs.Add(Kv("Exit", Val("Offline", "sev-bad"))); kvs.Add(Kv("Data lost", Val(Math.Round(S.Lost) + " MB"))); }
            else if (p == "renewfail") { kvs.Add(Kv("Renewal", Val("Failed", "sev-bad"))); kvs.Add(Kv("Next try in", w.Live("retry", Val("")))); }
            else { kvs.Add(Kv("This session", Val("0 MB", "sev-bad"))); kvs.Add(Kv(p == "balance" ? "Balance" : "Traffic", Val(p == "balance" ? Mb(S.Balance) : "Blocked", "sev-bad"))); }
        }
        else
        {
            var ping = Val(c == "reconnecting" ? "—" : e.Ping.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " ms");
            if (c != "reconnecting") ping.Foreground = PingBrush(e.Ping);
            kvs.Add(Kv("Ping", ping));
            kvs.Add(a > 0 || c == "connected" ? Kv("This session", w.Live("left", Val("", a <= LOW ? "sev-warn" : null))) : Kv("Sets aside", Val(S.Free ? "600 MB free" : "600 MB")));
            kvs.Add(Kv("Server load", Val((LOAD.TryGetValue(e.Id, out var l) ? l : 40) + "%")));
            kvs.Add(Kv("Reliability", Val(REL[e.Rel])));
        }
        var grid = new VariableSizedWrapGridLike(kvs, 120, 4);
        var col = V(12, grid);
        if (c != "paused")
        {
            var exp = new Expander
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsExpanded = S.Details,
                Header = HS(16, Icons.Icon("monitoring", 20), V(2, Txt("Diagnostics", 14), Cap("Path, relays and route of this exit"))),
            };
            ((StackPanel)exp.Header).Padding = T(0, 10, 0, 10);
            var dl = V(0);
            void Dl(string k, UIElement v) { var g = new Grid { Padding = T(0, 6, 0, 6), BorderBrush = Theme.Br("stroke"), BorderThickness = T(0, 0, 0, 1) }; var t = Txt(k, 13, "text-2"); g.Children.Add(t); v.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right); g.Children.Add(v); dl.Children.Add(g); }
            Dl("Last checked", w.Live("chk", Mono("", 13, bold: true)));
            Dl("Path", Mono((e.Rel == 3 ? 99 : e.Rel == 2 ? 95 : 61) + "%", 13, bold: true));
            Dl("Relays", Txt($"{e.Rel} of 3", 13, bold: true));
            Dl("Route", Txt("1 hop", 13, bold: true));
            Dl("Open spots", Txt(e.Spots > 0 ? "Yes" : "No", 13, bold: true));
            dl.Padding = T(36, 0, 0, 0);
            exp.Content = dl;
            exp.Expanding += (_, _) => { if (!S.Details) Act("details"); };
            exp.Collapsed += (_, _) => { if (S.Details) Act("details"); };
            col.Children.Add(exp);
        }
        if (a > 0 && G.IsOn) col.Children.Add(Box(Usage("Usage")));
        return col;
    }

    UIElement Usage(string cap)
    {
        var head = new Grid();
        head.Children.Add(Cap(cap));
        var v = w.Live("left", Mono("", 16, bold: true)); v.HorizontalAlignment = HorizontalAlignment.Right; head.Children.Add(v);
        var meter = w.Live("meter", Bar(Math.Min(100, G.Left() / 6), G.Left() <= LOW ? "sev-warn" : "sync", 6));
        var tip = G.IsOn ? $"Session expiry in {Dur((int)Math.Max(0, Math.Round((10800 - S.ConnSec) / 60)))}. Relay channels close after 3 h, because keeping them open costs data. Connecting again opens new ones." : "Your session starts when you connect.";
        var info = Tip(new Border { Child = Icons.Icon("info", 16, Theme.Br("text-2")) }, tip);
        return V(8, head, meter, HS(6, w.Live("tleft", Txt("", 12, "text-2")), info));
    }

    UIElement Home()
    {
        var c = S.Conn; bool simple = S.Simple;
        var main = V(12, StatusHeader(), LocationCard());
        var mid = V(8);
        if (c == "connecting") mid.Children.Add(ConnectingStage());
        else
        {
            var all = Messages.Moments(G); int lim = w.U.MoreMsgs ? 9 : 2;
            foreach (var m in all.Take(lim)) mid.Children.Add(Info(simple ? Messages.SimpleLine(G, m) : m, a => Act(a), k => Act("dismissMsg", k: k), w.LiveText));
            if (all.Count > lim) mid.Children.Add(Btn($"Show {all.Count - lim} more", () => { w.U.MoreMsgs = !w.U.MoreMsgs; w.Render(); }, "hyper sm", "expand_more"));
            if (S.LdOffPay) mid.Children.Add(Info(new Moment("warn", "", "Lockdown is off", "since you paid.", new() { new("Turn Lockdown back on", "lockOn"), new("Not now", "ldNot") }), a => Act(a), null));
        }
        if (mid.Children.Count > 0) main.Children.Add(mid);
        main.Children.Add(Actions());
        if (Details() is UIElement det) main.Children.Add(det);
        main.MaxWidth = 600;

        bool two = w.ContentWidth >= 900;
        var outer = new Grid { ColumnSpacing = 24, Padding = w.PaneMode == "m" ? T(20, 20, 20, 28) : T(32, 20, 32, 28) };
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(two ? 1.1 : 1, GridUnitType.Star), MaxWidth = 600 });
        outer.Children.Add(main);
        if (two)
        {
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 460 });
            var hh = new Grid();
            hh.Children.Add(Sub("Locations"));
            var all = Btn("All locations", () => Act("pick"), "hyper sm", trail: "chevron_right"); all.HorizontalAlignment = HorizontalAlignment.Right; hh.Children.Add(all);
            var list = Box(V(12, hh, Search(false), Selector(false), Rows(true)));
            list.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(list, 1);
            outer.Children.Add(list);
        }
        outer.HorizontalAlignment = HorizontalAlignment.Left;
        return outer;
    }

    // =============================== LOCATIONS ===============================
    AutoSuggestBox? search;
    public void FocusSearch() => w.DispatcherQueue.TryEnqueue(() => search?.Focus(FocusState.Programmatic));

    AutoSuggestBox Search(bool wide)
    {
        var b = new AutoSuggestBox { PlaceholderText = wide ? "Search countries  (Ctrl+F)" : "Search countries", QueryIcon = new SymbolIcon(Symbol.Find), Text = S.Q, VerticalAlignment = VerticalAlignment.Center };
        if (wide) { b.MaxWidth = 360; b.MinWidth = 280; }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, "Search locations");
        b.TextChanged += (s, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            S.Q = b.Text;
            // keep the box (and its focus); only rebuild the rows
            if (rowsHost != null) { rowsHost.Child = wide && tableHost ? Table() : Rows(!wide); }
        };
        search = b;
        return b;
    }
    Border? rowsHost; bool tableHost;

    FrameworkElement Selector(bool withAlpha)
    {
        var opts = new List<(string v, string l, string? tip)> { ("best", "Best", TIPS["fast"]), ("rel", "Reliable", TIPS["rel"]) };
        if (withAlpha) opts.Add(("alpha", "A–Z", TIPS["alpha"]));
        opts.Add(("recent", "Recent", TIPS["recent"]));
        return Kit.Selector(opts, S.Ptab, v => Act("ptab", v: v));
    }

    (List<Exit> list, bool more, Exit? b, string q) ExitList(bool shortList)
    {
        var q = S.Q.Trim().ToLowerInvariant(); var b = G.Best();
        var all = G.EX.Where(x => (Usable(x) || x.Offline || (S.FullMsg != null && S.FullMsg.Id == x.Id)) && (q != "" || x.Id != S.Loc || S.Ptab == "recent")).ToList();
        List<Exit> list = q != "" ? all.Where(x => x.City.ToLowerInvariant().Contains(q) || x.Country.ToLowerInvariant().Contains(q) || x.Place.ToLowerInvariant().Contains(q)).ToList()
            : S.Ptab == "recent" ? S.Recent.Select(r => G.ExOrNull(r.Id)).Where(x => x != null && all.Contains(x)).Cast<Exit>().ToList()
            : S.Ptab == "rel" ? all.OrderByDescending(x => x.Rel).ThenBy(x => x.Ping).ToList()
            : S.Ptab == "alpha" ? all.OrderBy(x => x.Country, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("en"), false)).ToList()
            : all.OrderByDescending(x => G.GradeOf(x).Sc ?? -2).ToList();
        if (S.Simple) list = list.Where(x => (!x.Offline || x.Id == S.Loc) && !(q == "" && S.Ptab == "best" && b != null && x.Id == b.Id)).ToList();
        var more = q == "" && !S.Pall && list.Count > (shortList ? 6 : 99);
        if (more) list = list.Take(6).ToList();
        return (list, more, b, q);
    }

    UIElement RowAction(Exit x, bool withPrice = true)
    {
        bool on = G.IsOn || S.Conn == "paused", cur = x.Id == S.Loc; var a = G.Left(x.Id);
        if (x.Offline) return Pill("Offline", tone: "bad");
        if (x.Full) return Pill("Full", tone: "bad");
        if (cur && on) return Pill(S.Conn == "paused" ? "Paused" : "Connected", tone: "ok");
        var row = HS(10);
        if (withPrice) row.Children.Add(new TextBlock { Text = a > 0 ? Math.Round(a) + " MB left" : S.Free && !on ? "free" : "600 MB", FontFamily = Theme.Mono, FontSize = 11, Foreground = Theme.Br("text-2"), VerticalAlignment = VerticalAlignment.Center });
        var id = x.Id;
        row.Children.Add(Btn(on ? "Switch" : "Connect", () => Act("pick2", id: id), "sm"));
        return row;
    }

    UIElement? HoldPill(string id)
    {
        var a = G.Left(id); if (!(a > 0) || id == S.Loc) return null;
        var mins = Dur((int)Math.Round(G.HeldLeft(id) / 60));
        var ring = new ProgressRing { IsIndeterminate = false, Value = Math.Min(100, a / COST * 100), Width = 12, Height = 12, MinWidth = 12, MinHeight = 12, Foreground = Theme.Br("sev-info") };
        var p = new Border { Height = 18, Padding = T(4, 0, 6, 0), CornerRadius = new CornerRadius(9), Background = Theme.Br("ib-lav"), Child = HS(4, ring, new TextBlock { Text = $"{Math.Round(a)} MB · {mins}", FontFamily = Theme.Mono, FontSize = 11, Foreground = Theme.Br("sev-info"), VerticalAlignment = VerticalAlignment.Center }) };
        return Tip(p, $"Data you left here. It stays for {mins}, then it is gone.");
    }

    FrameworkElement Row(UIElement? icon, UIElement title, UIElement sub, UIElement action, bool auto = false)
    {
        var g = new Grid { ColumnSpacing = 12, MinHeight = 56, Padding = T(12, 8, 10, 8), CornerRadius = new CornerRadius(4) };
        if (auto) { g.Background = Theme.Br("sel"); g.Margin = T(0, 0, 0, 4); }
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (icon != null) g.Children.Add(icon);
        var t = V(3, title, sub); t.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(t, 1); g.Children.Add(t);
        action.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); Grid.SetColumn((FrameworkElement)action, 2); g.Children.Add(action);
        if (!auto)
        {
            g.PointerEntered += (_, _) => g.Background = Theme.Br("subtle-h");
            g.PointerExited += (_, _) => g.Background = null;
        }
        return g;
    }

    TextBlock ExitName(Exit x)
    {
        var t = new TextBlock { FontSize = 14, Foreground = TextBrush, TextWrapping = TextWrapping.NoWrap };
        t.Inlines.Add(new Run { Text = x.Country });
        if (!S.Simple) t.Inlines.Add(new Run { Text = " #" + Xid(x.Id), FontFamily = Theme.Mono, FontSize = 11.2, Foreground = Theme.Br("text-2") });
        return t;
    }

    FrameworkElement AutoRow(Exit? b)
    {
        bool on = G.IsOn;
        var title = Txt(S.Simple ? (b != null ? b.Country : "Best available") : "Auto · best available", 14, wrap: false);
        var sub = new WrapPanelLike(6);
        sub.Add(Pill("Current Best Location", "auto_awesome"));
        if (S.Simple) { if (b != null) { sub.Add(Bars(G, b)); sub.Add(Txt(G.GradeOf(b).Label, 12, "text-2", wrap: false)); } }
        else sub.Add(Txt("Picks fast, reliable exits" + (b != null ? " · " + b.Country + " now" : ""), 12, "text-2"));
        UIElement act = S.Mode == "auto" && b != null && b.Id == S.Loc && (on || S.Conn == "idle")
            ? Pill(on ? "On" : "Selected", "check", "ok")
            : HS(10, new TextBlock { Text = "600 MB", FontFamily = Theme.Mono, FontSize = 11, Foreground = Theme.Br("text-2"), VerticalAlignment = VerticalAlignment.Center }, Btn(on ? "Switch" : "Connect", () => Act("auto"), "sm accent"));
        return Row(ExitIcons.For("auto", S.Icons), title, sub.Panel, act, auto: true);
    }

    UIElement? FullMsgFor(Exit x)
    {
        if (S.FullMsg == null || S.FullMsg.Id != x.Id) return null;
        bool on = G.IsOn || S.Conn == "paused";
        var alt = S.FullMsg.Alt;
        var btns = alt != null ? new List<Btn> { new($"{(on ? "Switch" : "Connect")} to {G.Ex(alt).Country} · 600 MB", "pick2") } : null;
        return Info(new Moment("warn", "", $"{x.City} just filled up.", alt != null ? $"Next most reliable: {G.Ex(alt).City}." : "", btns), a => Act(a, id: alt), null);
    }

    UIElement RowsList(bool shortList)
    {
        var (list, more, b, q) = ExitList(shortList);
        var lv = V(2);
        if (q == "" && S.Ptab == "best") lv.Children.Add(AutoRow(b));
        foreach (var x in list)
        {
            var rr = S.Recent.FirstOrDefault(y => y.Id == x.Id);
            var sub = new WrapPanelLike(6);
            if (G.Warm && x.Id != b?.Id) { sub.Add(Bars(G, x, true)); sub.Add(Txt("Measuring…", 12, "text-2", wrap: false)); }
            else
            {
                sub.Add(Bars(G, x));
                sub.Add(x.Full ? Txt("Full", 12, "sev-bad", wrap: false) : QLine(G, x));
                if (HoldPill(x.Id) is UIElement hp) sub.Add(hp);
                if (S.Ptab == "recent" && rr != null) sub.Add(Txt("· used " + FmtT(rr.At), 12, "text-3", wrap: false));
            }
            lv.Children.Add(Row(ExitIcons.For(x.Id, S.Icons), ExitName(x), sub.Panel, RowAction(x)));
            if (FullMsgFor(x) is UIElement fm) lv.Children.Add(fm);
        }
        if (list.Count == 0) { var em = Txt(q != "" ? $"No exit matches “{S.Q}”." : S.Ptab == "recent" ? "No exits used yet" : "", 14, "text-2"); em.Padding = T(12, 24, 12, 24); em.HorizontalAlignment = HorizontalAlignment.Center; lv.Children.Add(em); }
        if (more) lv.Children.Add(Btn("Show all exits", () => Act("pall"), "hyper sm", "expand_more"));
        return lv;
    }

    UIElement Rows(bool shortList)
    {
        rowsHost = new Border { Child = RowsList(shortList) };
        tableHost = false;
        return rowsHost;
    }

    UIElement Table()
    {
        var (list, _, b, q) = ExitList(false);
        var outer = V(8);
        if (q == "" && S.Ptab == "best") outer.Children.Add(AutoRow(b));
        var cols = new[] { new GridLength(2, GridUnitType.Star), new GridLength(1.2, GridUnitType.Star), new GridLength(64), new GridLength(44), new GridLength(44), new GridLength(1, GridUnitType.Star), new GridLength(72), new GridLength(92) };
        Grid TR(bool head)
        {
            var g = new Grid { ColumnSpacing = 8, MinHeight = head ? 36 : 48, Padding = T(12, 4, 12, 4), BorderBrush = Theme.Br("stroke"), BorderThickness = head ? new Thickness(0) : T(0, 1, 0, 0) };
            if (head) g.Background = Theme.Br("subtle-p");
            foreach (var c in cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = c, MinWidth = c.IsStar ? (c.Value == 2 ? 150 : c.Value == 1.2 ? 104 : 84) : 0 });
            return g;
        }
        void Put(Grid g, int col, UIElement el) { el.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); Grid.SetColumn((FrameworkElement)el, col); g.Children.Add(el); }
        UIElement Th(string label, string? sortV)
        {
            var t = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.Br("text-2") };
            if (sortV == null) return t;
            var row = HS(4, t); if (S.Ptab == sortV) row.Children.Add(Icons.Icon("expand_more", 16, Theme.Br("text-2")));
            var bt = new Button { Content = row, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = T(4, 0, 4, 0), Height = 28, Margin = T(-4, 0, 0, 0) };
            bt.Click += (_, _) => Act("ptab", v: sortV);
            return bt;
        }
        var tbl = V(0);
        var hr = TR(true);
        Put(hr, 0, Th("Exit", "alpha")); Put(hr, 1, Th("Quality", "best")); Put(hr, 2, Th("Latency", null)); Put(hr, 3, Th("Load", null)); Put(hr, 4, Th("Path", null)); Put(hr, 5, Th("Reliability", "rel")); Put(hr, 6, Th("Data", null));
        tbl.Children.Add(hr);
        foreach (var x in list)
        {
            var r = TR(false); var load = x.Slots > 0 ? (int?)Math.Round((1 - x.Spots / (double)x.Slots) * 100) : null; var a = G.Left(x.Id);
            var name = new WrapPanelLike(6); name.Add(new TextBlock { Text = x.Country, FontWeight = FontWeights.SemiBold, FontSize = 13 }); name.Add(Mono("#" + Xid(x.Id), 11, "text-2")); if (HoldPill(x.Id) is UIElement hp) name.Add(hp);
            var ex = HS(10); if (ExitIcons.For(x.Id, S.Icons) is FrameworkElement xi) ex.Children.Add(xi); ex.Children.Add(name.Panel);
            Put(r, 0, ex);
            var ql = HS(6); if (G.Warm && x.Id != b?.Id) { ql.Children.Add(Bars(G, x, true)); ql.Children.Add(Txt("Measuring…", 13, "text-2", wrap: false)); } else { ql.Children.Add(Bars(G, x)); var qt = x.Full ? Txt("Full", 13, "sev-bad", wrap: false) : QLine(G, x); qt.FontSize = 13; ql.Children.Add(qt); }
            Put(r, 1, ql);
            Put(r, 2, Mono(x.Measured ? x.Ping.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " ms" : "—", 13));
            Put(r, 3, Mono(x.Measured && load != null ? load + "%" : "—", 13));
            Put(r, 4, Mono(x.Pv > 0 ? Math.Round(x.Pv * 100) + "%" : "—", 13));
            Put(r, 5, Txt(REL[x.Rel], 13));
            Put(r, 6, Mono(a > 0 ? Math.Round(a) + " MB left" : "600 MB", 13));
            var act = RowAction(x, false); act.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right); Put(r, 7, act);
            r.PointerEntered += (_, _) => r.Background = Theme.Br("subtle-h"); r.PointerExited += (_, _) => r.Background = null;
            tbl.Children.Add(r);
            if (FullMsgFor(x) is UIElement fm) { var mb = new Border { Padding = T(12, 0, 12, 10), Child = fm }; tbl.Children.Add(mb); }
        }
        if (list.Count == 0) { var em = Txt(q != "" ? $"No exit matches “{S.Q}”." : S.Ptab == "recent" ? "No exits used yet" : "", 14, "text-2"); em.Padding = T(12, 24, 12, 24); em.HorizontalAlignment = HorizontalAlignment.Center; tbl.Children.Add(em); }
        var box = new Border { Background = Theme.Br("card"), BorderBrush = Theme.Br("card-bd"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = tbl };
        outer.Children.Add(box);
        return outer;
    }

    UIElement Locations()
    {
        int nFull = G.EX.Count(e => e.Full && !(S.FullMsg != null && S.FullMsg.Id == e.Id));
        bool table = !S.Simple && w.ContentWidth >= 760;
        var cmd = HS(16, Search(true), Selector(!S.Simple));
        rowsHost = new Border { Child = table ? Table() : RowsList(false) };
        tableHost = table;
        var foot = Txt(S.Ptab == "recent" ? "Your last 6 exits · data left there is used first" : $"Each new exit sets aside 600 MB · {nFull} full exits hidden", 12, "text-2");
        foot.Margin = T(4, 12, 4, 12);
        return Page(Title("Locations"), cmd, rowsHost, foot);
    }

    // =============================== BALANCE, SEND DIRECTLY ===============================
    UIElement Addr()
    {
        bool copied = Now - S.CopiedAt < 2200;
        var addr = G.Real?.NodeAddress is { Length: > 10 } ra ? ra : ADDRESS;
        var v = new TextBlock { FontFamily = Theme.Mono, FontSize = 13, Foreground = Theme.Br("text-2"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        v.Inlines.Add(new Run { Text = addr[..6], FontWeight = FontWeights.SemiBold, Foreground = TextBrush });
        v.Inlines.Add(new Run { Text = addr[6..^4] });
        v.Inlines.Add(new Run { Text = addr[^4..], FontWeight = FontWeights.SemiBold, Foreground = TextBrush });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(v, "Address " + addr);
        var b = Btn(copied ? "Copied" : "Copy", () =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage(); dp.SetText(addr);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            Act("copy");
            w.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { });
        }, "sm", copied ? "check" : "content_copy");
        if (copied && b is Button bb) bb.Background = Theme.Br("ib-ok");
        var wp = new WrapPanelLike(10); wp.Add(v); wp.Add(b); wp.Panel.VerticalSpacing = 8;
        return wp.Panel;
    }

    static string FundUrl => FUND_URL;
    InfoBar WaitBar() => Info(new Moment("info", "", "Waiting for your payment.", "Usually about 2 minutes.", null), _ => { }, null);

    UIElement Balance()
    {
        var e = G.Ex(S.Loc); var a = G.Left();
        var bal = Box(V(8, Cap("Available balance"), Mono(Mb(S.Balance), 40, bold: true), Txt(G.UsePhrase(), 14, "text-2")));
        var p = Page(Title("Balance"), bal);
        if (S.Balance <= COST) p.Children.Add(Info(new Moment(S.Balance < COST ? "bad" : "warn", "", S.Balance < COST ? "Balance empty:" : "Balance running low:", S.Balance < COST ? "no renewals left." : "1 renewal left.", null), _ => { }, null));
        if (S.Pay != null) p.Children.Add(WaitBar());
        p.Children.Add(Section("Add data"));
        var fund = Btn("Open funding site", () => Act("funding"), "accent", trail: "open_in_new");
        p.Children.Add(Grp(
            Card("account_balance_wallet", "Pay with crypto", "Top up your balance with crypto you have left in a wallet. The funding site walks you through it.", fund),
            Card("qr_code_2", "Send funds directly", "Your VPN address as a QR code, and what to send on Gnosis Chain", null, () => Act("gateDirect")),
            Card("key", "Your VPN address", "Paste it on the funding site", Addr())));
        p.Children.Add(Cap("Your balance updates about 2 minutes after paying. More payment methods are planned."));
        if (G.IsOn || a > 0) { p.Children.Add(Section("This exit")); p.Children.Add(Box(Usage(e.City))); }
        return p;
    }

    UIElement Direct()
    {
        bool ok = S.Balance >= COST;
        UIElement head = S.Gate ? Title("Send funds directly") : HS(6, Btn("Balance", () => Act("back"), "subtle"), Icons.Icon("chevron_right", 16, Theme.Br("text-2")), Title("Send funds directly"));
        if (!S.Gate) { var hb = (StackPanel)head; ((ButtonBase)hb.Children[0]).Content = new TextBlock { Text = "Balance", FontSize = 28, FontWeight = FontWeights.SemiBold, FontFamily = Theme.Display, Foreground = Theme.Br("text-2") }; hb.VerticalAlignment = VerticalAlignment.Center; }
        var qr = new Border { Width = 196, Padding = new Thickness(10), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Microsoft.UI.Colors.White), Child = new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "qr.svg"))), Width = 176, Height = 176 } };
        var qrCol = V(8, qr, Cap("Your VPN address"), Addr()); qrCol.HorizontalAlignment = HorizontalAlignment.Center;
        foreach (var ch in qrCol.Children) ch.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        var left = Box(qrCol);
        UIElement St() => ok ? Pill("Sufficient", tone: "ok") : Pill("Needed");
        var right = V(12,
            Info(new Moment("info", "", "", "", null), _ => { }, null),
            Sub("What to send"),
            Grp(Card(null, "wxHOPR", "Pays for your traffic", HS(8, Mono("234.52", 14, bold: true), St())), Card(null, "xDAI", "Pays network fees", HS(8, Mono("0.0126", 14, bold: true), St()))),
            S.Pay != null ? WaitBar() : Cap("Your balance updates by itself, usually within 2 minutes of sending."),
            Btn("Funding site", () => Act("funding"), "hyper", trail: "open_in_new"));
        // the "Gnosis Chain only" note, with the network name in bold
        var ib = (InfoBar)right.Children[0]; ib.Title = ""; ib.Message = "";
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = T(0, 0, 0, 12) }; note.Inlines.Add(new Run { Text = "Send on " }); note.Inlines.Add(new Run { Text = "Gnosis Chain", FontWeight = FontWeights.SemiBold }); note.Inlines.Add(new Run { Text = " only. Other networks won’t arrive." });
        ib.Content = note;
        var grid = new Grid { ColumnSpacing = 16 };
        bool stack = w.PaneMode == "m" || S.Gate && w.AppWindow.Size.Width < 700;
        if (stack) { var v = V(16, left, right); grid.Children.Add(v); }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            left.VerticalAlignment = VerticalAlignment.Top; grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right);
        }
        return Page(head, grid);
    }

    public UIElement BalanceFlyout()
    {
        var a = G.Left();
        var p = V(8, Cap("Balance"), Mono(Mb(S.Balance), 28, bold: true), Txt(G.UsePhrase(), 13, "text-2"));
        if (a > 0)
        {
            var g = new Grid(); g.Children.Add(Txt($"Left at {G.Ex(S.Loc).City}", 13, "text-2"));
            var v = w.Live("left", Mono("", 13, bold: true)); v.HorizontalAlignment = HorizontalAlignment.Right; g.Children.Add(v);
            p.Children.Add(g);
        }
        p.Children.Add(HS(8, Btn("Add data", () => { S.Sheet = null; Act("addData"); }, "accent"), Btn("Usage details", () => { S.Sheet = null; Act("usage"); }, "hyper")));
        p.Width = 268;
        return p;
    }

    // =============================== SETTINGS ===============================
    UIElement Settings()
    {
        var e = G.Ex(S.Loc);
        var p = Page(Title("Settings"));
        p.Children.Add(Section("Protection"));
        bool ld = S.Lockdown && !S.LdOffPay;
        p.Children.Add(Grp(
            Card("power_settings_new", "Connect when the app starts", null, Toggle(S.StartConnect, () => Act("tog", k: "startConnect"), "Connect when the app starts")),
            Card(ld ? "lock" : "lock_open", "Lockdown", "Block all traffic whenever Gnosis VPN is not connected. Gnosis VPN sets this up in the Windows firewall.", Toggle(ld, () => Act("lockTap"), "Lockdown"))));
        p.Children.Add(Cap("Together, nothing leaves this PC before Gnosis VPN is connected."));
        p.Children.Add(Section("Startup and notification area"));
        p.Children.Add(Grp(
            Card("startup", "Start with Windows", "Gnosis VPN opens when you sign in.", Toggle(w.U.StartWin, () => { w.U.StartWin = !w.U.StartWin; Startup.Apply(w.U); w.Render(); }, "Start with Windows")),
            Card("tray", "Start in the notification area", "Opens without a window. Find it next to the clock.", Toggle(w.U.StartMin, () => { w.U.StartMin = !w.U.StartMin; Startup.Apply(w.U); w.Render(); }, "Start in the notification area", !w.U.StartWin), disabled: !w.U.StartWin),
            Card("window", "Keep running when the window is closed", "Closing the window leaves the VPN connected in the notification area.", Toggle(w.U.KeepTray, () => { w.U.KeepTray = !w.U.KeepTray; w.Render(); }, "Keep running when the window is closed"))));
        p.Children.Add(Section("Renewing"));
        p.Children.Add(Grp(
            Card("visibility_off", "Hide data use before connecting", "Connect goes straight ahead instead of first showing the 600 MB an exit takes.", Toggle(!S.Popover, () => Act("togHide"), "Hide data use before connecting")),
            Card("autorenew", "Auto-renew sessions", "Adds the next 600 MB by itself when an exit runs low, without asking.", Toggle(S.AutoRenew, () => Act("togAR"), "Auto-renew sessions")),
            Card("all_inclusive", "Auto-renew past 4 cycles", "After four renewals in a row, keeps going without checking in with you.", Toggle(S.ArPast4, () => Act("togP4"), "Auto-renew past 4 cycles", !S.AutoRenew), disabled: !S.AutoRenew)));
        p.Children.Add(Section("App"));
        var app = Grp();
        if (S.Update != null) app.Children.Add(Card("system_update", $"Update to {UPD_V}", "Ready to install. Restarting takes a few seconds.", Btn("Restart now", () => Act("updNow"), "accent"), bg: "ib-lav"));
        app.Children.Add(Card("location", "Preferred exit", S.Mode == "auto" ? "Auto · best" : e.City, Btn("Change", () => Act("pick"))));
        app.Children.Add(Card("monitoring", "Detailed metrics", "Ping, route, diagnostics, the exit table and live traffic.", Toggle(!S.Simple, () => Act("view"), "Detailed metrics")));
        app.Children.Add(Card("person", "Exit icons", null, Combo(new[] { ("node", "Node icon"), ("flag", "Flags"), ("none", "None") }, S.Icons, v => Act("icons", v: v))));
        app.Children.Add(Card("public", "App theme", null, Combo(new[] { ("system", "Use system setting"), ("light", "Light"), ("dark", "Dark") }, w.U.Mode, v => { w.U.Mode = v; Prefs.Save("mode", v); w.Render(); })));
        p.Children.Add(app);
        p.Children.Add(Section("Logs, for support"));
        p.Children.Add(Grp(
            Card("download", "Export logs", "Save them to a file.", Btn("Export…", () => Act("logsExport"))),
            Card("upload", "Upload logs", "Sent to support. Check what’s included first.", Btn("Upload…", () => { w.U.Dialog = "upload"; w.Render(); }))));
        p.Children.Add(Section("About"));
        p.Children.Add(Grp(
            Card("vpn_key", "Gnosis VPN", "Version 1.3.2 · UI prototype", HS(4, Btn("Docs", () => Act("docs"), "hyper", trail: "open_in_new"), Btn("Support", () => Act("support"), "hyper", trail: "open_in_new"))),
            Card("beaker", "Demo controls", "Scenarios, network events, time speed, notification rules", null, () => w.NavTo("demo"))));
        return p;
    }
}

/// <summary>A wrapping row (flex-wrap: wrap) of small items.</summary>
public sealed class WrapPanelLike
{
    public readonly CommunityToolkit.WinUI.Controls.WrapPanel Panel;
    public WrapPanelLike(double gap) { Panel = new CommunityToolkit.WinUI.Controls.WrapPanel { HorizontalSpacing = gap, VerticalSpacing = 4 }; }
    public void Add(UIElement el) { el.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); Panel.Children.Add(el); }
}

/// <summary>The stat cards: an auto-fit grid of cards at least minWidth wide (grid auto-fit in CSS).</summary>
public sealed class VariableSizedWrapGridLike : Grid
{
    public VariableSizedWrapGridLike(List<FrameworkElement> items, double minWidth, double gap)
    {
        ColumnSpacing = gap; RowSpacing = gap;
        SizeChanged += (_, e) => Layout(items, minWidth, e.NewSize.Width);
        Layout(items, minWidth, 600 - 2 * gap);
    }
    void Layout(List<FrameworkElement> items, double minWidth, double width)
    {
        int cols = Math.Max(1, (int)Math.Floor((width + ColumnSpacing) / (minWidth + ColumnSpacing)));
        if (ColumnDefinitions.Count == cols && Children.Count == items.Count) return;
        Children.Clear(); ColumnDefinitions.Clear(); RowDefinitions.Clear();
        for (int c = 0; c < cols; c++) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < (items.Count + cols - 1) / cols; r++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < items.Count; i++) { SetColumn(items[i], i % cols); SetRow(items[i], i / cols); Children.Add(items[i]); }
    }
}
