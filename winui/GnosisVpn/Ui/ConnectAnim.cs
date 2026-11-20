using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GnosisVpn.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace GnosisVpn.Ui;

/// <summary>The connecting animation: a port of connectSVG() in common.js to Win2D. One timeline of
/// the connect duration (12 s at ×1): channels open to peers, then the route through one relay to
/// the exit draws in, the exit lights up and packets start to flow.</summary>
public static class ConnectAnim
{
    const float CX = 150, CY = 46, R = 40, K = R / 54;
    static Vector2 Sc(float x, float y) => new(CX + (x - 150) * K, CY + (y - 62) * K);
    static readonly Vector2[] GN = new (float, float)[] { (118, 34), (142, 22), (168, 30), (130, 58), (158, 50), (184, 64), (124, 86), (150, 80), (176, 92), (196, 40) }.Select(p => Sc(p.Item1, p.Item2)).ToArray();
    static readonly Vector2 YOU = Sc(108, 98), EXT = Sc(200, 46);

    // created once: Win2D objects hold native memory, so nothing is allocated per frame that is not disposed
    static readonly CanvasStrokeStyle Round = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
    // Labels are drawn without the scale transform, at whole pixels and a font size already scaled
    // (TextScale), so their glyphs land on the same pixels in every frame.
    const float TEXT = 7;
    static readonly CanvasTextFormat FB = new() { FontWeight = FontWeights.SemiBold, HorizontalAlignment = CanvasHorizontalAlignment.Right, WordWrapping = CanvasWordWrapping.NoWrap };
    static readonly CanvasTextFormat FN = new() { FontWeight = FontWeights.Medium, WordWrapping = CanvasWordWrapping.NoWrap };
    static readonly CanvasTextFormat FNB = new() { FontWeight = FontWeights.SemiBold, WordWrapping = CanvasWordWrapping.NoWrap };
    static readonly CanvasTextFormat FC = new() { FontWeight = FontWeights.Medium, HorizontalAlignment = CanvasHorizontalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };

    static double EaseIO(double p) => p < .5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;

    public static FrameworkElement Build(MainWindow w)
    {
        // A fixed size, so the drawing's scale never follows the page's width (a scroll bar coming and
        // going, a rebuild measuring the page differently) and nothing moves between frames.
        var c = new CanvasControl { Width = 300, Height = 150, HorizontalAlignment = HorizontalAlignment.Center, ClearColor = Microsoft.UI.Colors.Transparent };
        c.Draw += (s, e) => Draw(w, s, e.DrawingSession);
        if (!Anim.Off)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool on = false;
            void Tick(object? _, object __)
            {
                if (!c.IsLoaded) { CompositionTarget.Rendering -= Tick; on = false; return; }   // left the tree: stop for good
                if (sw.ElapsedMilliseconds >= 33) { sw.Restart(); c.Invalidate(); }   // 30 fps
            }
            c.Loaded += (_, _) => { if (!on) { on = true; CompositionTarget.Rendering += Tick; } };
            c.Unloaded += (_, _) => { CompositionTarget.Rendering -= Tick; on = false; c.RemoveFromVisualTree(); };
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(c, $"Connecting: opening channels to relays, then routing through one relay to the exit in {w.G.Ex(w.G.S.Loc).City}");
        return c;
    }

    /// <summary>Quadratic curve a → b with the control point lifted above both ends (q() in common.js).</summary>
    static (Vector2 a, Vector2 c, Vector2 b) Q(Vector2 a, Vector2 b, float lift) => (a, new Vector2((a.X + b.X) / 2, Math.Min(a.Y, b.Y) - lift), b);
    static Vector2 At((Vector2 a, Vector2 c, Vector2 b) q, float t) { var u = 1 - t; return u * u * q.a + 2 * u * t * q.c + t * t * q.b; }
    static List<Vector2> Pts(IEnumerable<(Vector2, Vector2, Vector2)> segs, int n = 24) { var l = new List<Vector2>(); foreach (var s in segs) for (int i = (l.Count == 0 ? 0 : 1); i <= n; i++) l.Add(At(s, i / (float)n)); return l; }
    static Vector2 Along(List<Vector2> pts, double f) { var x = Math.Clamp(f, 0, 1) * (pts.Count - 1); var i = Math.Min(pts.Count - 2, (int)x); var u = (float)(x - i); return pts[i] + (pts[i + 1] - pts[i]) * u; }

