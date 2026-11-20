using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using GnosisVpn.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GnosisVpn.Testing;

/// <summary>
/// Test drivers for the real app on a test machine (they act on the live service like a user would):
///   --snap DIR                    a screenshot every 10 s plus a line of state in DIR/snap.log
///   --click-connect QUERY         clicks Connect on the first exit whose name contains QUERY
///   --click-disconnect-after N    ... and Disconnect N seconds after it is connected
///   --act NAME                    runs the UI action NAME once the service has answered (e.g. fundPortal)
/// Screenshots come from the XAML tree (RenderTargetBitmap plus open popups), not from the screen.
/// </summary>
public static class Shots
{
    static string? Arg(string name)
    {
        var a = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    public static Task RunIfRequested(MainWindow w)
    {
        if (Arg("--snap") is string snapDir) _ = Snapper(w, snapDir);
        if (Arg("--click-connect") is string q) _ = ClickConnect(w, q.ToLowerInvariant());
        if (Arg("--act") is string act) _ = ActWhenReady(w, act);
        return Task.CompletedTask;
    }

    static async Task ActWhenReady(MainWindow w, string act)
    {
        for (int i = 0; i < 300 && (w.G.S.Fail != null || w.G.S.SyncT != null || w.G.Real.Core == "connecting to the service"); i++) await Task.Delay(1000);
        await Task.Delay(3000);
        App.Log($"test: action {act}");
        w.G.Act(act);
    }

    static async Task Snapper(MainWindow w, string dir)
    {
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 360; i++)
        {
            await Task.Delay(10000);
            try { var c = Capture(w, Path.Combine(dir, $"snap-{i:D3}.png")); if (await Task.WhenAny(c, Task.Delay(15000)) == c) await c; } catch (Exception e) { App.Log("snap: " + e.Message); }
            File.AppendAllText(Path.Combine(dir, "snap.log"), $"{DateTime.UtcNow:HH:mm:ss} {i:D3} conn={w.G.S.Conn} loc={w.G.S.Loc} core={w.G.Real.Core} code={Engine.Messages.ScreenCode(w.G)}{Environment.NewLine}");
        }
    }

    static async Task ClickConnect(MainWindow w, string q)
    {
        for (int i = 0; i < 900; i++)
        {
            await Task.Delay(2000);
            var S = w.G.S;
            if (w.BaseKey() != "home" || w.G.EX.Count == 0) continue;
            var e = w.G.EX.FirstOrDefault(x => x.City.ToLowerInvariant().Contains(q) || x.Country.ToLowerInvariant().Contains(q) || x.Id.ToLowerInvariant().Contains(q));
            if (e == null) continue;
            App.Log($"test: connecting to {e.City} ({e.Id}) as a click on Connect");
            S.Loc = e.Id; w.G.Act("connect");
            if (Arg("--click-disconnect-after") is string secs && int.TryParse(secs, out var n))
            {
                for (int k = 0; k < 600 && w.G.S.Conn != "connected"; k++) await Task.Delay(1000);
                App.Log($"test: connected in the UI; disconnecting in {n} s as a click on Disconnect");
                await Task.Delay(n * 1000);
                w.G.Act("disconnect");
            }
            return;
        }
    }

    /// <summary>Renders the window content and every open popup (ContentDialog, Flyout) into one PNG.</summary>
    public static async Task Capture(MainWindow w, string outPath)
    {
        var root = w.RootElement;
        int W = (int)Math.Round(root.ActualWidth), H = (int)Math.Round(root.ActualHeight);
        var (basePx, bw, bh) = await Render(root);
        var canvas = new byte[W * H * 4];
        bool dark = root.ActualTheme == ElementTheme.Dark;
        // the Mica backdrop is not in the XAML tree: start from the theme's Mica color (tokens.json "mica")
        var mica = Theme.C("mica");
        for (int i = 0; i < canvas.Length; i += 4) { canvas[i] = mica.B; canvas[i + 1] = mica.G; canvas[i + 2] = mica.R; canvas[i + 3] = 255; }
        Blend(canvas, W, H, basePx, bw, bh, 0, 0);
        foreach (var p in VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot))
        {
            if (p.Child is not FrameworkElement child || child.ActualWidth < 1) continue;
            var pt = child.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
            var (px, pw, ph) = await Render(child);
            Blend(canvas, W, H, px, pw, ph, (int)Math.Round(pt.X), (int)Math.Round(pt.Y));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        using var fs = new InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fs);
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)W, (uint)H, 96, 96, canvas);
        await enc.FlushAsync();
        fs.Seek(0);
        var bytes = new byte[fs.Size];
        await fs.ReadAsync(bytes.AsBuffer(), (uint)fs.Size, InputStreamOptions.None);
        File.WriteAllBytes(outPath, bytes);
    }

    static async Task<(byte[] px, int w, int h)> Render(UIElement el)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(el);
        return ((await rtb.GetPixelsAsync()).ToArray(), rtb.PixelWidth, rtb.PixelHeight);
    }

    /// <summary>Source-over of premultiplied BGRA pixels at (ox, oy).</summary>
    static void Blend(byte[] dst, int W, int H, byte[] src, int sw, int sh, int ox, int oy)
    {
        for (int y = 0; y < sh; y++)
        {
            int dy = y + oy; if (dy < 0 || dy >= H) continue;
            for (int x = 0; x < sw; x++)
            {
                int dx = x + ox; if (dx < 0 || dx >= W) continue;
                int s = (y * sw + x) * 4, d = (dy * W + dx) * 4; int a = src[s + 3];
                if (a == 0) continue;
                for (int c = 0; c < 3; c++) dst[d + c] = (byte)Math.Min(255, src[s + c] + dst[d + c] * (255 - a) / 255);
                dst[d + 3] = 255;
            }
        }
    }
}
