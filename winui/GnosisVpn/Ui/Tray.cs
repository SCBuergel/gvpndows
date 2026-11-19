using System;
using System.IO;
using System.Linq;
using GnosisVpn.Engine;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using static GnosisVpn.Engine.AppEngine;
using static GnosisVpn.Ui.Kit;

namespace GnosisVpn.Ui;

/// <summary>The notification-area icon: its color shows the state; left click opens the flyout
/// (status, exit, Connect/Disconnect, balance), right click the menu (Connect, Change location,
/// Open, Settings, Quit).</summary>
public static class Tray
{
    static NotifyIconNative? icon;
    static TrayFlyoutWindow? fly;

    public static string State(AppEngine g) => g.S.Conn switch { "connected" => "ok", "idle" => "off", _ => "warn" };
    static string StatusTxt(AppEngine g) => g.S.Conn switch { "connecting" => "Connecting", "connected" => "Connected", "reconnecting" => "Reconnecting", _ => "Not connected" };

    public static void Sync(MainWindow w)
    {
        if (w.U.WinState == "quit") return;
        try
        {
            if (icon == null)
            {
                icon = new NotifyIconNative();
                icon.LeftClick = pt => w.DispatcherQueue.TryEnqueue(() => ToggleFlyout(w, pt));
                icon.Menu = () => MenuItems(w);
            }
            var tip = $"Gnosis VPN · {StatusTxt(w.G)}" + (w.G.IsOn ? " · " + w.G.Ex(w.G.S.Loc).City : "");
            icon.Set(Path.Combine(AppContext.BaseDirectory, "Assets", $"tray-{State(w.G)}.ico"), tip);
            fly?.Refresh();
        }
        catch (Exception e) { App.Log("tray: " + e.Message); }
    }

    /// <summary>The right-click menu (ctxMenu() in ui.js), as a native Windows menu.</summary>
    static System.Collections.Generic.List<(string?, Action?)> MenuItems(MainWindow w)
    {
        var G = w.G; var S = G.S;
        void Ui(Action a) => w.DispatcherQueue.TryEnqueue(() => a());
        return new()
        {
            S.Conn != "idle" ? ("Disconnect", () => Ui(() => G.Act(S.Conn == "connecting" ? "cancel" : "disconnect"))) : ("Connect", () => Ui(() => G.Act("connect"))),
            ("Change location…", () => Ui(() => { w.ShowWindow(); G.Act("pick"); })),
            ("Open Gnosis VPN", () => Ui(w.ShowWindow)),
            ("Settings", () => Ui(() => { w.ShowWindow(); G.Act("settings"); })),
            (null, null),
            ("Quit Gnosis VPN", () => Ui(w.Quit)),
        };
    }

    /// <summary>The same menu as a MenuFlyout, for the TRAY·M screenshot stand-in.</summary>
    public static MenuFlyout MenuFor(MainWindow w)
    {
        var m = new MenuFlyout();
        foreach (var (label, act) in MenuItems(w))
        {
            if (label == null) { m.Items.Add(new MenuFlyoutSeparator()); continue; }
            var icon = label.StartsWith("Connect") || label.StartsWith("Disconnect") ? "power_settings_new" : label.StartsWith("Change") ? "location" : label.StartsWith("Open") ? "window" : label == "Settings" ? "settings" : "close";
            var it = new MenuFlyoutItem { Text = label, Icon = Icons.PathIcon(icon) }; it.Click += (_, _) => act?.Invoke(); m.Items.Add(it);
        }
        return m;
    }

    public static void OpenMenuAtIcon() => icon?.ShowMenuAtIcon();
    public static void OpenFlyout(MainWindow w) { if (fly == null) ToggleFlyout(w, new NotifyIconNative.POINT()); }
    static void ToggleFlyout(MainWindow w, NotifyIconNative.POINT at)
    {
        if (fly != null) { fly.Close(); fly = null; return; }
        fly = new TrayFlyoutWindow(w, at);
        fly.Closed += (_, _) => fly = null;
        fly.Activate();
    }

