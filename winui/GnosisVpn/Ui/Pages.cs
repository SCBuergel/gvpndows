using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GnosisVpn.Engine;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using static GnosisVpn.Engine.AppEngine;
using static GnosisVpn.Ui.Kit;

namespace GnosisVpn.Ui;

/// <summary>The pages in the NavigationView: Home, Locations, Balance, Settings.
/// Everything they show about the VPN comes from the Gnosis VPN service.</summary>
public sealed class Pages
{
    readonly MainWindow w;
    AppEngine G => w.G;
    State S => w.G.S;
    RealBackend R => w.G.Real;
    public Pages(MainWindow w) { this.w = w; }

    void Act(string a, string? id = null, string? v = null, string? k = null) => G.Act(a, id, v, k);
    string StatusTxt() => S.Conn switch { "connecting" => "Connecting", "connected" => "Connected", "reconnecting" => "Reconnecting", _ => "Not connected" };

    /// <summary>Everything a page shows except live values; the page is rebuilt when this changes.</summary>
    public string Signature(string route)
    {
        var sb = new StringBuilder(route).Append('|');
        void A(object? o) => sb.Append(o).Append(';');
        A(S.Conn); A(S.Loc); A(S.Mode); A(Mb(S.Balance)); A(S.Simple); A(S.Details); A(S.Icons);
        A(S.Q); A(S.Ptab); A(S.Pall); A(w.U.MoreMsgs); A(w.U.Mode); A(w.U.KeepTray); A(w.U.StartWin); A(w.U.StartMin); A(exporting);
        A(R.NodeAddress); A(R.NeedWx); A(R.NeedXdai); A(R.HaveWx); A(R.HaveXdai); A(R.DebugRelay);
        foreach (var kv in S.Alloc.OrderBy(k => k.Key)) A(kv.Key + "=" + Math.Round(kv.Value / 10));
        foreach (var e in G.EX) A(e.Id + G.GradeOf(e).K + G.GradeOf(e).N + e.Ping / 50);
        foreach (var r in S.Recent) A(r.Id);
        foreach (var m in Messages.Moments(G)) A(m.Lead + m.Text + m.Btns?.Count + m.Own);
        if (route == "home" && S.Conn == "connecting") A((int)Math.Min(3, Math.Floor((Now - S.CStart) / Math.Max(1, S.CDur) * 4)));
        if (route == "balance") A(Now - S.CopiedAt < 2200);
        return sb.ToString();
    }

