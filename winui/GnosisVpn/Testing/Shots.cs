using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using GnosisVpn.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GnosisVpn.Testing;

/// <summary>
/// Deep links and test screenshots (debug harness).
///   GnosisVPN.exe --hash s07-d-pick-dark            open in that state (same tokens as the prototype)
///   GnosisVPN.exe --shots LIST.txt OUTDIR            one PNG per line "name hash [WxH]", then exit
/// Each shot starts from a fresh engine (like a page load of the prototype), applies the deep link,
/// lays the window out at the size (default 1180x650), waits, and renders the window plus any open
/// dialog or flyout (popups) to a PNG with RenderTargetBitmap: EC2's display is only 800x600, so
/// the capture comes from the XAML tree, not from the screen.
/// </summary>
public static class Shots
{
    public static bool Active;
    static readonly Dictionary<string, (int w, int h)> SIZES = new() { ["wide"] = (1180, 650), ["std"] = (960, 640), ["compact"] = (720, 620), ["narrow"] = (520, 640), ["max"] = (1366, 720) };

    static string? Arg(string name)
    {
        var a = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    /// <summary>applyHash() in common.js, plus the Windows tokens (PF.hash in ui.js).</summary>
    public static (int w, int h)? Apply(MainWindow w, string hash)
    {
        var G = w.G; var S = G.S; (int, int)? size = null;
        var presets = new HashSet<string> { "s00", "s01", "s07", "s08", "s10", "s13", "s14", "s18c", "l1", "arc" };
        var pagesMap = new Dictionary<string, string> { ["pick"] = "pick", ["set"] = "settings", ["add"] = "add", ["payout"] = "payout", ["direct"] = "direct", ["traffic"] = "traffic", ["demo"] = "demo" };
        foreach (var p in hash.TrimStart('#').ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            S = G.S;
            if (presets.Contains(p)) { S.Gate = false; G.Preset(p); }
            else if (p == "i1") G.Act("gateRestart");
            else if (p == "i0") { S.Gate = false; G.Act("restart"); }
            else if (p == "e1") { S.Gate = false; G.Act("failShow"); }
            else if (p == "d") { S.Simple = false; S.Details = true; }
            else if (p == "simple") { S.Simple = true; S.Details = false; }
            else if (p is "dark" or "light") w.U.Mode = p;
            else if (pagesMap.TryGetValue(p, out var pg)) G.Go(pg);
            else if (p == "usage") { G.Go("settings"); S.Stab = "usage"; }
            else if (p == "balance") S.Sheet = new Engine.Sheet { Type = "balance" };
            else if (p == "off") S.OffConfirm = true;
            else if (p == "codes") w.U.Codes = true;
            else if (p == "x60") S.Speed = 60;
            else if (p == "connect") G.Act("connect");
            else if (p == "static") Anim.Off = true;
            else if (p is "tray" or "menu" or "nc") w.U.Fly = p == "menu" ? "ctx" : p;
            else if (p == "closed") w.U.WinState = "closed";
            else if (p == "quit") w.U.WinState = "quit";
            else if (SIZES.TryGetValue(p, out var z)) size = z;
            else if (p == "closewin") { var t = w.DispatcherQueue.CreateTimer(); t.Interval = TimeSpan.FromSeconds(2); t.IsRepeating = false; MainWindow.Pending.Add(t); t.Tick += (_, _) => { MainWindow.Pending.Remove(t); w.Close(); }; t.Start(); }
            else if (p == "traymenu") { var t = w.DispatcherQueue.CreateTimer(); t.Interval = TimeSpan.FromSeconds(3); t.IsRepeating = false; MainWindow.Pending.Add(t); t.Tick += (_, _) => { MainWindow.Pending.Remove(t); Tray.OpenMenuAtIcon(); }; t.Start(); }
            else if (p == "trayfly") w.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Tray.OpenFlyout(w));
        }
        w.Render();
        return size;
    }

    public static async Task RunIfRequested(MainWindow w)
    {
        if (Arg("--hash") is string h) { var sz = Apply(w, h); if (sz != null) w.SetSize(sz.Value.w, sz.Value.h); }
        // real-service tests: periodic window captures, and one connect as a click would do it
        if (Arg("--snap") is string snapDir) _ = Snapper(w, snapDir);
        if (Arg("--click-connect") is string q) _ = ClickConnect(w, q.ToLowerInvariant());
        var list = Arg("--shots"); var outDir = Arg("--shots") is string ? Environment.GetCommandLineArgs().SkipWhile(a => a != "--shots").Skip(2).FirstOrDefault() : null;
        if (list == null || outDir == null) return;
        Active = true;
        WindowSizer.AllowLarge(WinRT.Interop.WindowNative.GetWindowHandle(w));
        w.AppWindow.Move(new Windows.Graphics.PointInt32(0, 0));
        Directory.CreateDirectory(outDir);
        var log = new List<string>();
        foreach (var raw in File.ReadAllLines(list))
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || raw.StartsWith("#")) continue;
            var (name, hash) = (parts[0], parts[1]);
            try
            {
                Anim.Off = false;
                w.ResetForShot();
                var size = Apply(w, hash) ?? (1180, 650);
                if (parts.Length > 2 && parts[2].Split('x') is [var a, var b]) size = (int.Parse(a), int.Parse(b));
                w.SetSize(size.Item1, size.Item2);
                await Task.Delay(900);
                w.Render();
                await Task.Delay(700);
                if (w.U.WinState == "closed") { log.Add($"skip {name}: window closed to the notification area (desktop screenshot covers it)"); continue; }
                var cap = Capture(w, Path.Combine(outDir, name + ".png"));
                if (await Task.WhenAny(cap, Task.Delay(20000)) != cap) { log.Add($"FAIL {name}: capture did not finish in 20 s (is the session locked or the display off?)"); File.WriteAllLines(Path.Combine(outDir, "shots.log"), log); continue; }
                await cap;
                var pops = VisualTreeHelper.GetOpenPopupsForXamlRoot(w.RootElement.XamlRoot);
                var pinfo = string.Join(",", pops.Select(pp => $"{pp.Child?.GetType().Name}:{(pp.Child as FrameworkElement)?.ActualWidth:0}x{(pp.Child as FrameworkElement)?.ActualHeight:0}"));
                log.Add($"ok {name} {size.Item1}x{size.Item2} code={Engine.Messages.ScreenCode(w.G, w.U.Fly, w.U.WinState).Code} dialog={(Dialogs.Open != null)} popups=[{pinfo}]");
            }
            catch (Exception e) { log.Add($"FAIL {name}: {e}"); }
            File.WriteAllLines(Path.Combine(outDir, "shots.log"), log);   // after every shot: a hang shows where
        }
        File.WriteAllLines(Path.Combine(outDir, "shots.log"), log);
        w.Quit();
    }

    static async Task Snapper(MainWindow w, string dir)
    {
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 360; i++)
        {
            await Task.Delay(10000);
            try { var c = Capture(w, Path.Combine(dir, $"snap-{i:D3}.png")); if (await Task.WhenAny(c, Task.Delay(15000)) == c) await c; } catch (Exception e) { App.Log("snap: " + e.Message); }
            File.AppendAllText(Path.Combine(dir, "snap.log"), $"{DateTime.UtcNow:HH:mm:ss} {i:D3} conn={w.G.S.Conn} loc={w.G.S.Loc} core={w.G.Real?.Core} code={Engine.Messages.ScreenCode(w.G).Code}{Environment.NewLine}");
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
