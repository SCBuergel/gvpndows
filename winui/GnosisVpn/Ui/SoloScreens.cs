using System;
using System.IO;
using GnosisVpn.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using static GnosisVpn.Engine.FakeEngine;
using static GnosisVpn.Ui.Kit;

namespace GnosisVpn.Ui;

/// <summary>Screens without the navigation pane: fund first (I-1), syncing (I0), failure (E1).</summary>
public static class SoloScreens
{
    static readonly (double at, string text)[] SYNC_STEPS =
    {
        (0, "Looking for other node runners"), (13, "Determining available routes"), (25, "Opening channels"), (40, "Finding return paths"),
        (62, "Checking exit health and capacity"), (88, "Establishing first loop."), (100, "Same exit, a different route each time"),
    };
    public static string SyncStep(double t) { var st = SYNC_STEPS[0].text; foreach (var (a, s) in SYNC_STEPS) if (t >= a) st = s; return st; }
    static string Asset(string f) => Path.Combine(AppContext.BaseDirectory, "Assets", f);
    static StackPanel Col(params UIElement?[] kids)
    {
        var c = V(14, kids); c.MaxWidth = 520; c.HorizontalAlignment = HorizontalAlignment.Center;
        foreach (var k in c.Children) if (k is FrameworkElement fe && fe.HorizontalAlignment == HorizontalAlignment.Stretch) fe.HorizontalAlignment = HorizontalAlignment.Center;
        return c;
    }
    static Grid Center(UIElement c) { var g = new Grid { Padding = T(24, 32, 24, 32), MinHeight = 560 }; c.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); g.Children.Add(c); return g; }

    public static UIElement Build(MainWindow w, string key)
    {
        var G = w.G; var S = G.S;
        if (key == "gate")
        {
            var logo = new Image { Source = new SvgImageSource(new Uri(Asset(Theme.Dark ? "logo-dark.svg" : "logo-light.svg"))), Height = 26, Margin = T(0, 0, 0, 12) };
            var lead = Txt("Your funds pay the independent nodes that carry your traffic. Once they arrive, we find your best route.", 14, "text-2"); lead.TextAlignment = TextAlignment.Center; lead.MaxWidth = 380;
            var gvis = new Image { Source = new SvgImageSource(new Uri(Asset(Theme.Dark ? "gvis-dark.svg" : "gvis-light.svg"))), Width = 260 };
            var c = Col(logo, Title("Fund your VPN"), lead, gvis);
            if (S.Pay != null) { var wb = Info(new Moment("info", "", "Waiting for your payment.", "Usually about 2 minutes.", null), _ => { }, null); wb.HorizontalAlignment = HorizontalAlignment.Stretch; c.Children.Add(wb); }
            c.Children.Add(HS(8, Btn("Open funding site", () => G.Act("gateFund"), "accent lg", trail: "open_in_new"), Btn("Send funds directly", () => G.Act("gateDirect"), "lg")));
            if (G.Real == null) c.Children.Add(Btn("Payment arrives (prototype)", () => G.Act("gatePaid"), "proto"));
            else c.Children.Add(Txt("Node " + G.Real.NodeAddress, 12, "text-2"));
            return Center(c);
        }
        if (key == "sync")
        {
            // The prototype's Lottie (assets/sync-*.json) is not used: Lottie-Windows' loader spends
            // minutes and gigabytes optimizing that file and never shows it. The spinner is the
            // prototype's own fallback (.sync-fb).
            UIElement anim = new Grid { Height = 340, Width = 520, Children = { new ProgressRing { Width = 64, Height = 64, IsActive = !Anim.Off, IsIndeterminate = !Anim.Off, Value = 25 } } };
            var prog = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            prog.Children.Add(w.Live("systep", Txt("", 14, "text-2")));
            var pct = w.Live("sypct", Mono("", 20, bold: true)); pct.HorizontalAlignment = HorizontalAlignment.Right; prog.Children.Add(pct);
            var bar = w.Live("sybar", Bar(0)); bar.HorizontalAlignment = HorizontalAlignment.Stretch;
            var c = Col(Title("Discovering the network"), anim, prog, bar, G.Real == null ? Btn("Skip (prototype)", () => G.Act("syncSkip"), "proto") : w.Live("core", Txt(G.Real.Core, 12, "text-2")));
            // DEBUG FEATURE ONLY: test builds of the service (debug-relay); never shown in a release
            if (G.Real is { DebugRelay: true }) c.Children.Add(Btn("Send log to developer (debug build)", () => G.Real.SendLog(w.ShowSnack), "hyper"));
            prog.Width = 520; bar.Width = 520;
            return Center(c);
        }
        // failure
        var fail = S.Fail!;
        var code = new Microsoft.UI.Xaml.Documents.Run { Text = fail.Code, FontFamily = Theme.Mono, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Br("text") };
        var desc = new TextBlock { FontSize = 12, Foreground = Theme.Br("text-2") };
        desc.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "Failure code " }); desc.Inlines.Add(code);
        var card = Card("cloud_off", fail.Title, null, Btn("Copy code", () =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage(); dp.SetText(fail.Code); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp); G.Act("copyCode");
        }, "", "content_copy"));
        card.Description = desc;
        var col = V(14, Icons.Icon("error", 40, Theme.Br("sev-bad"), true), Txt("Something went wrong", 12, "text-2"), Title("That didn’t work."), card,
            Sub("Troubleshooting"), Txt("Check that this PC is online, then try again. If it keeps failing, the docs or support can help. Mention the failure code."),
            HS(8, Btn("Try again", () => G.Act("failRetry"), "accent"), Btn("Docs", () => G.Act("docs"), "hyper", trail: "open_in_new"), Btn("Support", () => G.Act("support"), "hyper", trail: "open_in_new")));
        // DEBUG FEATURE ONLY: test builds of the service (debug-relay); never shown in a release
        if (G.Real is { DebugRelay: true }) col.Children.Add(Btn("Send log to developer (debug build)", () => G.Real.SendLog(w.ShowSnack), "hyper"));
        ((FrameworkElement)col.Children[0]).HorizontalAlignment = HorizontalAlignment.Left;
        col.MaxWidth = 520; col.HorizontalAlignment = HorizontalAlignment.Center;
        return Center(col);
    }
}
