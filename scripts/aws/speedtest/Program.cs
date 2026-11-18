// Transfer test with per-second throughput, for VPN runs on the test server.
//   speedtest down URL BYTES MAX_S OUT.csv     GET, counts body bytes as they arrive
//   speedtest up   URL BYTES MAX_S OUT.csv     POST of BYTES random bytes, counts bytes handed to the socket
// Each second one CSV row: second, bytes in that second, Mbit/s, total bytes. Stops at BYTES or MAX_S.
using System.Diagnostics;
using System.Net;

if (args.Length < 5) { Console.Error.WriteLine("usage: speedtest down|up URL BYTES MAX_S OUT.csv"); return 2; }
var (dir, url, total, maxS, outPath) = (args[0], args[1], long.Parse(args[2]), int.Parse(args[3]), args[4]);
var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
http.DefaultRequestHeaders.UserAgent.ParseAdd("gvpndows-speedtest/1");
var cts = new CancellationTokenSource(TimeSpan.FromSeconds(maxS));
long done = 0, lastDone = 0; var sw = Stopwatch.StartNew(); var rows = new List<string> { "second,bytes,mbit_s,total_bytes" };
var tick = new Timer(_ =>
{
    var d = Interlocked.Read(ref done); var n = d - lastDone; lastDone = d;
    var line = $"{rows.Count},{n},{n * 8 / 1e6:0.00},{d}"; lock (rows) rows.Add(line); Console.WriteLine(line);
}, null, 1000, 1000);
string end = "complete"; string? err = null;
try
{
    if (dir == "down")
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
        var buf = new byte[256 * 1024];
        while (done < total) { var r = await s.ReadAsync(buf, cts.Token); if (r == 0) break; Interlocked.Add(ref done, r); }
    }
    else
    {
        var content = new CountingContent(total, n => Interlocked.Add(ref done, n), cts.Token);
        using var resp = await http.PostAsync(url, content, cts.Token);
        err = $"HTTP {(int)resp.StatusCode}";
        if (!resp.IsSuccessStatusCode) end = "error";
    }
}
catch (OperationCanceledException) { end = "max time"; }
catch (Exception e) { end = "error"; err = e.Message; }
tick.Dispose();
var secs = sw.Elapsed.TotalSeconds; var tot = Interlocked.Read(ref done);
var summary = $"# {dir} {end}: {tot / 1e6:0.0} MB in {secs:0.0} s = {tot * 8 / 1e6 / secs:0.00} Mbit/s average{(err != null ? " (" + err + ")" : "")}";
lock (rows) { rows.Add(summary); File.WriteAllLines(outPath, rows); }
Console.WriteLine(summary);
return end == "error" ? 1 : 0;

sealed class CountingContent(long total, Action<int> sent, CancellationToken ct) : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var buf = new byte[64 * 1024]; Random.Shared.NextBytes(buf); long left = total;
        while (left > 0) { var n = (int)Math.Min(buf.Length, left); await stream.WriteAsync(buf.AsMemory(0, n), ct); await stream.FlushAsync(ct); sent(n); left -= n; }
    }
    protected override bool TryComputeLength(out long length) { length = total; return true; }
}
