using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using GnosisVpn.Engine;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using static GnosisVpn.Engine.AppEngine;

namespace GnosisVpn.Ui;

/// <summary>Theme colors from the prototype's tokens.json (light / dark).</summary>
public static class Theme
{
    public static bool Dark;
    static readonly Dictionary<string, SolidColorBrush> cacheL = new(), cacheD = new();
    public static Color C(string k) => (Dark ? Tokens.Dark : Tokens.Light).TryGetValue(k, out var c) ? c : Microsoft.UI.Colors.Magenta;
    public static SolidColorBrush Br(string k)
    {
        var cache = Dark ? cacheD : cacheL;
        if (!cache.TryGetValue(k, out var b)) cache[k] = b = new SolidColorBrush(C(k));
        return b;
    }
    public static SolidColorBrush Mix(string k, double alpha) { var c = C(k); return new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B)); }
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
}

/// <summary>Fluent UI System Icons, addressed by the Material names the prototype uses (FMAP in ui.js).</summary>
public static partial class Icons
{
    public static readonly Dictionary<string, string> FMAP = new()
    {
        ["home"] = "home", ["settings"] = "settings", ["arrow_back"] = "arrow_left", ["check"] = "checkmark", ["close"] = "dismiss", ["info"] = "info", ["warning"] = "warning",
        ["error"] = "error_circle", ["check_circle"] = "checkmark_circle", ["pause_circle"] = "pause_circle", ["cloud_off"] = "cloud_off", ["sync_problem"] = "arrow_sync_circle",
        ["lock"] = "lock_closed", ["lock_open"] = "lock_open", ["hourglass_top"] = "hourglass_half", ["radar"] = "radar", ["push_pin"] = "pin", ["account_balance_wallet"] = "wallet",
        ["system_update"] = "arrow_download", ["auto_awesome"] = "sparkle", ["history"] = "history", ["savings"] = "savings", ["content_copy"] = "copy", ["share"] = "share",
        ["open_in_new"] = "open", ["qr_code_2"] = "qr_code", ["chevron_right"] = "chevron_right", ["chevron_left"] = "chevron_left", ["expand_more"] = "chevron_down", ["expand_less"] = "chevron_up",
        ["search"] = "search", ["power_settings_new"] = "power", ["autorenew"] = "arrow_sync", ["travel_explore"] = "globe_search", ["refresh"] = "arrow_clockwise", ["add"] = "add",
        ["public"] = "globe", ["location"] = "globe_location", ["south"] = "arrow_down", ["north"] = "arrow_up", ["download"] = "arrow_download", ["upload"] = "arrow_upload", ["tune"] = "wrench",
        ["visibility_off"] = "eye_off", ["all_inclusive"] = "arrow_repeat_all", ["monitoring"] = "data_usage", ["schedule"] = "clock", ["vpn_key"] = "key", ["key"] = "key",
        ["shield"] = "shield_lock", ["shield_ok"] = "shield_checkmark", ["shield_off"] = "shield_dismiss", ["block"] = "dismiss_circle", ["data_alert"] = "warning", ["search_off"] = "search",
        ["menu"] = "navigation", ["beaker"] = "beaker", ["bell"] = "alert", ["wifi"] = "wifi_1", ["speaker"] = "speaker_2", ["battery"] = "battery_9", ["timer"] = "timer", ["plug"] = "plug_disconnected",
        ["window"] = "window_new", ["apps"] = "apps_list", ["delete"] = "delete", ["person"] = "person", ["startup"] = "power", ["tray"] = "arrow_minimize",
    };
    public static string Data(string name, bool fill = false)
    {
        var f = FMAP.GetValueOrDefault(name, name);
        return Paths.GetValueOrDefault(f + (fill ? ":f" : ":r")) ?? Paths.GetValueOrDefault(f + ":r") ?? Paths["info:r"];
    }
    static Geometry Geo(string d) => (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), d);
    /// <summary>An icon at any size (the paths are on a 20 × 20 grid).</summary>
    public static FrameworkElement Icon(string name, double size = 16, Brush? fg = null, bool fill = false)
    {
        var p = new Path { Data = Geo(Data(name, fill)), Width = 20, Height = 20, Stretch = Stretch.None };
        if (fg != null) p.Fill = fg; else p.SetValue(Shape.FillProperty, Kit.TextBrush);
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 20, Height = 20, Children = { p } } };
    }
    /// <summary>An IconElement (NavigationViewItem, SettingsCard, InfoBar).</summary>
    public static PathIcon PathIcon(string name, bool fill = false) => new() { Data = Geo(Data(name, fill)) };
}

