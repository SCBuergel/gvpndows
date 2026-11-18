using System;
using System.Linq;
using GnosisVpn.Engine;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using static GnosisVpn.Engine.FakeEngine;
using static GnosisVpn.Ui.Kit;

namespace GnosisVpn.Ui;

/// <summary>ContentDialogs for the demo's sheets (dialogSpec() in the prototype's ui.js).</summary>
public static class Dialogs
{
    sealed record BtnSpec(string Label, string? Act, Action? Ui = null);
    sealed record Spec(string Key, string Title, Func<UIElement> Body, BtnSpec Primary, BtnSpec? Secondary, BtnSpec Close);

    static ContentDialog? open;
    static string? openKey;
    static readonly System.Collections.Generic.HashSet<ContentDialog> closedByCode = new();

    static Spec? SpecFor(MainWindow w)
    {
        var G = w.G; var S = G.S;
        if (w.BaseKey() != "home") return null;
        if (G.Page == "payout")
            return new("payout", "Pay outside the VPN", () => V(12,
                Txt("With no data left, the funding site can’t be reached through Gnosis VPN. To pay, Lockdown turns off and the VPN disconnects."),
                Info(new Moment("warn", "", "Until you reconnect, your traffic will not be private.", "", null), _ => { }, null),
                Txt("Once your payment arrives, we’ll ask you to turn Lockdown back on.", 14, "text-2")),
                new("Turn off Lockdown and continue", "payGo"), null, new("Cancel", "back"));
        if (S.OffConfirm)
            return S.Lockdown
                ? new("off1", "Lockdown on: traffic stays blocked", () => Txt("Turning off the VPN won’t bring the internet back while Lockdown is on."), new("Turn off", "offDo"), null, new("Cancel", "offCancel"))
                : new("off0", "Your traffic will not be private", () => Txt("Turning off sends everything outside Gnosis VPN."), new("Turn off", "offDo"), null, new("Cancel", "offCancel"));
        if (w.U.Dialog == "upload")
            return new("upload", "Upload logs to support?", () => Txt("Included: app and connection logs of the last 24 hours, the app version and your Windows version. Not included: the sites you visit or your VPN address."),
                new("Upload", "logsUpload", () => w.U.Dialog = null), null, new("Cancel", null, () => w.U.Dialog = null));
        var sh = S.Sheet;
        if (sh == null || sh.Type == "balance" || (sh.Type == "quota" && G.Page != null)) return null;
        if (sh.Type == "quota" && sh.Check)
            return new("check", "Four renewals done", () =>
            {
                var t = new TextBlock { TextWrapping = TextWrapping.WrapWholeWords, FontSize = 14 };
                t.Inlines.Add(new Run { Text = "Auto-renew stops after four in a row unless you say so. Renew four more, or it pauses in " });
                var timer = new Run { Text = "5:00", FontFamily = Theme.Mono, FontWeight = FontWeights.SemiBold };
                w.Live("ckt", timer);
                t.Inlines.Add(timer); t.Inlines.Add(new Run { Text = "." });
                return V(12, CheckGraphic(), t);
            }, new("Renew 4 more", "arCheckGo"), new("Always renew past 4", "arPastOn"), new("Pause", "arCheckNo"));
        if (sh.Type == "quota")
        {
            var l = G.Ex(sh.Id); bool on = G.IsOn || S.Conn == "paused";
            var title = sh.Intro ? "Auto-renew is on" : sh.Soon ? "Internet pauses soon" : sh.Renew ? "600 MB used up" : $"{l.City} gets 600 MB";
            var sub = sh.Intro ? "Each exit gets 600 MB. Renews by itself." : sh.Soon ? $"About {Dur(G.MinsLeft())} left here. Renew now to keep going." : sh.Renew ? "Renew to keep going." : "";
            var key = "quota" + sh.Id + (sh.Intro ? "i" : sh.Soon ? "s" : sh.Renew ? "r" : "");
            return new(key, title, () =>
            {
                var p = V(12, QuotaGraphic(w, sh.Id));
                if (sub != "") p.Children.Add(Txt(sub));
                if (!sh.Renew && Leave(w, sh.Id) is UIElement lv) p.Children.Add(lv);
                return p;
            }, new(sh.Renew ? "Renew" : on ? "Switch" : "Connect", "sheetGo"), sh.Intro ? new("Ask me each time", "arOffGo") : new("Turn on Auto-renew", "arOnGo"), new(sh.Renew ? "Not now" : "Cancel", "sheetNo"));
        }
        var e = G.Ex(sh.Id); var cur = G.Ex(S.Loc);
        if (sh.Type == "switch")
            return new("sw" + sh.Id, $"Switch to {e.City}?", () =>
            {
                var p = V(12, Txt("Unlike traditional VPNs, Gnosis VPN sets aside data from your balance for each exit you use."));
                if (Leave(w, e.Id) is UIElement lv) p.Children.Add(lv);
                var cb = new CheckBox { Content = "Don’t ask again", IsChecked = sh.Dont }; cb.Click += (_, _) => G.Act("dont");
                p.Children.Add(cb);
                return p;
            }, new("Switch · 600 MB", "sheetGo"), null, new("Cancel", "sheetNo"));
        return new("nobal" + sh.Id, "Not enough data to switch", () =>
        {
            var t = new TextBlock { TextWrapping = TextWrapping.WrapWholeWords, FontSize = 14 };
            t.Inlines.Add(new Run { Text = $"Switching would set aside 600 MB for {e.City}. Your balance is {(S.Balance <= 0 ? "empty" : "too low")} (" });
            t.Inlines.Add(new Run { Text = Mb(S.Balance), FontFamily = Theme.Mono, FontWeight = FontWeights.SemiBold, Foreground = Theme.Br("sev-bad") });
            t.Inlines.Add(new Run { Text = $"). Add data first{(G.IsOn ? $", or stay at {cur.City}" : "")}." });
            return t;
        }, new("Add data", "addData"), null, new(G.IsOn ? "Stay at " + cur.Country : "Cancel", "stay"));
    }

