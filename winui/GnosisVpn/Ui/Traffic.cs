using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GnosisVpn.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using static GnosisVpn.Ui.Kit;

namespace GnosisVpn.Ui;

/// <summary>Live traffic: a port of the prototype's globe visualizer (vizFrame() in common.js) to Win2D.</summary>
public static class Traffic
{
    sealed class Ep { public string Kind = "vpn"; public double[] G = new double[2]; public double Born; public double? Die; public double Ph; }
    static readonly Dictionary<string, double[]> GEO = new() { ["nl"] = new[] { 52.4, 4.9 }, ["at"] = new[] { 48.2, 16.4 }, ["de"] = new[] { 52.5, 13.4 }, ["ch"] = new[] { 46.2, 6.1 }, ["pt"] = new[] { 38.7, -9.1 }, ["se"] = new[] { 59.3, 18.1 }, ["br"] = new[] { -23.5, -46.6 }, ["gb"] = new[] { 51.5, -.1 }, ["pl"] = new[] { 52.2, 21.0 }, ["kr"] = new[] { 37.6, 127.0 } };
    static readonly double[] YOU = { 47.4, 8.5 };
    static long seed = 7;
    static double Vr() { seed = seed * 16807 % 2147483647; return seed / 2147483647.0; }
    static readonly double[][] RELAYS = Enumerable.Range(0, 17).Select(_ => new[] { Vr() * 100 - 30, Vr() * 140 - 40 }).ToArray();
    static readonly List<Ep> eps = new();
    static double[]? cen; static double last, nextChurn, recv, sent, dpk, upk, bwAt; static double? relT0;
    static TextBlock? down, up, dpkT, upkT, u1, u2; static TextBlock? foot;

    static readonly CanvasStrokeStyle Solid = new() { LineJoin = CanvasLineJoin.Round, StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
    static readonly CanvasStrokeStyle Dashed = new() { LineJoin = CanvasLineJoin.Round, CustomDashStyle = new float[] { 3 / 1.1f, 3 / 1.1f } };
    static readonly CanvasTextFormat Label = new() { FontSize = 11, FontWeight = FontWeights.Medium, HorizontalAlignment = CanvasHorizontalAlignment.Center };

    static double[] Vec(double[] g) { double a = g[0] * Math.PI / 180, b = g[1] * Math.PI / 180; return new[] { Math.Cos(a) * Math.Cos(b), Math.Cos(a) * Math.Sin(b), Math.Sin(a) }; }
    static double[] GeoOf(double[] v) => new[] { Math.Asin(Math.Clamp(v[2], -1, 1)) * 180 / Math.PI, Math.Atan2(v[1], v[0]) * 180 / Math.PI };

    public static UIElement Build(MainWindow w)
    {
        var map = new CanvasControl { Width = 420, Height = 400, ClearColor = Microsoft.UI.Colors.Transparent };
        map.Draw += (s, e) => Frame(w, e.DrawingSession, Anim.Off ? 20 : FakeEngine.Now / 1000);
        if (!Anim.Off)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool on = false;
            void Tick(object? _, object __)
            {
                if (!map.IsLoaded) { CompositionTarget.Rendering -= Tick; on = false; return; }   // left the tree: stop for good
                if (sw.ElapsedMilliseconds >= 33) { sw.Restart(); map.Invalidate(); }   // 30 fps
            }
            map.Loaded += (_, _) => { if (!on) { on = true; CompositionTarget.Rendering += Tick; } };
            map.Unloaded += (_, _) => { CompositionTarget.Rendering -= Tick; on = false; map.RemoveFromVisualTree(); };
        }
        var mapBox = Box(new Viewbox { Child = map, Stretch = Stretch.Uniform }, pad: 8);
        TextBlock Big() => new() { FontFamily = Theme.Mono, FontWeight = FontWeights.Black, FontSize = 36, Text = "0.0" };
        down = Big(); up = Big(); dpkT = Mono("0.0", 12, "text-2"); upkT = Mono("0.0", 12, "text-2"); u1 = Txt("Mbit/s", 12, "text-2"); u2 = Txt("Mbit/s", 12, "text-2");
        UIElement Bw(string icon, string label, TextBlock n, TextBlock u, TextBlock pk)
        {
            var k = HS(4, Icons.Icon(icon, 16, Theme.Br("text-2")), new TextBlock { Text = label.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, CharacterSpacing = 60, Foreground = Theme.Br("text-2") });
            var v = HS(6, n, u); u.VerticalAlignment = VerticalAlignment.Bottom; u.Margin = T(0, 0, 0, 6);
            var p = HS(0, Mono("peak ", 12, "text-2"), pk);
            return Box(V(4, k, v, p));
        }
        var bw = new Grid { ColumnSpacing = 8 };
        bw.ColumnDefinitions.Add(new ColumnDefinition()); bw.ColumnDefinitions.Add(new ColumnDefinition());
        var b1 = Bw("south", "Downstream", down, u1, dpkT); var b2 = Bw("north", "Upstream", up, u2, upkT); Grid.SetColumn((FrameworkElement)b2, 1);
        bw.Children.Add(b1); bw.Children.Add(b2);
        var bwBtn = new Button { Content = bw, Padding = new Thickness(0), Background = null, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Tip(bwBtn, "Switch between Mbit/s and MB/s");
        bwBtn.Click += (_, _) => { w.U.VizMb = !w.U.VizMb; bwAt = 0; };
        foot = Txt("", 12, "text-2");
        var grid = new Grid { ColumnSpacing = 16 };
        if (w.PaneMode == "m") { grid.Children.Add(V(16, mapBox, V(12, bwBtn, foot))); }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
            grid.Children.Add(mapBox); var right = V(12, bwBtn, foot); Grid.SetColumn(right, 1); grid.Children.Add(right);
        }
        var head = new Grid(); head.Children.Add(Title("Live traffic")); var avg = Txt("10 s avg", 14, "text-2"); avg.HorizontalAlignment = HorizontalAlignment.Right; avg.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(avg);
        var page = V(12, head, grid);
        page.Padding = w.PaneMode == "x" ? T(40, 20, 40, 32) : T(24, 20, 24, 32); page.MaxWidth = 1060; page.HorizontalAlignment = HorizontalAlignment.Left;
        eps.Clear(); cen = null; dpk = upk = 0; bwAt = -1; nextChurn = 0; last = 0; relT0 = null; recv = sent = 0;
        if (Anim.Off) for (double t = 1; t <= 20; t += 0.25) Simulate(w, t);   // static shots: a settled scene
        return page;
    }