/// <summary>Building blocks for the pages, matching the prototype's controls (ui.js / app.css).</summary>
public static class Kit
{
    public static Brush TextBrush => Theme.Br("text");
    public static Thickness T(double l, double t, double r, double b) => new(l, t, r, b);

    public static TextBlock Txt(string s, double size = 14, string? color = null, bool bold = false, bool wrap = true, FontFamily? font = null)
    {
        var tb = new TextBlock { Text = s, FontSize = size, Foreground = Theme.Br(color ?? "text"), TextWrapping = wrap ? TextWrapping.WrapWholeWords : TextWrapping.NoWrap };
        if (bold) tb.FontWeight = FontWeights.SemiBold;
        if (font != null) tb.FontFamily = font;
        if (!wrap) tb.TextTrimming = TextTrimming.CharacterEllipsis;
        return tb;
    }
    public static TextBlock Title(string s) { var t = Txt(s, 28, bold: true); t.FontFamily = Theme.Display; t.LineHeight = 36; return t; }
    public static TextBlock Sub(string s) { var t = Txt(s, 20, bold: true); t.FontFamily = Theme.Display; return t; }
    public static TextBlock Sub2(string s) => Txt(s, 14, bold: true);
    public static TextBlock Cap(string s, string color = "text-2") => Txt(s, 12, color);
    public static TextBlock Mono(string s, double size = 14, string? color = null, bool bold = false) => Txt(s, size, color, bold, false, Theme.Mono);

