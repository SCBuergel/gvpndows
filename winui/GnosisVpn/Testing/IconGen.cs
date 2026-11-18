using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GnosisVpn.Ui;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Svg;
using Windows.UI;

namespace GnosisVpn.Testing;

/// <summary>`GnosisVPN.exe --gen-icons DIR`: renders the app icon (the prototype's .appi: dark
/// gradient tile with the lime key) and the notification-area icons (shield, colored by state) with
/// Win2D, as PNG and multi-size ICO. Run once; the files are committed under Assets/.</summary>
public static class IconGen
{
    static string Svg(string pathKey, string fill, double size) =>
        $"<svg xmlns='http://www.w3.org/2000/svg' width='{size}' height='{size}' viewBox='0 0 20 20'><path fill='{fill}' d='{Icons.Paths[pathKey]}'/></svg>";

    static byte[] Png(CanvasDevice dev, int px, Action<CanvasDrawingSession, float> draw)
    {
        using var rt = new CanvasRenderTarget(dev, px, px, 96);
        using (var ds = rt.CreateDrawingSession()) { ds.Clear(Microsoft.UI.Colors.Transparent); draw(ds, px); }
        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        rt.SaveAsync(ms, CanvasBitmapFileFormat.Png).AsTask().Wait();
        var bytes = new byte[ms.Size]; using var r = new Windows.Storage.Streams.DataReader(ms.GetInputStreamAt(0)); r.LoadAsync((uint)ms.Size).AsTask().Wait(); r.ReadBytes(bytes);
        return bytes;
    }

    static void Ico(string path, List<(int size, byte[] png)> imgs)
    {
        using var f = new BinaryWriter(File.Create(path));
        f.Write((short)0); f.Write((short)1); f.Write((short)imgs.Count);
        int off = 6 + 16 * imgs.Count;
        foreach (var (s, png) in imgs) { f.Write((byte)(s >= 256 ? 0 : s)); f.Write((byte)(s >= 256 ? 0 : s)); f.Write((byte)0); f.Write((byte)0); f.Write((short)1); f.Write((short)32); f.Write(png.Length); f.Write(off); off += png.Length; }
        foreach (var (_, png) in imgs) f.Write(png);
    }

    public static bool RunIfRequested()
    {
        var a = Environment.GetCommandLineArgs(); var i = Array.IndexOf(a, "--gen-icons");
        if (i < 0 || i + 1 >= a.Length) return false;
        var dir = a[i + 1]; Directory.CreateDirectory(dir);
        var dev = CanvasDevice.GetSharedDevice();
        void AppTile(CanvasDrawingSession ds, float s)
        {
            var r = s * 0.25f;
            using var g = new CanvasLinearGradientBrush(ds, Color.FromArgb(255, 0x2a, 0x23, 0x47), Color.FromArgb(255, 0x12, 0x0f, 0x22)) { StartPoint = new(0, 0), EndPoint = new(s, s) };
            ds.FillRoundedRectangle(0, 0, s, s, r, r, g);
            using var doc = CanvasSvgDocument.LoadFromXml(dev, Svg("key:f", "#9fd34a", s * 0.56));
            var o = s * 0.22f; ds.Transform = System.Numerics.Matrix3x2.CreateTranslation(o, o); ds.DrawSvg(doc, new Windows.Foundation.Size(s * 0.56, s * 0.56)); ds.Transform = System.Numerics.Matrix3x2.Identity;
        }
        var app = new List<(int, byte[])>();
        foreach (var s in new[] { 16, 24, 32, 48, 64, 256 }) { var png = Png(dev, s, AppTile); app.Add((s, png)); if (s == 32 || s == 256 || s == 48) File.WriteAllBytes(Path.Combine(dir, $"appicon-{s}.png"), png); }
        Ico(Path.Combine(dir, "app.ico"), app);
        // tray: shield (filled), colored by state, with a dot for connected / paused (tray-dot in app.css)
        var states = new Dictionary<string, (string fill, string? dot)> { ["off"] = ("#8a8992", null), ["ok"] = ("#8DC60C", "#8DC60C"), ["warn"] = ("#fce100", null), ["bad"] = ("#ff99a4", "#c42b1c") };
        foreach (var (st, (fill, dot)) in states)
        {
            var imgs = new List<(int, byte[])>();
            foreach (var s in new[] { 16, 20, 24, 32, 48 })
                imgs.Add((s, Png(dev, s, (ds, px) =>
                {
                    using var doc = CanvasSvgDocument.LoadFromXml(dev, Svg("shield_lock:f", fill, px));
                    ds.DrawSvg(doc, new Windows.Foundation.Size(px, px));
                    if (dot != null) { var d = px * 0.34f; ds.FillCircle(px - d / 2, px - d / 2, d / 2 + px * 0.06f, Color.FromArgb(255, 0x20, 0x20, 0x24)); ds.FillCircle(px - d / 2, px - d / 2, d / 2, Microsoft.UI.ColorHelper.FromArgb(255, Convert.ToByte(dot[1..3], 16), Convert.ToByte(dot[3..5], 16), Convert.ToByte(dot[5..7], 16))); }
                })));
            Ico(Path.Combine(dir, $"tray-{st}.ico"), imgs);
        }
        File.WriteAllText(Path.Combine(dir, "done.txt"), "ok");
        return true;
    }
}
