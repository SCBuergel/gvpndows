using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using static GnosisVpn.Engine.AppEngine;

namespace GnosisVpn.Ui;

/// <summary>Exit icons: a pixel bot per exit generated from its id (botURI() in common.js, same hash
/// and random sequence, so each exit gets the same bot as in the prototype), the Auto icon, or a flag.</summary>
public static class ExitIcons
{
    static (Color c1, Color c2, Color bg) Palette() => Theme.Dark
        ? (Color.FromArgb(255, 0xA8, 0xA8, 0xCA), Color.FromArgb(255, 0xD2, 0xD0, 0xE9), Color.FromArgb(36, 0xA8, 0xA8, 0xCA))
        : (Color.FromArgb(255, 0x64, 0x67, 0x94), Color.FromArgb(255, 0xA8, 0xA8, 0xCA), Color.FromArgb(255, 0xEC, 0xEB, 0xF5));

    /// <summary>Pixel map x,y -> 1 (body) or 2 (highlight) on a 7 × 7 grid, mirrored.</summary>
    public static Dictionary<(int x, int y), int> BotPixels(string id)
    {
        uint h = Hash32("bot:" + id);
        int R(int n) { var v = (int)(h % (uint)n); h = unchecked((h ^ (h >> 13)) * 2654435761u); return v; }
        var px = new Dictionary<(int, int), int>();
        void Set(int x, int y, int c) { px[(x, y)] = c; px[(6 - x, y)] = c; }
        int wide = R(2), top = 1 + R(2), bot = top + 2 + R(2);
        for (int y = top; y <= bot; y++) for (int x = wide == 1 ? 0 : 1; x <= 3; x++) Set(x, y, 1);
        switch (R(4)) { case 0: Set(3, top - 1, 2); break; case 1: Set(1, top - 1, 2); break; case 2: Set(2, top - 1, 1); break; }
        int ey = top + (bot - top > 2 ? 1 : 0), ex0 = 1 + R(2);
        int ec = Math.Min(ex0 + 1, 2);
        px.Remove((ec, ey)); px.Remove((6 - ec, ey));
        if (R(2) == 1) Set(3, bot - (R(2) == 1 ? 0 : 1), 2);
        if (wide == 0 && R(2) == 1) Set(0, top + 1, 2);
        switch (R(4))
        {
            case 0: Set(1, bot + 1, 1); break;
            case 1: Set(2, bot + 1, 1); break;
            case 2: Set(1, bot + 1, 2); Set(3, bot + 1, 2); break;
            case 3: Set(0, bot + 1, 1); Set(2, bot + 1, 1); break;
        }
        if (R(3) == 0) Set(wide == 1 ? 1 : 2, top, 2);
        return px;
    }

    static FrameworkElement Grid(double vw, double vh, IEnumerable<(double x, double y, bool hi)> cells)
    {
        var (c1, c2, bg) = Palette();
        var cv = new Canvas { Width = vw, Height = vh, Background = new SolidColorBrush(bg) };
        SolidColorBrush b1 = new(c1), b2 = new(c2);
        foreach (var (x, y, hi) in cells)
        {
            var r = new Rectangle { Width = 1.04, Height = 1.04, Fill = hi ? b2 : b1 };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y); cv.Children.Add(r);
        }
        return new Viewbox { Stretch = Stretch.UniformToFill, Child = cv };
    }

    public static FrameworkElement Bot(string id)
    {
        var cells = new List<(double, double, bool)>();
        foreach (var kv in BotPixels(id)) if (kv.Key.y is >= 0 and <= 6) cells.Add((kv.Key.x + 2.5, kv.Key.y + 1, kv.Value == 2));
        return Grid(12, 9, cells);
    }

    public static FrameworkElement Auto()
    {
        var g = new char[11, 13];
        for (int y = 0; y < 11; y++) for (int x = 0; x < 13; x++) g[y, x] = '.';
        var ys = new int[13];
        for (int x = 0; x < 13; x++) ys[x] = (int)Math.Round(9 * (Math.Exp(3.4 * x / 12) - 1) / (Math.Exp(3.4) - 1), MidpointRounding.AwayFromZero);
        int? prev = null;
        for (int x = 0; x < 13; x++)
        {
            int y = Math.Min(10, 10 - ys[x]);
            if (prev != null) for (int yy = Math.Min(prev.Value, y); yy <= Math.Max(prev.Value, y); yy++) g[yy, x] = '#';
            g[y, x] = '#'; prev = y;
        }
        int ey = 10 - ys[12];
        for (int dy = 0; dy < 2; dy++) for (int dx = -1; dx < 1; dx++) if (ey + dy < 11) g[ey + dy, 12 + dx] = 'o';
        var cells = new List<(double, double, bool)>();
        for (int y = 0; y < 11; y++) for (int x = 0; x < 13; x++) if (g[y, x] != '.') cells.Add((x + 1, y + 1, g[y, x] == 'o'));
        return Grid(15, 13, cells);
    }

    static string AssetPath(params string[] p) => System.IO.Path.Combine(new[] { AppContext.BaseDirectory, "Assets" }.Concat(p));
    static ImageSource? Flag(string id)
    {
        foreach (var ext in new[] { "svg", "png", "webp" })
        {
            var f = AssetPath("flags", $"{id}.{ext}");
            if (!File.Exists(f)) continue;
            return ext == "svg" ? new SvgImageSource(new Uri(f)) : new BitmapImage(new Uri(f));
        }
        return null;
    }

    /// <summary>xicon(): 32 × 24 (or 48 × 36 when large), rounded, by the "Exit icons" setting.</summary>
    public static FrameworkElement? For(string id, string iconsSetting, bool large = false)
    {
        if (iconsSetting == "none") return null;
        double w = large ? 48 : 32, h = large ? 36 : 24;
        FrameworkElement inner;
        if (id == "auto") inner = Auto();
        else if (iconsSetting == "flag" && Flag(id) is ImageSource src) inner = new Image { Source = src, Stretch = Stretch.UniformToFill };
        else inner = Bot(id);
        return new Border { Width = w, Height = h, CornerRadius = new CornerRadius(large ? 6 : 4), Background = Theme.Br("sel"), Child = inner, VerticalAlignment = VerticalAlignment.Center };
    }
}

static class PathExt
{
    public static string[] Concat(this string[] a, string[] b) { var r = new string[a.Length + b.Length]; a.CopyTo(r, 0); b.CopyTo(r, a.Length); return r; }
}