    /// <summary>"**Lead** text" in one paragraph. {retry} and other live tokens are filled by the caller.</summary>
    public static TextBlock Rich(string lead, string text, double size = 14)
    {
        var tb = new TextBlock { FontSize = size, TextWrapping = TextWrapping.WrapWholeWords, Foreground = TextBrush };
        tb.Inlines.Add(new Run { Text = lead, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(text)) tb.Inlines.Add(new Run { Text = " " + text });
        return tb;
    }

    public static StackPanel V(double spacing, params UIElement?[] kids)
    {
        var p = new StackPanel { Spacing = spacing };
        foreach (var k in kids) if (k != null) p.Children.Add(k);
        return p;
    }
    public static StackPanel HS(double spacing, params UIElement?[] kids)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var k in kids) if (k != null) p.Children.Add(k);
        return p;
    }
    public static T Tip<T>(T el, string? tip) where T : DependencyObject { if (!string.IsNullOrEmpty(tip)) ToolTipService.SetToolTip(el, tip); return el; }

    /// <summary>Buttons: kind contains accent | subtle | hyper | lg | sm.</summary>
    public static ButtonBase Btn(string label, Action? click, string kind = "", string? icon = null, string? trail = null, bool disabled = false, string? tip = null)
    {
        bool lg = kind.Contains("lg"), sm = kind.Contains("sm");
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        double isz = lg ? 18 : 16;
        Brush? fg = kind.Contains("accent") ? Theme.Br("on-accent") : kind.Contains("hyper") ? Theme.Br("accent-t") : null;
        if (icon != null) content.Children.Add(Icons.Icon(icon, isz, fg ?? (disabled ? Theme.Br("text-dis") : null)));
        if (!string.IsNullOrEmpty(label)) content.Children.Add(new TextBlock { Text = label, FontSize = sm ? 13 : 14, FontWeight = lg ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
        if (trail != null) content.Children.Add(Icons.Icon(trail, 16, fg));
        ButtonBase b;
        if (kind.Contains("hyper"))
            b = new HyperlinkButton { Content = content, Foreground = Theme.Br("accent-t"), Padding = T(8, 4, 8, 5) };
        else
        {
            var bt = new Button { Content = content };
            if (kind.Contains("accent")) bt.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            if (kind.Contains("subtle")) { bt.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); bt.BorderThickness = new Thickness(0); }
            b = bt;
        }
        if (lg) { b.MinHeight = 40; b.MinWidth = 200; b.Padding = T(20, 8, 20, 8); }
        else if (sm) { b.MinHeight = 28; b.Padding = T(10, 3, 10, 4); }
        b.IsEnabled = !disabled;
        if (click != null) b.Click += (_, _) => click();
        return Tip(b, tip);
    }

    public static Button IconBtn(string icon, string label, Action click, bool fill = false)
    {
        var b = new Button { Content = Icons.Icon(icon, 16, null, fill), Width = 36, Height = 32, Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, label);
        b.Click += (_, _) => click();
        return Tip(b, label);
    }

    /// <summary>Pills: tone "" | sel | ok | bad.</summary>
    public static Border Pill(string text, string? icon = null, string tone = "")
    {
        var (bg, fg) = tone switch { "sel" => ("ib-lav", "sev-info"), "ok" => ("ib-ok", "sev-ok"), "bad" => ("ib-bad", "sev-bad"), _ => ("sel", "text-2") };
        var row = HS(4);
        if (icon != null) row.Children.Add(Icons.Icon(icon, 12, Theme.Br(fg)));
        row.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.Br(fg), VerticalAlignment = VerticalAlignment.Center });
        return new Border { Height = 22, Padding = T(8, 0, 8, 0), CornerRadius = new CornerRadius(11), Background = Theme.Br(bg), Child = row, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    }

    /// <summary>Signal bars for an exit's grade (bars() in common.js); chk = the "checking" animation.</summary>
    public static FrameworkElement Bars(AppEngine g, Exit e, bool chk = false)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        var gr = g.GradeOf(e);
        if (!chk && gr.K == "hidden") return p;
        int[] hs = { 4, 7, 10, 12 };
        string on = gr.Tone switch { "ok" => "sig-ok", "warn" => "sev-warn", "bad" => "sev-bad", _ => "track" };
        for (int i = 0; i < 4; i++)
        {
            var b = new Border { Width = 3, Height = hs[i], CornerRadius = new CornerRadius(1), VerticalAlignment = VerticalAlignment.Bottom };
            if (chk) b.Background = Theme.Br("track");
            else if (gr.K == "unmeasured") { b.BorderBrush = Theme.Br("text-3"); b.BorderThickness = new Thickness(1); }
            else b.Background = Theme.Br(i < gr.N ? on : "track");
            p.Children.Add(b);
        }
        if (chk) Anim.Pulse(p);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(p, chk ? "Checking" : gr.K == "unmeasured" ? "Not measured yet" : $"{gr.Label}, {gr.N} of 4");
        return p;
    }

    /// <summary>The quality line next to an exit. A weak route gets no words (it read as alarming);
    /// its bars still show it.</summary>
    public static TextBlock QLine(AppEngine g, Exit e)
    {
        var gr = g.GradeOf(e);
        return Txt(gr.K == "weak" ? "" : gr.Label, 12, "text-2");
    }

    public static Brush PingBrush(int p) => Theme.Br(p < 500 ? "sev-ok" : p < 900 ? "sev-warn" : "sev-bad");

    /// <summary>A settings card (.sc in the prototype, the Windows Settings card): icon, title and
    /// description, the control on the right; optionally clickable with a chevron.</summary>
    public sealed class CardEl : ContentControl
    {
        public object? Description { set { if (desc != null) { desc.Inlines.Clear(); if (value is TextBlock tb) { foreach (var i in tb.Inlines.ToList()) { tb.Inlines.Remove(i); desc.Inlines.Add(i); } } else desc.Text = value?.ToString() ?? ""; desc.Visibility = Visibility.Visible; } } }
        internal TextBlock? desc;
    }
    public static CardEl Card(string? icon, string title, string? desc, object? ctl, Action? click = null, bool disabled = false, string? bg = null)
    {
        var g = new Grid { ColumnSpacing = 16, MinHeight = 40, VerticalAlignment = VerticalAlignment.Center };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (icon != null) { var ic = Icons.Icon(icon, 20); ic.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(ic); }
        var t = V(2, Txt(title, 14));
        var d = Txt(desc ?? "", 12, "text-2"); d.Visibility = string.IsNullOrEmpty(desc) ? Visibility.Collapsed : Visibility.Visible; t.Children.Add(d);
        t.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(t, 1); g.Children.Add(t);
        var right = HS(8); right.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(right, 2); g.Children.Add(right);
        if (ctl is UIElement ue) right.Children.Add(ue); else if (ctl is string str) right.Children.Add(Txt(str, 14, "text-2", wrap: false));
        if (click != null && ctl == null) right.Children.Add(Icons.Icon("chevron_right", 16, Theme.Br("text-2")));
        var border = new Border { Background = Theme.Br(bg ?? "card"), BorderBrush = Theme.Br("card-bd"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = T(16, 12, 16, 12), MinHeight = 64, Child = g };
        var c = new CardEl { Content = border, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !disabled, desc = d };
        if (disabled) border.Opacity = 0.5;
        if (click != null && !disabled)
        {
            border.PointerEntered += (_, _) => border.Background = Theme.Br("ctrl-h");
            border.PointerExited += (_, _) => border.Background = Theme.Br(bg ?? "card");
            border.PointerPressed += (_, _) => border.Background = Theme.Br("ctrl-p");
            border.Tapped += (_, _) => click();
            c.IsTabStop = true; c.KeyDown += (_, e) => { if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space) click(); };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(c, title);
        }
        return c;
    }

    /// <summary>Toggle with the On/Off label on its left, as in Windows Settings.</summary>
    public static FrameworkElement Toggle(bool on, Action flip, string label, bool disabled = false)
    {
        var t = new ToggleSwitch { IsOn = on, IsEnabled = !disabled, MinWidth = 0, OnContent = "", OffContent = "", Width = 52 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(t, label);
        t.Toggled += (_, _) => { if (t.IsOn != on) flip(); };
        var l = new TextBlock { Text = on ? "On" : "Off", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, MinWidth = 22, TextAlignment = TextAlignment.Right, Foreground = Theme.Br(disabled ? "text-dis" : "text") };
        var row = HS(12, l, t); row.VerticalAlignment = VerticalAlignment.Center;
        return row;
    }

    /// <summary>The prototype's SelectorBar: text tabs, the selected one bold with an accent underline.</summary>
    public static FrameworkElement Selector(IEnumerable<(string v, string label, string? tip)> opts, string sel, Action<string> pick)
    {
        var row = HS(4);
        foreach (var (v, l, tip) in opts)
        {
            bool on = v == sel;
            var tb = new TextBlock { Text = l, FontSize = 14, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Theme.Br(on ? "text" : "text-2") };
            var bar = new Border { Width = 16, Height = 3, CornerRadius = new CornerRadius(2), Background = on ? Theme.Br("accent") : null, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = T(0, 0, 0, 2) };
            var g = new Grid { Height = 36, Padding = T(12, 0, 12, 0) };
            tb.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(tb); g.Children.Add(bar);
            var b = new Button { Content = g, Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), MinHeight = 0 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, l);
            b.Click += (_, _) => { if (v != sel) pick(v); };
            row.Children.Add(Tip(b, tip));
        }
        return row;
    }

    public static ComboBox Combo(IEnumerable<(string key, string label)> opts, string value, Action<string> pick)
    {
        var list = opts.ToList();
        var cb = new ComboBox { MinWidth = 180 };
        foreach (var o in list) cb.Items.Add(o.label);
        cb.SelectedIndex = Math.Max(0, list.FindIndex(o => o.key == value));
        cb.SelectionChanged += (_, _) => { var k = list[cb.SelectedIndex].key; if (k != value) pick(k); };
        return cb;
    }

    /// <summary>A Home-page message as a WinUI InfoBar in the prototype's tones (ok, warn, bad, info, lav).</summary>
    public static InfoBar Info(Moment m, Action<string> act, Action<string>? close, Func<string, string>? live = null)
    {
        var sev = m.Tone switch { "ok" => InfoBarSeverity.Success, "warn" => InfoBarSeverity.Warning, "bad" => InfoBarSeverity.Error, _ => InfoBarSeverity.Informational };
        var ib = new InfoBar { IsOpen = true, Severity = sev, IsClosable = m.Own && close != null, Title = m.Lead, Message = live != null ? live(m.Text) : m.Text };
        if (m.Tone is "info" or "lav")
        {
            ib.Background = Theme.Br(m.Tone == "lav" ? "ib-lav" : "ib-info");
            ib.Resources["InfoBarInformationalSeverityIconBackground"] = Theme.Br("sev-info");
        }
        if (m.Btns is { Count: > 0 })
        {
            var row = HS(6);
            for (int i = 0; i < m.Btns.Count; i++) { var b = m.Btns[i]; row.Children.Add(Btn(b.Label, () => act(b.Act), i == 0 ? "sm" : "subtle sm")); }
            ib.Content = new Border { Margin = T(0, 0, 0, 10), Child = row };
        }
        if (ib.IsClosable) ib.CloseButtonClick += (_, _) => close!(m.Lead);
        return ib;
    }

    public static Border Box(UIElement child, string bg = "card", double pad = 16, double radius = 6)
        => new() { Background = Theme.Br(bg), BorderBrush = Theme.Br("card-bd"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(radius), Padding = new Thickness(pad), Child = child };

    public static ProgressBar Bar(double pct, string color = "accent", double height = 3)
        => new() { Value = pct, Maximum = 100, Height = height, MinHeight = height, Foreground = Theme.Br(color), Background = Theme.Br("track"), CornerRadius = new CornerRadius(height / 2) };
}

/// <summary>Small looping animations (checking bars, spinning status icon, blinking dots).</summary>
public static class Anim
{
    public static bool Off;   // --shot: static screenshots
    public static void Pulse(UIElement el, double from = 1, double to = 0.35, double sec = 0.6)
    {
        if (Off) return;
        var v = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(el);
        var a = v.Compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, (float)from); a.InsertKeyFrame(0.5f, (float)to); a.InsertKeyFrame(1, (float)from);
        a.Duration = TimeSpan.FromSeconds(sec * 2); a.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        v.StartAnimation("Opacity", a);
    }
    public static void Spin(FrameworkElement el, double sec = 1.6)
    {
        if (Off) return;
        el.SizeChanged += (_, _) =>
        {
            var v = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(el);
            v.CenterPoint = new System.Numerics.Vector3((float)el.ActualWidth / 2, (float)el.ActualHeight / 2, 0);
            var a = v.Compositor.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(0, 0); a.InsertKeyFrame(1, 360, v.Compositor.CreateLinearEasingFunction());
            a.Duration = TimeSpan.FromSeconds(sec); a.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
            v.StartAnimation("RotationAngleInDegrees", a);
        };
    }
}