    static void Draw(MainWindow w, CanvasControl c, CanvasDrawingSession ds)
    {
        var S = w.G.S; var e = w.G.Ex(S.Loc);
        double T = Anim.Off ? 3 : AppEngine.Now / 1000, D = (S.CDur > 0 ? S.CDur : 12000) / 1000;
        double el = Anim.Off ? D * 0.85 : (AppEngine.Now - S.CStart) / 1000;
        float scale = (float)Math.Min(c.Width / 220, c.Height / 92);   // the fixed size, not ActualWidth
        float ox = (float)Math.Round((c.Width - 220 * scale) / 2);
        var view = Matrix3x2.CreateTranslation(-40, 0) * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(ox, 0);
        ds.Transform = view;
        Color Cl(string k, double op = 1) { var col = Theme.C(k); return Color.FromArgb((byte)(col.A * Math.Clamp(op, 0, 1)), col.R, col.G, col.B); }
        double Tl(double f, double d, double from, double to) { var t0 = f * D; if (el < t0) return from; var p = d > 0 ? EaseIO(Math.Min(1, (el - t0) / d)) : 1; return from + (to - from) * p; }
        double Loop(double dur, double ph = 0) => ((T + ph) % dur) / dur;

        int h = (int)Math.Floor(S.CStart);
        int[] hops = new[] { new[] { 3, 1, 4, 5 }, new[] { 0, 4, 2, 7 }, new[] { 6, 1, 5, 4 }, new[] { 3, 7, 2, 8 } }[((h % 4) + 4) % 4];
        int ci = hops[1];
        var peers = hops.Concat(Enumerable.Range(0, 10).Where(i => !hops.Contains(i)).OrderBy(a => (a * 7 + h) % 11).Take(2)).ToArray();
        var routes = hops.Select((i, k) => Pts(new[] { Q(YOU, GN[i], 10 + k * 2), Q(GN[i], EXT, 8 + k) })).ToList();
        double LegAt(int k) => .27 + k * .115; double HopLit(int k) => LegAt(k) + .07; const double EXIT_AT = .72;

        // globe, meridians, stars
        using (var grad = new CanvasRadialGradientBrush(ds, Cl("sync", .16), Cl("sync", .02)) { Center = new Vector2(CX, CY - R * 0.1f), RadiusX = R * 1.1f, RadiusY = R * 1.1f })
            ds.FillCircle(CX, CY, R, grad);
        ds.DrawCircle(CX, CY, R, Cl("sync-line"), 1.2f);
        ds.DrawEllipse(CX, CY, R, R * .32f, Cl("sync-line"), 1);
        ds.DrawEllipse(CX, CY - R * .45f, R * .82f, R * .22f, Cl("sync-line", .7), 1);
        ds.DrawEllipse(CX, CY + R * .45f, R * .82f, R * .22f, Cl("sync-line", .7), 1);
        for (int k = 0; k < 3; k++) { var p = Anim.Off ? k / 3.0 : Loop(12, k * 4); var rx = (float)(R - (R - 2) * EaseIO(p < .5 ? p * 2 : (1 - p) * 2)); ds.DrawEllipse(CX, CY, rx, R, Cl("sync-line"), 1); }
        for (int k = 0; k < 34; k++)
        {
            var a = (k * 137.5) % 360 * Math.PI / 180; var rr = R * Math.Sqrt(((k * 53) % 100) / 100.0);
            float x = (float)(CX + rr * Math.Cos(a)), y = (float)(CY + rr * Math.Sin(a) * .92);
            double op = k % 5 == 0 ? .5 : .28; if (k % 7 == 0 && !Anim.Off) { var d = 2 + k % 3; var p = Loop(d, k); op = .15 + .55 * (p < .5 ? p * 2 : (1 - p) * 2); }
            ds.FillCircle(x, y, k % 5 == 0 ? 1.2f : .7f, Cl("sync", op));
        }
        var round = Round;
        void Poly(List<Vector2> pts, double frac, Color col, float width)
        {
            frac = Math.Clamp(frac, 0, 1); if (frac <= 0) return;
            var n = Math.Max(2, (int)Math.Ceiling(pts.Count * frac));
            using var pb = new CanvasPathBuilder(ds); pb.BeginFigure(pts[0]);
            for (int i = 1; i < n; i++) pb.AddLine(pts[i]);
            pb.AddLine(Along(pts, frac)); pb.EndFigure(CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(pb); ds.DrawGeometry(g, col, width, round);
        }
        // channels to peers
        for (int k = 0; k < peers.Length; k++)
        {
            var i = peers[k]; var pts = Pts(new[] { Q(YOU, GN[i], 8 + k * 2) }); var f = .02 + k * .035;
            var op = Tl(.27, 1.2, .75, .28);
            Poly(pts, Tl(f, .9, 0, 1), Cl("sync", op), 1);
            var dotOp = Math.Min(Tl(f + .08, .4, 0, .85), Tl(.27, 1.2, .85, .25) > 0 && el >= .27 * D ? Tl(.27, 1.2, .85, .25) : 1);
            if (dotOp > 0) { var lp = Anim.Off ? .5 : Loop(1.5 + k * .17, k * .4); var u = EaseIO(lp < .5 ? lp * 2 : (1 - lp) * 2); ds.FillCircle(Along(pts, u), 1.3f, Cl("sync-hi", Tl(f + .08, .4, 0, .85) * (el >= .27 * D ? Tl(.27, 1.2, 1, .3) : 1))); }
        }
        for (int i = 0; i < GN.Length; i++)
        {
            var k = Array.IndexOf(peers, i); var hi = Array.IndexOf(hops, i);
            double op = k >= 0 ? Tl(.02 + k * .035 + .07, .5, .55, 1) : .55; float r = 2.3f; var col = Cl("sync-node", op);
            if (hi >= 0 && el >= HopLit(hi) * D) { r = (float)Tl(HopLit(hi), .5, 2.3, 3.4); col = Cl("sync-hi", op); }
            ds.FillCircle(GN[i], r, col);
        }
        // the route legs, then packets
        for (int k = 0; k < routes.Count; k++)
        {
            var fr = Tl(LegAt(k), D * .1, 0, 1);
            Poly(routes[k], fr, Cl("sync", k > 0 ? .08 : .14), k > 0 ? 4 : 7);
            Poly(routes[k], fr, Cl("sync", k > 0 ? .7 : 1), k > 0 ? 1.2f : 2);
        }
        for (int k = 0; k < routes.Count; k++)
        {
            var op = Tl(Math.Max(EXIT_AT + .03, LegAt(k) + .1), .4, 0, 1); if (op <= 0) continue;
            var p = Anim.Off ? (k * .25 + .1) % 1 : Loop(2.4, k * .6);
            ds.FillCircle(Along(routes[k], EaseIO(p)), 2.4f, Cl("sync-hi", op));
        }
        void Ring(Vector2 pt, double f)
        {
            ds.DrawCircle(pt, 6.5f, Cl("sync-node"), 1.3f); ds.FillCircle(pt, 3.2f, Cl("sync-node"));
            var op = Tl(f, .6, 0, 1); ds.DrawCircle(pt, 6.5f, Cl("sync", op), 1.3f); ds.FillCircle(pt, 3.2f, Cl("sync-hi", op));
        }
        Ring(YOU, 0); Ring(EXT, EXIT_AT);
        // labels: at whole device pixels, without the transform, in grayscale (steady in every frame)
        ds.Transform = Matrix3x2.Identity;
        ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
        float fs = (float)Math.Round(TEXT * scale * 2) / 2;
        void Label(string s, float x, float y, Color col, CanvasTextFormat f)
        {
            var p = Vector2.Transform(new Vector2(x, y), view);
            f.FontSize = fs;
            ds.DrawText(s, (float)Math.Round(p.X), (float)Math.Round(p.Y), col, f);
        }
        Label("You", YOU.X - 12, YOU.Y - 5, Cl("text"), FB);
        var hop = GN[ci]; Label("1 hop", hop.X, hop.Y + 5, Cl("text-2", Tl(.3, .6, 0, 1)), FC);
        Label(e.Country, EXT.X + 13, EXT.Y - 5, Cl("text"), FNB);
        Label("Exit", EXT.X + 13, EXT.Y + 6, Cl("text-2"), FN);
    }
}