    static double[] ExitGeo(MainWindow w)
    {
        var g = GEO.TryGetValue(w.G.S.Loc, out var x) ? x : new[] { 50.0, 10 };
        var a = Vec(YOU); var b = Vec(g);
        var ang = Math.Acos(Math.Min(1, a[0] * b[0] + a[1] * b[1] + a[2] * b[2])) * 180 / Math.PI;
        return ang >= 34 ? g : new[] { YOU[0] - 14, YOU[1] + 34 };
    }
    static double[] Center(MainWindow w)
    {
        var on = w.G.IsOn; var a = Vec(YOU); var b = Vec(on ? ExitGeo(w) : YOU);
        var m = new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] }; var n = Math.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]); if (n == 0) n = 1;
        var g = GeoOf(m.Select(x => x / n).ToArray());
        return new[] { Math.Clamp(g[0] + 12, -10, 52), g[1] + 12 };
    }

    static void Simulate(MainWindow w, double t)
    {
        if (t > nextChurn)
        {
            var alive = eps.Where(p => p.Die == null).ToList(); var filling = alive.Count < 20;
            nextChurn = t + (filling ? .35 + Vr() * .9 : 1.2 + Vr() * 1.4);
            if (!filling) alive[(int)Math.Floor(Vr() * alive.Count)].Die = t;
            var kind = Vr() < .82 ? "vpn" : "other"; var c = Vr() < .82 ? ExitGeo(w) : YOU;
            eps.Add(new Ep { Kind = kind, G = new[] { Math.Clamp(c[0] + (Vr() - .5) * 120, -60, 70), c[1] + (Vr() - .5) * 150 }, Born = t, Ph = Vr() });
        }
        eps.RemoveAll(p => p.Die != null && t - p.Die >= 1);
    }

    static void Frame(MainWindow w, CanvasDrawingSession ds, double t)
    {
        var G = w.G; var S = G.S;
        double dt = Math.Min(.1, t - (last == 0 ? t : last)); last = t;
        var tgt = Center(w); cen ??= (double[])tgt.Clone();
        cen[0] += (tgt[0] - cen[0]) * Math.Min(1, dt * 1.2); var dl = ((tgt[1] - cen[1] + 540) % 360) - 180; cen[1] += dl * Math.Min(1, dt * 1.2);
        double la0 = cen[0] * Math.PI / 180, lo0 = (cen[1] + 7 * Math.Sin(t / 14)) * Math.PI / 180; const double CX = 210, CY = 200, R = 168;
        Vector3 P(double[] v)
        {
            double x0 = v[0], y0 = v[1], z0 = v[2];
            double x1 = x0 * Math.Cos(-lo0) - y0 * Math.Sin(-lo0), y1 = x0 * Math.Sin(-lo0) + y0 * Math.Cos(-lo0);
            double x = y1, y = z0 * Math.Cos(la0) - x1 * Math.Sin(la0), z = z0 * Math.Sin(la0) + x1 * Math.Cos(la0);
            return new Vector3((float)(CX + R * x), (float)(CY - R * y), (float)z);
        }
        List<Vector3> Arc(double[] ga, double[] gb, double grow = 1, double lift = .18)
        {
            var a = Vec(ga); var b = Vec(gb);
            var om = Math.Acos(Math.Clamp(a[0] * b[0] + a[1] * b[1] + a[2] * b[2], -1, 1)); if (om == 0) om = 1e-6;
            const int n = 30; var pts = new List<Vector3>();
            Vector3 Pt(double f) { double s1 = Math.Sin((1 - f) * om) / Math.Sin(om), s2 = Math.Sin(f * om) / Math.Sin(om), hh = 1 + lift * om * Math.Sin(Math.PI * f); return P(new[] { (a[0] * s1 + b[0] * s2) * hh, (a[1] * s1 + b[1] * s2) * hh, (a[2] * s1 + b[2] * s2) * hh }); }
            grow = Math.Clamp(grow, 0, 1); var k = (int)Math.Floor(n * grow);
            for (int i = 0; i <= k; i++) pts.Add(Pt(i / (double)n));
            if (n * grow - k > 1e-3) pts.Add(Pt(grow));
            return pts;
        }
        Vector2 Along(List<Vector3> pts, double f) { var x = f * (pts.Count - 1); var i = Math.Min(pts.Count - 2, (int)Math.Floor(x)); var u = (float)(x - i); var A = pts[i]; var B = pts[i + 1]; return new Vector2(A.X + (B.X - A.X) * u, A.Y + (B.Y - A.Y) * u); }
        double EaseO(double u) => 1 - Math.Pow(1 - u, 3);
        Color Cl(string k, double op = 1) { var c = Theme.C(k); return Color.FromArgb((byte)(c.A * op), c.R, c.G, c.B); }
        void Line(List<Vector3> pts, Color col, float width, bool dashed = false, bool hideBack = false)
        {
            if (pts.Count < 2) return;
            using var pb = new CanvasPathBuilder(ds);
            bool pen = false;
            foreach (var p in pts)
            {
                if (hideBack && p.Z <= 0) { if (pen) { pb.EndFigure(CanvasFigureLoop.Open); pen = false; } continue; }
                if (!pen) { pb.BeginFigure(p.X, p.Y); pen = true; } else pb.AddLine(p.X, p.Y);
            }
            if (pen) pb.EndFigure(CanvasFigureLoop.Open);
            using var geo = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(geo, col, width, dashed ? Dashed : Solid);
        }
        void Dot(Vector2 c, float r, Color fill, Color? stroke = null, float sw = 2) { ds.FillCircle(c, r, fill); if (stroke != null) ds.DrawCircle(c, r, stroke.Value, sw); }
        Vector2 V2(Vector3 v) => new(v.X, v.Y);

        // globe
        ds.FillCircle((float)CX, (float)CY, (float)R, Cl("sync", .06));
        ds.DrawCircle((float)CX, (float)CY, (float)R, Cl("sync-line"), 1.5f);
        if (!Anim.Off) Simulate(w, t);
        bool on = G.IsOn, conn = S.Conn == "connecting"; var EXG = ExitGeo(w); var e = G.Ex(S.Loc);
        for (int lo = -180; lo < 180; lo += 30) Line(Enumerable.Range(0, 37).Select(i => P(Vec(new[] { -90.0 + i * 5, lo }))).ToList(), Cl("sync-line", .7), .8f, hideBack: true);
        for (int la = -60; la <= 60; la += 30) Line(Enumerable.Range(0, 73).Select(i => P(Vec(new[] { (double)la, -180 + i * 5 }))).ToList(), Cl("sync-line", .7), .8f, hideBack: true);
        var you = P(Vec(YOU)); var exitP = P(Vec(EXG));
        int nRel = 0, nVpn = 0, nOth = 0; bool Vis(Vector3 p) => p.Z > -.05;
        if (on || conn) relT0 ??= Anim.Off ? t - 20 : t; else relT0 = null;
        var dots = new List<Action>(); var pks = new List<Action>();
        if (on || conn)
            for (int i = 0; i < RELAYS.Length; i++)
            {
                var since = t - relT0!.Value - (i * .55 + (i * 37 % 10) * .04); if (since <= 0) continue;
                var pts = Arc(YOU, RELAYS[i], EaseO(Math.Min(1, since / 1)), .12); if (pts.Count < 2) continue;
                var end = pts[^1]; if (!Vis(end)) continue; nRel++;
                Line(pts, Cl("sync", .45), 1);
                dots.Add(() => Dot(V2(end), 4, Cl("sync"), Cl("card")));
                if (i % 3 == 0 && on) { var q = Along(pts, (t * .35 + i * .13) % 1); pks.Add(() => ds.FillCircle(q, 2, Cl("sync-hi"))); }
            }
        bool blocked = !on && S.Lockdown && !S.LdOffPay;
        foreach (var p in eps)
        {
            var age = t - p.Born; var grow = EaseO(Math.Min(1, age / 1.1));
            var fade = (p.Die != null ? Math.Max(0, 1 - (t - p.Die.Value) / 1) : 1) * Math.Min(1, age / .5);
            var viaVpn = p.Kind == "vpn" && on; var from = viaVpn ? EXG : YOU;
            if (blocked) continue;
            if (p.Kind == "vpn" && !on && conn) continue;
            var kind = viaVpn ? "vpn" : "other"; var pts = Arc(from, p.G, grow, .2); if (pts.Count < 2) continue; var end = pts[^1]; if (!Vis(end)) continue;
            fade *= Math.Clamp((end.Z + .05) / .3, 0, 1); if (kind == "vpn") nVpn++; else nOth++;
            var col = kind == "vpn" ? Cl("text-2", .7 * fade) : Cl("sev-warn", fade);
            Line(pts, col, 1.1f, dashed: kind == "other");
            if (grow > .8) { var r = (float)(3.6 * (grow - .8) / .2); var f = fade; dots.Add(() => Dot(V2(end), r, kind == "vpn" ? Cl("text-2", f) : Cl("sev-warn", f), Cl("card", f))); }
            if (grow >= .999 && p.Ph < .45) { var q = Along(pts, (t * .45 + p.Ph * 3) % 1); var f = fade; pks.Add(() => ds.FillCircle(q, 1.8f, kind == "vpn" ? Cl("text", f) : Cl("sev-warn", f))); }
        }
        if (on && Vis(exitP)) Line(Arc(YOU, EXG, 1, .5), Cl("sync"), 2.4f);
        foreach (var a in pks) a();
        foreach (var a in dots) a();
        var fmt = Label;
        if (on && Vis(exitP))
        {
            ds.DrawCircle(V2(exitP), 9, Cl("sync"), 1.5f); Dot(V2(exitP), 5.5f, Cl("sync-hi"), Cl("card"));
            ds.FillRoundedRectangle(exitP.X - 48, exitP.Y + 12, 96, 18, 9, 9, Cl("layer"));
            ds.DrawText(e.Country, exitP.X, exitP.Y + 13, Cl("text"), fmt);
        }
        ds.DrawCircle(V2(you), 11, Cl("text-2"), 1.5f); Dot(V2(you), 7, Cl("text"), Cl("card"));
        ds.DrawText("You", you.X, you.Y + 14, Cl("text"), fmt);

        // bandwidth
        var bb = blocked ? 0 : on ? 240 + 90 * Math.Sin(t / 3) : 60 + 25 * Math.Sin(t / 4);
        recv += ((bb * 1.5 + (blocked ? 0 : 60 * Vr())) - recv) * Math.Min(1, dt * .8);
        sent += ((bb * .42 + (blocked ? 0 : 18 * Vr())) - sent) * Math.Min(1, dt * .8);
        if (Anim.Off) { recv = bb * 1.5 + (blocked ? 0 : 30); sent = bb * .42 + (blocked ? 0 : 9); }
        double dn = recv * 8 / 1000, upv = sent * 8 / 1000; dpk = Math.Max(dpk, dn); upk = Math.Max(upk, upv);
        if ((t - bwAt >= .2 || bwAt < 0) && down != null)
        {
            bwAt = t; var k = w.U.VizMb ? 1 / 8.0 : 1; string F(double v) => (v * k).ToString(w.U.VizMb ? "0.00" : "0.0", System.Globalization.CultureInfo.InvariantCulture);
            down.Text = F(dn); up!.Text = F(upv); dpkT!.Text = F(dpk); upkT!.Text = F(upk); u1!.Text = u2!.Text = w.U.VizMb ? "MB/s" : "Mbit/s";
            var idle = !(on || conn); down.Foreground = up.Foreground = Theme.Br(idle ? "text-3" : "text");
            foot!.Text = $"● {nRel} HOPR relays   ● {nVpn} via VPN   ● {nOth} not via VPN   {nRel + nVpn + nOth} endpoints" + (blocked ? "   Lockdown: nothing leaves while disconnected" : "");
        }
    }
}