    public UIElement Build(string route) => route switch
    {
        "home" => Home(),
        "locations" => Locations(),
        "balance" => Balance(),
        "settings" => Settings(),
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
            "connecting" => $"Setting up a route to {e.City}.",
            "reconnecting" => "Finding a new route…",
            _ => "",
        };
    }

    /// <summary>The connected line: when the exit's paid data runs out, how much is left, and that the
    /// next chunk is bought by itself (the core always renews; there is no setting to stop it).</summary>
    public static string Expiry(AppEngine g)
    {
        var parts = new List<string>();
        if (g.S.EmptyIn is double s && s > 0) parts.Add("Expires in " + Dur(s));
        if (g.Left() > 0) parts.Add(Mb(g.Left()) + " left");
        parts.Add("Auto-renew on");
        return string.Join(" · ", parts);
    }
    static string Dur(double s) => s >= 3600 ? $"{Math.Floor(s / 3600)} h {Math.Floor(s % 3600 / 60)} min" : s >= 60 ? $"{Math.Ceiling(s / 60)} min" : "under a minute";

    FrameworkElement StatusHeader()
    {
        var c = S.Conn;
        var icon = c switch { "connecting" or "reconnecting" => "autorenew", "connected" => "shield_ok", _ => "shield_off" };
        var (bg, fg) = c switch
        {
            "connected" => ((Brush)Theme.Mix("sync", 0.22), "sync-hi"),
            "connecting" or "reconnecting" => (Theme.Br("ib-warn"), "sev-warn"),
            _ => (Theme.Br("sel"), "text-2"),
        };
        var ic = Icons.Icon(icon, 28, Theme.Br(fg), true);
        if (c is "connecting" or "reconnecting") Anim.Spin(ic);
        var circle = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Background = bg, Child = ic };
        var txt = V(2, Title(StatusTxt()));
        if (c == "connected" || StatusSub() != "")
        {
            var sub = new TextBlock { FontSize = 14, Foreground = Theme.Br("text-2"), TextWrapping = TextWrapping.WrapWholeWords };
            sub.Inlines.Add(c == "connected" ? w.Live("expiry", new Run { Text = Expiry(G) }) : new Run { Text = StatusSub() });
            txt.Children.Add(sub);
        }
        txt.VerticalAlignment = VerticalAlignment.Center;
        var row = HS(16, circle, txt);
        row.Padding = T(0, 4, 0, 8);
        return row;
    }

    FrameworkElement LocationCard()
    {
        var e = G.Ex(S.Loc); var c = S.Conn; bool simple = S.Simple;
        UIElement? label = c == "connected" ? (simple ? null : Pill("Current location", "check", "ok"))
            : S.Mode == "auto" ? Pill("Current Best Location", "auto_awesome") : Pill("Selected Location", "push_pin", "sel");
        var name = new TextBlock { FontSize = 16, Foreground = TextBrush, TextWrapping = TextWrapping.NoWrap };
        name.Inlines.Add(new Run { Text = G.EX.Count == 0 ? "Loading locations…" : e.Country });
        if (!simple && G.EX.Count > 0) name.Inlines.Add(new Run { Text = " #" + Xid(e.Id), FontFamily = Theme.Mono, FontSize = 12.8, Foreground = Theme.Br("text-2") });
        var col = V(2, name, HS(6, Bars(G, e), QLine(G, e)));
        if (label != null) { label.SetValue(FrameworkElement.MarginProperty, T(0, 4, 0, 0)); col.Children.Add(label); }
        var grid = new Grid { ColumnSpacing = 16, MinHeight = 60 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (ExitIcons.For(e.Id, S.Icons, true, e.Cc) is FrameworkElement xi) grid.Children.Add(xi);
        col.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(col, 1); grid.Children.Add(col);
        var chg = HS(8, Txt("Change", 14, "text-2"), Icons.Icon("chevron_right", 16, Theme.Br("text-2"))); chg.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(chg, 2); grid.Children.Add(chg);
        var card = new Button
        {
            Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = T(16, 12, 16, 12), MinHeight = 84, CornerRadius = new CornerRadius(6),
            Background = Theme.Br("card"), BorderBrush = Theme.Br("card-bd"), BorderThickness = new Thickness(1),
        };
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
        var t1 = Txt(steps[st], 13);
        dots.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(t1, 1);
        cap.Children.Add(dots); cap.Children.Add(t1);
        var stage = V(8, ConnectAnim.Build(w), cap, w.Live("cprog", Bar(0, "accent")));
        return Box(stage, pad: 14);
    }

    UIElement Actions()
    {
        var c = S.Conn;
        var row = new CommunityToolkit.WinUI.Controls.WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8, Padding = T(0, 4, 0, 4) };
        if (c == "idle") row.Children.Add(Btn("Connect", () => Act("connect"), "accent lg", "power_settings_new", disabled: G.EX.Count == 0));
        else if (c == "connecting") row.Children.Add(Btn("Disconnect", () => Act("cancel"), "lg", "power_settings_new"));
        else row.Children.Add(Btn("Disconnect", () => Act("disconnect"), "lg", "power_settings_new"));
        return row;
    }

    UIElement? Details()
    {
        var c = S.Conn; var a = G.Left(); var e = G.Ex(S.Loc);
        if (S.Simple || c == "connecting") return null;
        FrameworkElement Kv(string k, UIElement v) => Box(V(2, Cap(k), v), pad: 12);
        TextBlock Val(string s, string? color = null) => Mono(s, 16, color, true);
        var kvs = new List<FrameworkElement>();
        var ping = Val(e.Ping > 0 && c != "reconnecting" ? e.Ping.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " ms" : "—");
        if (e.Ping > 0) ping.Foreground = PingBrush(e.Ping);
        kvs.Add(Kv("Ping", ping));
        if (a > 0) kvs.Add(Kv("Data left here", w.Live("left", Val(""))));
        var gr = G.GradeOf(e);
        kvs.Add(Kv("Route", Val(gr.Label)));
        if (e.Measured) kvs.Add(Kv("Path vs. best exit", Val(Math.Round(e.Pv * 100) + "%")));
        var col = V(12, new VariableSizedWrapGridLike(kvs, 120, 4));
        if (a > 0 && G.IsOn) col.Children.Add(Box(Usage("Data left at " + e.City)));
        return col;
    }

    UIElement Usage(string cap)
    {
        var head = new Grid();
        head.Children.Add(Cap(cap));
        var v = w.Live("left", Mono("", 16, bold: true)); v.HorizontalAlignment = HorizontalAlignment.Right; head.Children.Add(v);
        var meter = w.Live("meter", Bar(Math.Min(100, G.Left() / Math.Max(1, R.ChunkMb) * 100), "sync", 6));
        return V(8, head, meter, Txt("The exit is paid ahead for each chunk of data; the next payment happens by itself.", 12, "text-2"));
    }

    UIElement Home()
    {
        var main = V(12, StatusHeader(), LocationCard());
        var mid = V(8);
        if (S.Conn == "connecting") mid.Children.Add(ConnectingStage());
        var all = Messages.Moments(G).Where(m => S.Conn != "connecting" || m.Own).ToList(); int lim = w.U.MoreMsgs ? 9 : 2;
        foreach (var m in all.Take(lim)) mid.Children.Add(Info(m, a => Act(a), k => Act("dismissMsg", k: k)));
        if (all.Count > lim) mid.Children.Add(Btn($"Show {all.Count - lim} more", () => { w.U.MoreMsgs = !w.U.MoreMsgs; w.Render(); }, "hyper sm", "expand_more"));
        if (mid.Children.Count > 0) main.Children.Add(mid);
        main.Children.Add(Actions());
        if (Details() is UIElement det) main.Children.Add(det);
        main.Children.Add(Txt(PROTOTYPE, 12, "text-2"));
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
            var allBtn = Btn("All locations", () => Act("pick"), "hyper sm", trail: "chevron_right"); allBtn.HorizontalAlignment = HorizontalAlignment.Right; hh.Children.Add(allBtn);
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
        var opts = new List<(string v, string l, string? tip)> { ("best", "Best", TIPS["best"]) };
        if (withAlpha) opts.Add(("alpha", "A–Z", TIPS["alpha"]));
        opts.Add(("recent", "Recent", TIPS["recent"]));
        return Kit.Selector(opts, S.Ptab, v => Act("ptab", v: v));
    }

    (List<Exit> list, bool more, Exit? b, string q) ExitList(bool shortList)
    {
        var q = S.Q.Trim().ToLowerInvariant(); var b = G.Best();
        var all = G.EX.Where(x => G.GradeOf(x).K != "hidden" && (q != "" || x.Id != S.Loc || S.Ptab is "recent" or "best")).ToList();
        List<Exit> list = q != "" ? all.Where(x => x.City.ToLowerInvariant().Contains(q) || x.Country.ToLowerInvariant().Contains(q) || x.Place.ToLowerInvariant().Contains(q)).ToList()
            : S.Ptab == "recent" ? S.Recent.Select(r => G.ExOrNull(r.Id)).Where(x => x != null && all.Contains(x)).Cast<Exit>().ToList()
            : S.Ptab == "alpha" ? all.OrderBy(x => x.Country, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("en"), false)).ToList()
            : all.OrderByDescending(x => G.GradeOf(x).Sc ?? -2).ToList();
        var more = q == "" && !S.Pall && list.Count > (shortList ? 6 : 99);
        if (more) list = list.Take(6).ToList();
        return (list, more, b, q);
    }

    UIElement RowAction(Exit x, bool withLeft = true)
    {
        bool on = G.IsOn, cur = x.Id == S.Loc; var a = G.Left(x.Id);
        if (cur && on) return Pill("Connected", tone: "ok");
        var row = HS(10);
        if (withLeft && a > 0) row.Children.Add(new TextBlock { Text = Math.Round(a) + " MB left", FontFamily = Theme.Mono, FontSize = 11, Foreground = Theme.Br("text-2"), VerticalAlignment = VerticalAlignment.Center });
        var id = x.Id;
        row.Children.Add(Btn(on ? "Switch" : "Connect", () => Act("pick2", id: id), "sm"));
        return row;
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

    /// <summary>The best exit right now carries this label in the list (no separate "best available" row).</summary>
    static FrameworkElement BestPill() => Pill("Current Best Location", "auto_awesome");

    UIElement RowsList(bool shortList)
    {
        var (list, more, b, q) = ExitList(shortList);
        var lv = V(2);
        foreach (var x in list)
        {
            var sub = new WrapPanelLike(6);
            if (b != null && x.Id == b.Id) sub.Add(BestPill());
            sub.Add(Bars(G, x));
            sub.Add(QLine(G, x));
            lv.Children.Add(Row(ExitIcons.For(x.Id, S.Icons, cc: x.Cc), ExitName(x), sub.Panel, RowAction(x)));
        }
        if (list.Count == 0)
        {
            var em = Txt(q != "" ? $"No exit matches “{S.Q}”." : S.Ptab == "recent" ? "No exits used yet" : G.EX.Count == 0 ? "Loading locations…" : "", 14, "text-2");
            em.Padding = T(12, 24, 12, 24); em.HorizontalAlignment = HorizontalAlignment.Center; lv.Children.Add(em);
        }
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
        var cols = new[] { new GridLength(2, GridUnitType.Star), new GridLength(1.2, GridUnitType.Star), new GridLength(72), new GridLength(64), new GridLength(96), new GridLength(92) };
        Grid TR(bool head)
        {
            var g = new Grid { ColumnSpacing = 8, MinHeight = head ? 36 : 48, Padding = T(12, 4, 12, 4), BorderBrush = Theme.Br("stroke"), BorderThickness = head ? new Thickness(0) : T(0, 1, 0, 0) };
            if (head) g.Background = Theme.Br("subtle-p");
            foreach (var c in cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = c, MinWidth = c.IsStar ? (c.Value == 2 ? 150 : 104) : 0 });
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
        Put(hr, 0, Th("Exit", "alpha")); Put(hr, 1, Th("Quality", "best")); Put(hr, 2, Th("Latency", null)); Put(hr, 3, Th("Path", null)); Put(hr, 4, Th("Data left", null));
        tbl.Children.Add(hr);
        foreach (var x in list)
        {
            var r = TR(false); var a = G.Left(x.Id);
            var name = new WrapPanelLike(6); name.Add(new TextBlock { Text = x.Country, FontWeight = FontWeights.SemiBold, FontSize = 13 }); name.Add(Mono("#" + Xid(x.Id), 11, "text-2"));
            if (b != null && x.Id == b.Id) name.Add(BestPill());
            var ex = HS(10); if (ExitIcons.For(x.Id, S.Icons, cc: x.Cc) is FrameworkElement xi) ex.Children.Add(xi); ex.Children.Add(name.Panel);
            Put(r, 0, ex);
            var ql = HS(6); ql.Children.Add(Bars(G, x)); var qt = QLine(G, x); qt.FontSize = 13; ql.Children.Add(qt);
            Put(r, 1, ql);
            Put(r, 2, Mono(x.Ping > 0 ? x.Ping.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " ms" : "—", 13));
            Put(r, 3, Mono(x.Measured ? Math.Round(x.Pv * 100) + "%" : "—", 13));
            Put(r, 4, Mono(a > 0 ? Math.Round(a) + " MB" : "—", 13));
            var act = RowAction(x, false); act.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right); Put(r, 5, act);
            r.PointerEntered += (_, _) => r.Background = Theme.Br("subtle-h"); r.PointerExited += (_, _) => r.Background = null;
            tbl.Children.Add(r);
        }
        if (list.Count == 0) { var em = Txt(q != "" ? $"No exit matches “{S.Q}”." : S.Ptab == "recent" ? "No exits used yet" : "", 14, "text-2"); em.Padding = T(12, 24, 12, 24); em.HorizontalAlignment = HorizontalAlignment.Center; tbl.Children.Add(em); }
        var box = new Border { Background = Theme.Br("card"), BorderBrush = Theme.Br("card-bd"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = tbl };
        outer.Children.Add(box);
        return outer;
    }

    UIElement Locations()
    {
        int nHidden = G.EX.Count(e => G.GradeOf(e).K == "hidden");
        bool table = !S.Simple && w.ContentWidth >= 760;
        var cmd = HS(16, Search(true), Selector(!S.Simple));
        rowsHost = new Border { Child = table ? Table() : RowsList(false) };
        tableHost = table;
        var foot = Txt(S.Ptab == "recent" ? "Your last 6 exits" : nHidden > 0 ? $"{nHidden} exit{(nHidden == 1 ? "" : "s")} without a route right now hidden" : "", 12, "text-2");
        foot.Margin = T(4, 12, 4, 12);
        return Page(Title("Locations"), cmd, rowsHost, foot);
    }

    // =============================== BALANCE ===============================
    /// <summary>The address as plain monospace text with a Copy button.</summary>
    UIElement Addr()
    {
        bool copied = Now - S.CopiedAt < 2200;
        var addr = R.NodeAddress;
        if (addr.Length < 12) return Txt("Your VPN address appears once the Gnosis VPN service has started.", 13, "text-2");
        var v = Mono(addr, 13); v.TextWrapping = TextWrapping.Wrap; v.VerticalAlignment = VerticalAlignment.Center; v.IsTextSelectionEnabled = true;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(v, "Address " + addr);
        var b = Btn(copied ? "Copied" : "Copy", () =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage(); dp.SetText(addr);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            Act("copy");
        }, "sm", copied ? "check" : "content_copy");
        if (copied && b is Button bb) bb.Background = Theme.Br("ib-ok");
        var wp = new WrapPanelLike(10); wp.Add(v); wp.Add(b); wp.Panel.VerticalSpacing = 8;
        return wp.Panel;
    }

    UIElement Balance()
    {
        var e = G.Ex(S.Loc); var a = G.Left();
        var bal = Box(V(8, Cap("Available balance"), Mono(Mb(S.Balance), 40, bold: true)));
        var p = Page(Title("Balance"), bal);
        p.Children.Add(Section("Add data"));
        var addr = R.NodeAddress;
        var col = V(10);
        if (addr.Length >= 12)
            col.Children.Add(new Border { Width = 196, Padding = new Thickness(10), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Microsoft.UI.Colors.White), Child = QrCode.For(addr, 176), HorizontalAlignment = HorizontalAlignment.Left });
        col.Children.Add(Addr());
        col.Children.Add(Btn("Open the funding portal", () => Act("fundPortal"), "accent", trail: "open_in_new", disabled: addr.Length < 12));
        p.Children.Add(Box(col));
        if (G.IsOn && a > 0) { p.Children.Add(Section("This exit")); p.Children.Add(Box(Usage(e.City))); }
        return p;
    }

    public UIElement BalanceFlyout()
    {
        var a = G.Left();
        var p = V(8, Cap("Balance"), Mono(Mb(S.Balance), 28, bold: true));
        if (a > 0)
        {
            var g = new Grid(); g.Children.Add(Txt($"Left at {G.Ex(S.Loc).City}", 13, "text-2"));
            var v = w.Live("left", Mono("", 13, bold: true)); v.HorizontalAlignment = HorizontalAlignment.Right; g.Children.Add(v);
            p.Children.Add(g);
        }
        p.Children.Add(Btn("Add data", () => { S.BalanceOpen = false; Act("addData"); }, "accent"));
        p.Width = 268;
        return p;
    }

    // =============================== SETTINGS ===============================
    static bool exporting;
    static string Version()
    {
        var ver = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion ?? "?";
        var plus = ver.IndexOf('+'); return plus > 0 ? ver[..Math.Min(ver.Length, plus + 8)] : ver;
    }
    async void ExportLogs()
    {
        if (exporting) return;
        exporting = true; w.Render();
        try
        {
            var (path, service, why) = await LogExport.Run(R.LastStatus, Version());
            App.Log($"logs exported to {path}" + (service ? "" : $" without the service log: {why}"));
            w.ShowSnack(service ? $"Logs saved to Downloads: {System.IO.Path.GetFileName(path)}" : $"Saved without the service's log ({why}): {System.IO.Path.GetFileName(path)}");
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
        }
        catch (Exception e) { App.Log("log export: " + e); w.ShowSnack("Could not export the logs: " + e.Message); }
        finally { exporting = false; w.Render(); }
    }

    UIElement Settings()
    {
        var e = G.Ex(S.Loc);
        var p = Page(Title("Settings"));
        p.Children.Add(Section("Startup and notification area"));
        p.Children.Add(Grp(
            Card("startup", "Start with Windows", "Gnosis VPN opens when you sign in.", Toggle(w.U.StartWin, () => { w.U.StartWin = !w.U.StartWin; Startup.Apply(w.U); w.Render(); }, "Start with Windows")),
            Card("tray", "Start in the notification area", "Opens without a window. Find it next to the clock.", Toggle(w.U.StartMin, () => { w.U.StartMin = !w.U.StartMin; Startup.Apply(w.U); w.Render(); }, "Start in the notification area", !w.U.StartWin), disabled: !w.U.StartWin),
            Card("window", "Keep running when the window is closed", "Closing the window leaves the VPN connected in the notification area.", Toggle(w.U.KeepTray, () => { w.U.KeepTray = !w.U.KeepTray; w.Render(); }, "Keep running when the window is closed"))));
        p.Children.Add(Section("App"));
        p.Children.Add(Grp(
            Card("location", "Preferred exit", S.Mode == "auto" ? "Auto · best" : e.City, Btn("Change", () => Act("pick"))),
            Card("monitoring", "Detailed metrics", "Ping, route quality and the exit table.", Toggle(!S.Simple, () => Act("view"), "Detailed metrics")),
            Card("person", "Exit icons", null, Combo(new[] { ("country", "Country"), ("node", "Node icon"), ("flag", "Flags"), ("none", "None") }, S.Icons, v => Act("icons", v: v))),
            Card("public", "App theme", null, Combo(new[] { ("system", "Use system setting"), ("light", "Light"), ("dark", "Dark") }, w.U.Mode, v => { w.U.Mode = v; Prefs.Save("mode", v); w.Render(); }))));
        p.Children.Add(Section("Logs"));
        p.Children.Add(Grp(Card("download", "Export logs",
            "Saves one zip to your Downloads folder with the app's and the service's logs, the HOPR node's included (asks for administrator approval for the service's part). It contains your VPN address and connection details, not your node's keys.",
            Btn(exporting ? "Exporting…" : "Export", ExportLogs, disabled: exporting))));
        p.Children.Add(Section("About"));
        p.Children.Add(Grp(Card("vpn_key", "Gnosis VPN", $"App {Version()} · prototype" + (R.DebugRelay ? " · DEBUG-logging: the service sends its log to the developer" : ""), null),
            Card("warning", "Prototype, for testing only", "Not a finished or supported product, and not security-audited. There is no kill switch, so it may expose your traffic; it spends real funds on Gnosis Chain. Use small amounts, and do not rely on it to protect your identity or your data. No warranty (GNU GPL v3.0).", null)));
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