    public static void Dispose() { try { fly?.Close(); icon?.Dispose(); } catch { } icon = null; }

    /// <summary>The notification-area flyout's content.</summary>
    public static UIElement FlyoutContent(MainWindow w, Action close)
    {
        var G = w.G; var S = G.S; var e = G.Ex(S.Loc); var c = S.Conn; var g = G.GradeOf(e);
        void A(string a) { G.Act(a); }
        ButtonBase main = c == "idle" ? Btn("Connect", () => { A("connect"); close(); }, "accent", "power_settings_new")
            : Btn("Disconnect", () => A(c == "connecting" ? "cancel" : "disconnect"), "", "power_settings_new");
        main.HorizontalAlignment = HorizontalAlignment.Stretch;
        var head = new Grid();
        head.Children.Add(HS(10, AppIcon(24), Txt("Gnosis VPN", 14, bold: true)));
        var set = IconBtn("settings", "Settings", () => { close(); w.ShowWindow(); A("settings"); }); set.HorizontalAlignment = HorizontalAlignment.Right; head.Children.Add(set);
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 10, Height = 10, Fill = Theme.Br(c == "connected" ? "sync" : c == "idle" ? "text-3" : "sev-warn"), VerticalAlignment = VerticalAlignment.Center };
        if (c is "connecting" or "reconnecting") Anim.Pulse(dot, 1, .3, .5);
        var st = new Grid();
        st.Children.Add(HS(10, dot, Txt(StatusTxt(G), 16, bold: true)));
        if (G.IsOn) { var l = HS(0, w.Live("left", Mono("", 12, "text-2")), Txt(" left here", 12, "text-2")); l.HorizontalAlignment = HorizontalAlignment.Right; l.VerticalAlignment = VerticalAlignment.Center; st.Children.Add(l); }
        var locRow = new Grid { ColumnSpacing = 12 };
        locRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); locRow.ColumnDefinitions.Add(new ColumnDefinition()); locRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (ExitIcons.For(e.Id, S.Icons) is FrameworkElement xi) locRow.Children.Add(xi);
        var lt = V(2, Txt(e.Country, 14), HS(6, Bars(G, e), Txt(g.Label, 12, "text-2"))); Grid.SetColumn(lt, 1); locRow.Children.Add(lt);
        var chev = Icons.Icon("chevron_right", 16); Grid.SetColumn(chev, 2); chev.VerticalAlignment = VerticalAlignment.Center; locRow.Children.Add(chev);
        var loc = new Button { Content = locRow, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = T(12, 10, 12, 10) };
        loc.Click += (_, _) => { close(); w.ShowWindow(); A("pick"); };
        var foot = new Grid { BorderBrush = Theme.Br("stroke"), BorderThickness = T(0, 1, 0, 0), Padding = T(0, 4, 0, 0) };
        var bal = new TextBlock { FontSize = 12, Foreground = Theme.Br("text-2"), VerticalAlignment = VerticalAlignment.Center };
        bal.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "Balance " }); bal.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Mb(S.Balance), FontFamily = Theme.Mono, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Br("text") });
        foot.Children.Add(bal);
        var open = Btn("Open Gnosis VPN", () => { close(); w.ShowWindow(); }, "hyper sm"); open.HorizontalAlignment = HorizontalAlignment.Right; foot.Children.Add(open);
        var p = V(12, head, st, loc, main, foot); p.Padding = new Thickness(16); p.Width = 340;
        return p;
    }

    public static FrameworkElement AppIcon(double size)
    {
        var f = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon-32.png");
        if (File.Exists(f)) return new Image { Source = new BitmapImage(new Uri(f)), Width = size, Height = size };
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 4), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1d, 0x18, 0x34)), Child = Icons.Icon("key", size * .56, new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x9f, 0xd3, 0x4a)), true) };
    }
}

