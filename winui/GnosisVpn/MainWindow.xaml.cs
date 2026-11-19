using System;
using System.Collections.Generic;
using System.Linq;
using GnosisVpn.Engine;
using GnosisVpn.Ui;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using static GnosisVpn.Engine.AppEngine;
using static GnosisVpn.Ui.Kit;

namespace GnosisVpn;

/// <summary>UI-only state: theme, panels, window and notification-area settings.</summary>
public sealed class UiState
{
    public string Mode = "system";          // app theme: system | light | dark
    public bool MoreMsgs, VizMb;
    public bool KeepTray = true, StartWin = true, StartMin;
    public string WinState = "open";        // open | min | closed | quit
    public bool Hinted;
}

public sealed partial class MainWindow : Window
{
    public readonly AppEngine G = new();
    /// <summary>The Gnosis VPN service's state, polled through its pipe (the only backend).</summary>
    public readonly UiState U = new();
    public State S => G.S;
    readonly Pages pages;
    readonly DispatcherQueue dq;
    readonly List<(string key, object el)> live = new();
    string curSig = "", curSoloSig = "";
    Microsoft.UI.Dispatching.DispatcherQueueTimer? tickTimer, relayTimer;
    bool rendering;

    public MainWindow()
    {
        InitializeComponent();
        dq = DispatcherQueue.GetForCurrentThread();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TBar);
        Title = "Gnosis VPN";
        FitToScreen(1180, 760);
        try { AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico")); } catch { }
        pages = new Pages(this);
        U.Mode = Prefs.Get("mode") ?? "system";
        U.StartWin = Prefs.Get("startWin") != "0"; U.StartMin = Prefs.Get("startMin") == "1";

        G.Render += () => Render();
        G.Snack += msg => ShowSnack(msg);
        G.OpenUrl += url => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));

        // the Gnosis VPN service is the only backend: until its pipe answers, the app shows
        // "Can't reach the Gnosis VPN service" and keeps polling (it starts with Windows, delayed)
        G.Real = new RealBackend(G, a => dq.TryEnqueue(() => a()));
        App.Log("backend: Gnosis VPN service");
        // Fields, not locals: a DispatcherQueueTimer nothing references is garbage-collected and stops
        // firing. The tick drives the service polling; losing it froze the app on "Discovering the network".
        tickTimer = dq.CreateTimer(); tickTimer.Interval = TimeSpan.FromMilliseconds(250); tickTimer.Tick += (_, _) => G.Tick(); tickTimer.Start();
        relayTimer = dq.CreateTimer(); relayTimer.Interval = TimeSpan.FromMilliseconds(2600); relayTimer.Tick += (_, _) => G.RelayTick(); relayTimer.Start();

        BuildNav();
        Nav.ItemInvoked += (_, e) => { if (e.InvokedItemContainer?.Tag is string k) NavTo(k); };
        Nav.DisplayModeChanged += (_, _) => { curSig = ""; Render(); };
        Nav.SizeChanged += (_, _) => Render();
        TBar.BackRequested += (_, _) => GoBack();
        TBar.PaneToggleRequested += (_, _) => Nav.IsPaneOpen = !Nav.IsPaneOpen;
        Root.ActualThemeChanged += (_, _) => { curSig = ""; curSoloSig = ""; Render(); };
        AddKeys();
        Closed += OnClosed;
        Notify.Init(this);
        // a thread timer, so it reports even when the UI thread is busy
        perf = new System.Threading.Timer(_ => App.Log($"perf: renders={Renders} rebuilds={Rebuilds} ws={Environment.WorkingSet / 1048576} MB conn={S.Conn}"), null, 30000, 30000);
        Render();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>Opens at w × h effective pixels (the display's scaling applied), but never larger than
    /// 92% of the screen's work area, centred on it.</summary>
    void FitToScreen(int ww, int hh)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var sc = Math.Max(1, GetDpiForWindow(hwnd)) / 96.0;
        var wa = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        int pw = (int)Math.Min(ww * sc, wa.Width * 0.92), ph = (int)Math.Min(hh * sc, wa.Height * 0.92);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(wa.X + (wa.Width - pw) / 2, wa.Y + (wa.Height - ph) / 2, pw, ph));
    }

    /// <summary>Lays the content out at w × h (logical px) even if the screen is smaller (screenshots).</summary>
    public void SetSize(int ww, int hh)
    {
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(ww, hh));
        Root.Width = ww; Root.Height = hh;
        Root.HorizontalAlignment = HorizontalAlignment.Left; Root.VerticalAlignment = VerticalAlignment.Top;
        curSig = ""; Render();
    }
    // ---------- navigation (route() / navTo() in ui.js) ----------
    public string Route()
    {
        var st = S.Stack;
        var top = st.Count > 0 ? st[^1] : null;
        if (top == null) return "home";
        if (top == "pick") return "locations";
        if (top == "settings") return "settings";
        if (top == "add") return "balance";
        return top;   // direct
    }
    public string BaseKey() => S.Fail != null ? "fail" : S.Gate ? "gate" : S.SyncT != null ? "sync" : "home";
    bool CanBack() => S.Stack.Count > 0;

    public void NavTo(string k)
    {
        if (Nav.DisplayMode != NavigationViewDisplayMode.Expanded) Nav.IsPaneOpen = false;
        S.Stack = k switch
        {
            "home" => new(),
            "locations" => new() { "pick" },
            "balance" => new() { "add" },
            "settings" => new() { "settings" },
            _ => new() { k },
        };
        if (k == "locations") S.Pall = false;
        Render();
    }

    public void GoBack()
    {
        if (S.BalanceOpen) { S.BalanceOpen = false; Render(); return; }
        if (CanBack()) G.Act("back");
    }

    readonly Dictionary<string, NavigationViewItem> navItems = new();
    void BuildNav()
    {
        NavigationViewItem Item(string k, string icon, string label)
        {
            var it = new NavigationViewItem { Content = label, Tag = k, Icon = Icons.PathIcon(icon) };
            navItems[k] = it; return it;
        }
        Nav.MenuItems.Add(Item("home", "home", "Home"));
        Nav.MenuItems.Add(Item("locations", "location", "Locations"));
        Nav.MenuItems.Add(Item("balance", "account_balance_wallet", "Balance"));
        Nav.FooterMenuItems.Add(Item("settings", "settings", "Settings"));
    }

    void UpdateNav()
    {
        var r = Route();
        foreach (var (k, it) in navItems)
        {
            bool on = r == k || (k == "balance" && r == "direct");
            if (on && !ReferenceEquals(Nav.SelectedItem, it)) Nav.SelectedItem = it;
            string? badge = k switch
            {
                "home" => S.Conn == "connected" ? "sync" : S.Conn is "connecting" or "reconnecting" ? "sev-warn" : null,
                _ => null,
            };
            it.InfoBadge = badge == null ? null : new InfoBadge { Background = Theme.Br(badge), Width = 8, Height = 8, MinWidth = 8, Padding = new Thickness(0) };
            it.Icon = Icons.PathIcon(it.Tag is "home" ? "home" : k == "locations" ? "location" : k == "balance" ? "account_balance_wallet" : "settings", on);
        }
    }

    // ---------- layout facts ----------
    public double ContentWidth
    {
        get
        {
            var w = Nav.ActualWidth > 0 ? Nav.ActualWidth : AppWindow.Size.Width;
            return w - Nav.DisplayMode switch { NavigationViewDisplayMode.Expanded => 280, NavigationViewDisplayMode.Compact => 48, _ => 0 };
        }
    }
    public string PaneMode => Nav.DisplayMode switch { NavigationViewDisplayMode.Expanded => "x", NavigationViewDisplayMode.Compact => "c", _ => "m" };

    // ---------- live values (data-live in the prototype) ----------
    public T Live<T>(string key, T el) where T : class { live.Add((key, el)); return el; }

    void UpdateLive()
    {
        string? Val(string k) => k switch
        {
            "timer" => Hms(S.ConnSec),
            "left" => Math.Round(G.Left()) + " MB",
            "sypct" => S.SyncT is double t1 ? (Math.Min(SoloScreens.SYNC_LEN, t1) / SoloScreens.SYNC_LEN * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%" : null,
            "systep" => S.SyncT is double t2 ? SoloScreens.SyncStep(t2) : null,
            "core" => G.Real?.Core,
            _ => null,
        };
        double? Num(string k) => k switch
        {
            "meter" => Math.Min(100, G.Left() / Math.Max(1, G.Real.ChunkMb) * 100),
            "cprog" => S.Conn == "connecting" ? Math.Min(100, (Now - S.CStart) / Math.Max(1, S.CDur) * 100) : null,
            "sybar" => S.SyncT is double t ? Math.Min(SoloScreens.SYNC_LEN, t) / SoloScreens.SYNC_LEN * 100 : null,
            _ => null,
        };
        foreach (var (k, el) in live)
        {
            if (el is TextBlock tb) { var v = Val(k); if (v != null && tb.Text != v) tb.Text = v; }
            else if (el is ProgressBar pb) { var v = Num(k); if (v != null) pb.Value = v.Value; }
            else if (el is Microsoft.UI.Xaml.Documents.Run run) { var v = Val(k); if (v != null && run.Text != v) run.Text = v; }
        }
    }

    // ---------- render ----------
    public void Render()
    {
        if (rendering) return;
        rendering = true;
        try { RenderNow(); } finally { rendering = false; }
    }

    void ApplyTheme()
    {
        var want = U.Mode == "dark" ? ElementTheme.Dark : U.Mode == "light" ? ElementTheme.Light : ElementTheme.Default;
        if (Root.RequestedTheme != want) Root.RequestedTheme = want;
        Theme.Dark = Root.ActualTheme == ElementTheme.Dark;
        if (TBar.IconSource == null && System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "appicon-32.png")))
            TBar.IconSource = new ImageIconSource { ImageSource = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "appicon-32.png"))) };
    }

    public static int Renders, Rebuilds;
    System.Threading.Timer? perf;
    void RenderNow()
    {
        Renders++;
        ApplyTheme();
        var bk = BaseKey();
        bool navShown = bk == "home" || (bk == "gate" && G.Page == "direct");
        Nav.Visibility = navShown && bk == "home" ? Visibility.Visible : Visibility.Collapsed;
        Solo.Visibility = Nav.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        Solo.Background = Theme.Br("layer"); Solo.BorderBrush = Theme.Br("stroke");

        // title bar: back button, pane toggle in minimal mode, balance button (detailed view)
        TBar.IsBackButtonVisible = CanBack() && bk == "home";
        TBar.IsPaneToggleButtonVisible = bk == "home" && Nav.DisplayMode == NavigationViewDisplayMode.Minimal;
        Nav.IsPaneToggleButtonVisible = Nav.DisplayMode != NavigationViewDisplayMode.Minimal;
        TRight.Content = bk == "home" && !S.Simple ? BalanceButton() : null;
        if (TRight.Content == null) balBtn = null;

        if (Nav.Visibility == Visibility.Visible)
        {
            UpdateNav();
            var sig = $"{Theme.Dark}|{PaneMode}|{(ContentWidth >= 900 ? 2 : 1)}|{(ContentWidth >= 760 ? "t" : "l")}|" + pages.Signature(Route());
            if (sig != curSig)
            {
                bool routeChanged = curSig.Split('|').ElementAtOrDefault(5) != Route();
                live.Clear(); Rebuilds++;
                if (Rebuilds % 20 == 0) App.Log($"perf: rebuild #{Rebuilds}, signature now {sig[..Math.Min(sig.Length, 300)]}");
                PageHost.Content = pages.Build(Route());
                if (routeChanged) PageScroll.ChangeView(null, 0, null, true);
                curSig = sig;
            }
        }
        else
        {
            var direct = bk == "gate" && G.Page == "direct";
            // the funding page shows the node's balances and the Copy feedback: rebuild when they change
            var sig = $"{Theme.Dark}|{bk}|{G.Page}|{S.Fail?.Code}|{S.Fail?.Title}|{G.Real.NeedWx}|{G.Real.NodeAddress}" + (direct ? "|" + pages.Signature("direct") : "");
            if (sig != curSoloSig)
            {
                live.Clear();
                SoloHost.Content = direct ? pages.Build("direct") : SoloScreens.Build(this, bk);
                curSoloSig = sig;
            }
        }
        SyncBalanceFlyout();
        Tray.Sync(this);
        UpdateLive();
    }

    // ---------- title bar balance button + flyout ----------
    Button? balBtn;
    Flyout? balFly;
    FrameworkElement BalanceButton()
    {
        balBtn = new Button
        {
            Height = 32, Padding = T(10, 0, 10, 0),
            Content = HS(8, Icons.Icon("account_balance_wallet", 16), Mono(Mb(S.Balance), 12)),
        };
        Tip(balBtn, $"Balance {Mb(S.Balance)}");
        balBtn.Click += (_, _) => { S.BalanceOpen = !S.BalanceOpen; Render(); };
        return balBtn;
    }

    void SyncBalanceFlyout()
    {
        bool want = S.BalanceOpen && BaseKey() == "home";
        if (want && balFly == null)
        {
            balFly = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight, Content = pages.BalanceFlyout() };
            balFly.Closed += (_, _) => { balFly = null; if (S.BalanceOpen) { S.BalanceOpen = false; Render(); } };
            if (balBtn != null && balBtn.XamlRoot != null) balFly.ShowAt(balBtn);
            else balFly.ShowAt(Root, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = new Windows.Foundation.Point(Math.Max(320, Root.ActualWidth - 150), 46), Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight });
        }
        else if (!want && balFly != null) { var f = balFly; balFly = null; f.Hide(); }
    }

    // ---------- in-app banner (HOOK.snack) ----------
    DispatcherQueueTimer? snackTimer;
    public void ShowSnack(string msg)
    {
        Snack.Background = Theme.Br("flyout"); Snack.BorderBrush = Theme.Br("stroke-2");
        Snack.Child = HS(10, Icons.Icon("info", 16, Theme.Br("sev-info"), true), Txt(msg, 14));
        Snack.Visibility = Visibility.Visible;
        snackTimer?.Stop();
        snackTimer = dq.CreateTimer(); snackTimer.Interval = TimeSpan.FromMilliseconds(2800); snackTimer.IsRepeating = false;
        snackTimer.Tick += (_, _) => { Snack.Visibility = Visibility.Collapsed; snackTimer?.Stop(); };
        snackTimer.Start();
    }

    // ---------- keyboard: Esc closes, Alt+Left goes back, Ctrl+F searches locations ----------
    void AddKeys()
    {
        void K(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers mod, Action a)
        {
            var ka = new KeyboardAccelerator { Key = key, Modifiers = mod };
            ka.Invoked += (_, e) => { e.Handled = true; a(); };
            Root.KeyboardAccelerators.Add(ka);
        }
        K(Windows.System.VirtualKey.Escape, Windows.System.VirtualKeyModifiers.None, GoBack);
        K(Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.Menu, GoBack);
        K(Windows.System.VirtualKey.F, Windows.System.VirtualKeyModifiers.Control, () => { if (BaseKey() != "home") return; if (Route() != "locations") NavTo("locations"); pages.FocusSearch(); });
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    // ---------- window close: keep running in the notification area ----------
    public bool Quitting;
    void OnClosed(object sender, WindowEventArgs e)
    {
        if (Quitting) { Tray.Dispose(); return; }
        if (U.KeepTray)
        {
            e.Handled = true;
            AppWindow.Hide();
            U.WinState = "closed";
            if (!U.Hinted)
            {
                U.Hinted = true;
                Notify.SystemNote(this, "Gnosis VPN is still running", G.IsOn ? "You’re still connected. Find Gnosis VPN in the notification area to disconnect or quit." : "Find it in the notification area, next to the clock.");
            }
            Render();
        }
        else Tray.Dispose();
    }

    public void ShowWindow()
    {
        U.WinState = "open";
        AppWindow.Show();
        Activate();
        Render();
    }

    public void Quit()
    {
        Quitting = true;
        Tray.Dispose();
        Close();
        Application.Current.Exit();
    }

    public FrameworkElement RootElement => Root;
}