    /// <summary>Shows, swaps or hides the dialog to match the state.</summary>
    public static void Sync(MainWindow w)
    {
        var spec = SpecFor(w);
        if (spec?.Key == openKey) { if (open != null && open.RequestedTheme != w.RootElement.ActualTheme) open.RequestedTheme = w.RootElement.ActualTheme; return; }
        if (open != null) { closedByCode.Add(open); open.Hide(); open = null; openKey = null; }
        // only one ContentDialog can be open: wait until a hidden one has finished closing
        if (spec == null || w.RootElement.XamlRoot == null || closedByCode.Count > 0) return;   // no XamlRoot yet: the next render opens it
        var d = new ContentDialog
        {
            XamlRoot = w.RootElement.XamlRoot, Title = spec.Title, Content = spec.Body(), RequestedTheme = w.RootElement.ActualTheme,
            PrimaryButtonText = spec.Primary.Label, CloseButtonText = spec.Close.Label, DefaultButton = ContentDialogButton.Primary,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        if (spec.Secondary != null) d.SecondaryButtonText = spec.Secondary.Label;
        open = d; openKey = spec.Key;
        _ = Show(w, d, spec);
    }

    static async System.Threading.Tasks.Task Show(MainWindow w, ContentDialog d, Spec spec)
    {
        if (d.XamlRoot == null) return;
        ContentDialogResult r;
        try { r = await d.ShowAsync(); }
        catch (Exception e) { App.Log("dialog: " + e.Message); closedByCode.Remove(d); if (open == d) { open = null; openKey = null; } return; }
        if (closedByCode.Remove(d)) { w.Render(); return; }   // hidden because the state moved on: no button action; open the next
        if (open == d) { open = null; openKey = null; }
        var b = r switch { ContentDialogResult.Primary => spec.Primary, ContentDialogResult.Secondary => spec.Secondary, _ => spec.Close };
        if (b == null) return;
        b.Ui?.Invoke();
        if (b.Act != null) w.G.Act(b.Act); else w.Render();
    }

    public static ContentDialog? Open => open;
    public static void Reset() { if (open != null) { closedByCode.Add(open); open.Hide(); } open = null; openKey = null; }

    static UIElement? Leave(MainWindow w, string toId)
    {
        var G = w.G; var S = G.S; var a = G.Left(S.Loc);
        if (!(a > 0) || toId == S.Loc || !(G.IsOn || S.Conn == "paused")) return null;
        var t = new TextBlock { TextWrapping = TextWrapping.WrapWholeWords, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        t.Inlines.Add(new Run { Text = $"{Math.Round(a)} MB", FontWeight = FontWeights.SemiBold });
        t.Inlines.Add(new Run { Text = $" stays at {G.Ex(S.Loc).City} for {Dur((int)Math.Round(G.HeldLeft(S.Loc) / 60))}." });
        return new Border { Background = Theme.Br("ib-lav"), CornerRadius = new CornerRadius(4), Padding = T(12, 8, 12, 8), Child = HS(8, Icons.Icon("savings", 16), t) };
    }

    static Rectangle R(double x, double y, double w, double h, double rx, Brush? fill, Brush? stroke = null, double sw = 0)
    {
        var r = new Rectangle { Width = Math.Max(0, w), Height = h, RadiusX = rx, RadiusY = rx, Fill = fill, Stroke = stroke, StrokeThickness = sw };
        Canvas.SetLeft(r, x); Canvas.SetTop(r, y); return r;
    }
    static TextBlock Tx(double x, double y, string s, bool amt, bool center = true)
    {
        var t = new TextBlock { Text = s, FontSize = amt ? 11 : 10, FontWeight = amt ? FontWeights.SemiBold : FontWeights.Medium, Foreground = Theme.Br(amt ? "text" : "text-2") };
        if (amt) t.FontFamily = Theme.Mono;
        t.Measure(new Windows.Foundation.Size(400, 40));
        Canvas.SetLeft(t, center ? x - t.DesiredSize.Width / 2 : x); Canvas.SetTop(t, y - 12); return t;
    }
    static Path Arrow(double x)
    {
        var p = new Path { Stroke = Theme.Br("text-3"), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        p.Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), $"M{x} 38h12m-5-5 5 5-5 5");
        return p;
    }

    /// <summary>The 600 MB graphic: your balance, the chunk that moves to the exit (quotaGraphic()).</summary>
    static UIElement QuotaGraphic(MainWindow w, string id)
    {
        var G = w.G; var S = G.S;
        double per = COST; var others = S.Alloc.Where(kv => kv.Value > 0 && kv.Key != id).Select(kv => (id: kv.Key, mb: kv.Value)).ToList();
        double total = S.Balance + (S.Free ? per : 0) + others.Sum(o => o.mb), after = S.Free ? S.Balance : S.Balance - per;
        const double W = 244, x0 = 6; double cx = x0, freeMid = x0;
        var cv = new Canvas { Width = 330, Height = 74 };
        cv.Children.Add(R(2, 22, W + 8, 32, 8, null, Theme.Br("text-3"), 1.5));
        cv.Children.Add(R(W + 12, 31, 4, 14, 1.5, Theme.Br("text-3")));
        foreach (var sg in others.Select(o => (o.mb, (string?)o.id)).Append((after, null)))
        {
            var bw = sg.Item1 / total * W; if (bw <= 0) continue; if (sg.Item2 == null) freeMid = cx + bw / 2;
            cv.Children.Add(R(cx, 26, bw - 2, 24, 4, Theme.Br("track")));
            if (sg.Item2 != null && S.Icons != "none" && ExitIcons.For(sg.Item2, S.Icons) is FrameworkElement fi) { fi.Width = 14; fi.Height = 10; ((Border)fi).CornerRadius = new CornerRadius(2); Canvas.SetLeft(fi, cx + bw / 2 - 7); Canvas.SetTop(fi, 55); cv.Children.Add(fi); }
            cx += bw;
        }
        var thisW = Math.Max(36, per / total * W); cx = Math.Min(cx, W + 6 - thisW); var dx = 290 - cx;
        var ghost = R(cx, 26, thisW, 24, 4, Theme.Br("sync")); ghost.Opacity = 0.18; cv.Children.Add(ghost);
        var chunk = R(cx, 26, Math.Max(thisW, 8), 24, 4, Theme.Br("sync")); cv.Children.Add(chunk);
        cv.Children.Add(Tx(cx + thisW / 2, 16, "600 MB", true));
        cv.Children.Add(Arrow(W + 22));
        if (S.Icons != "none" && ExitIcons.For(id, S.Icons) is FrameworkElement big) { big.Width = 38; big.Height = 28; ((Border)big).CornerRadius = new CornerRadius(5); Canvas.SetLeft(big, 288); Canvas.SetTop(big, 24); cv.Children.Add(big); }
        else cv.Children.Add(R(288, 24, 38, 28, 5, Theme.Br("track")));
        cv.Children.Add(Tx(freeMid, 70, Mb(after) + " left", false));
        if (!Anim.Off)
        {
            // the chunk moves from the balance to the exit (q-chunk animation, 1.4 s after 0.3 s)
            var v = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(chunk);
            var a = v.Compositor.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(0, new System.Numerics.Vector3(0, 0, 0)); a.InsertKeyFrame(1, new System.Numerics.Vector3((float)dx, 0, 0), v.Compositor.CreateCubicBezierEasingFunction(new(0, 0), new(0, 1)));
            a.Duration = TimeSpan.FromSeconds(1.4); a.DelayTime = TimeSpan.FromSeconds(0.3);
            chunk.Loaded += (_, _) => v.StartAnimation("Offset", a);
        }
        else Canvas.SetLeft(chunk, cx + dx);
        return new Viewbox { Child = cv, Stretch = Stretch.Uniform, MaxWidth = 452 };
    }

    static UIElement CheckGraphic()
    {
        var cv = new Canvas { Width = 280, Height = 74 };
        cv.Children.Add(R(2, 22, 266, 32, 8, null, Theme.Br("text-3"), 1.5));
        cv.Children.Add(R(272, 31, 4, 14, 1.5, Theme.Br("text-3")));
        for (int i = 0; i < 4; i++)
        {
            cv.Children.Add(R(6 + i * 28, 26, 24, 24, 4, Theme.Br("sync")));
            var dash = R(150 + i * 28, 26, 24, 24, 4, null, Theme.Br("text-3"), 1.4); dash.StrokeDashArray = new DoubleCollection { 3 / 1.4, 3 / 1.4 }; cv.Children.Add(dash);
            var f = R(150 + i * 28, 26, 24, 24, 4, Theme.Br("sync")); f.Opacity = 0; cv.Children.Add(f);
            if (!Anim.Off)
            {
                var v = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(f);
                var a = v.Compositor.CreateScalarKeyFrameAnimation();
                a.InsertKeyFrame(0, 0); a.InsertKeyFrame(0.3f, 1); a.InsertKeyFrame(0.7f, 1); a.InsertKeyFrame(1, 0);
                a.Duration = TimeSpan.FromSeconds(2.4); a.DelayTime = TimeSpan.FromSeconds(i * 0.5); a.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
                f.Loaded += (_, _) => v.StartAnimation("Opacity", a);
            }
        }
        cv.Children.Add(Tx(48, 16, "4 renewed", false));
        cv.Children.Add(Tx(204, 16, "4 more?", true));
        cv.Children.Add(Arrow(122));
        return new Viewbox { Child = cv, Stretch = Stretch.Uniform, MaxWidth = 452 };
    }
}