/// <summary>A small borderless window above the notification area for the tray flyout; closes when it loses focus.</summary>
public sealed class TrayFlyoutWindow : Window
{
    readonly MainWindow w;
    readonly Grid root = new();
    public TrayFlyoutWindow(MainWindow w, NotifyIconNative.POINT at)
    {
        this.w = w;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        Content = root;
        root.RequestedTheme = w.RootElement.ActualTheme;
        Refresh();
        AppWindow.SetPresenter(OverlappedPresenter.CreateForContextMenu());
        AppWindow.IsShownInSwitchers = false;
        var area = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(at.X, at.Y), DisplayAreaFallback.Primary).WorkArea;
        double scale = w.RootElement.XamlRoot?.RasterizationScale ?? 1.0;
        root.Measure(new Windows.Foundation.Size(340, 2000));
        int ww = (int)(340 * scale), hh = (int)((root.DesiredSize.Height + 48) * scale);
        int x = at.X > 0 ? Math.Clamp(at.X - ww / 2, area.X + 12, area.X + area.Width - ww - 12) : area.X + area.Width - ww - 12;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, area.Y + area.Height - hh - 12, ww, hh));
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) Close(); };
        // fit the height once the content is laid out (live values and fonts change its size)
        root.Loaded += (_, _) =>
        {
            root.Measure(new Windows.Foundation.Size(340, 2000));
            var h2 = (int)((root.DesiredSize.Height + 2) * scale); var p0 = AppWindow.Position;
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(p0.X, area.Y + area.Height - h2 - 12, ww, h2));
        };
    }
    public void Refresh() { root.Children.Clear(); root.Children.Add(Tray.FlyoutContent(w, Close)); }
}

/// <summary>Toasts (Windows App SDK app notifications) for the app's notifications (connection changes, low data); missed
/// ones wait in Notification Center. Buttons run the same actions as in the app.</summary>
public static class Notify
{
    static bool registered;
    static MainWindow? win;

    public static void Init(MainWindow w)
    {
        win = w;
        if (registered) return;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, args) =>
            {
                var act = args.Arguments.TryGetValue("act", out var a) ? a : null;
                win?.DispatcherQueue.TryEnqueue(() => { win.ShowWindow(); if (act != null && act != "nclose" && act != "open") win.G.Act(act); });
            };
            // unpackaged: register the name and icon Windows shows on toasts and in Notification Center
            AppNotificationManager.Default.Register("Gnosis VPN", new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico")));
            registered = true;
        }
        catch (Exception e) { App.Log("toasts: " + e.Message); }
    }

    public static void SystemNote(MainWindow w, string title, string body)
    {
                Init(w);
        try { AppNotificationManager.Default.Show(new AppNotificationBuilder().AddArgument("act", "open").AddText(title).AddText(body).BuildNotification()); }
        catch (Exception e) { App.Log("toast: " + e.Message); }
    }

    public static void Unregister() { try { if (registered) AppNotificationManager.Default.Unregister(); } catch { } }
}

/// <summary>Start with Windows / Start in the notification area: the per-user Run key.</summary>
public static class Startup
{
    public static void Apply(UiState u)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            var exe = Environment.ProcessPath ?? "";
            if (u.StartWin) k.SetValue("GnosisVPN", $"\"{exe}\"" + (u.StartMin ? " --tray" : ""));
            else k.DeleteValue("GnosisVPN", false);
        }
        catch (Exception e) { App.Log("startup: " + e.Message); }
        Prefs.Save("startWin", u.StartWin ? "1" : "0"); Prefs.Save("startMin", u.StartMin ? "1" : "0");
    }
}

/// <summary>UI preferences in %LOCALAPPDATA%\GnosisVPN\ui.json.</summary>
public static class Prefs
{
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GnosisVPN", "ui.json");
    static System.Collections.Generic.Dictionary<string, string>? cache;
    static System.Collections.Generic.Dictionary<string, string> All()
    {
        if (cache != null) return cache;
        try { cache = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(FilePath)); } catch { }
        return cache ??= new();
    }
    public static string? Get(string k) => All().TryGetValue(k, out var v) ? v : null;
    public static void Save(string k, string v)
    {
                All()[k] = v;
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(cache)); } catch { }
    }
}
