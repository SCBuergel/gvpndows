using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GnosisVpn.Engine;

/// <summary>Line-delimited JSON over \\.\pipe\gnosisvpn to the Gnosis VPN service (gvpndows.exe
/// --service, which runs the client core and the tunnel as SYSTEM).</summary>
public sealed class ServiceLink
{
    NamedPipeClientStream? pipe; StreamReader? rd; StreamWriter? wr;
    readonly SemaphoreSlim gate = new(1, 1);
    public bool Connected => pipe?.IsConnected == true;
    public string? LastError { get; private set; }

    /// <param name="retry">resend once on a broken connection: only for requests that can safely repeat</param>
    public async Task<JsonNode?> Send(object cmd, int timeoutMs = 65000, bool retry = true)
    {
        await gate.WaitAsync();
        var t0 = DateTime.UtcNow;
        try
        {
            for (int attempt = 0; attempt < (retry ? 2 : 1); attempt++)
            {
                try
                {
                    if (!Connected)
                    {
                        pipe?.Dispose();
                        pipe = new NamedPipeClientStream(".", "gnosisvpn", PipeDirection.InOut, PipeOptions.Asynchronous);
                        await pipe.ConnectAsync(1500);
                        rd = new StreamReader(pipe, new UTF8Encoding(false)); wr = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    }
                    await wr!.WriteLineAsync(JsonSerializer.Serialize(cmd));
                    var line = await rd!.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
                    if (line == null) throw new IOException("service closed the connection");
                    LastError = null;
                    return JsonNode.Parse(line);
                }
                catch (Exception e) when (attempt == 0 && retry) { LastError = e.Message; pipe?.Dispose(); pipe = null; }
            }
            return null;
        }
        catch (Exception e) { LastError = e.Message; App.Log($"service: {JsonSerializer.Serialize(cmd)} failed after {(DateTime.UtcNow - t0).TotalSeconds:0.0} s: {e.Message}"); pipe?.Dispose(); pipe = null; return null; }
        finally { gate.Release(); }
    }
}

/// <summary>Drives the UI's state (the fake engine's <see cref="State"/>) from the real core: run
/// mode → which screen, the destination list → exits, the connection and its PIX payment state →
/// status, data left and messages. Connect, Disconnect and exit picks go to the service; every other
/// action stays in the UI. Numbers the core does not report (latency, load) are left neutral.</summary>
public sealed class RealBackend
{
    readonly FakeEngine g;
    // separate connections: status polls never wait behind a slow command or the balance query
    readonly ServiceLink link = new(), cmdLink = new(), balLink = new();
    JsonNode? lastBal;
    readonly Action<Action> ui;
    bool polling; double lastBalance = -1e9; string? pendingConnect; double pendingAt;
    public string NodeAddress { get; private set; } = "";
    public string Core { get; private set; } = "connecting to the service";
    public bool ServiceUp => link.Connected;
    public string? LastError => link.LastError;
    /// <summary>MB per wxHOPR, from the exit's PIX chunk price once known (660 MB for 5.94 wxHOPR on prod).</summary>
    double mbPerWx = 660.160512 / 5.941444608;

    static readonly Dictionary<string, string> COUNTRY = new()
    {
        ["DE"] = "Germany", ["LT"] = "Lithuania", ["NL"] = "Netherlands", ["FR"] = "France", ["AT"] = "Austria", ["CH"] = "Switzerland", ["SE"] = "Sweden",
        ["FI"] = "Finland", ["PL"] = "Poland", ["CZ"] = "Czechia", ["GB"] = "United Kingdom", ["US"] = "United States", ["CA"] = "Canada", ["ES"] = "Spain",
        ["PT"] = "Portugal", ["IT"] = "Italy", ["RS"] = "Serbia", ["RO"] = "Romania", ["BG"] = "Bulgaria", ["SK"] = "Slovakia", ["SI"] = "Slovenia", ["HU"] = "Hungary",
        ["BR"] = "Brazil", ["JP"] = "Japan", ["SG"] = "Singapore", ["KR"] = "South Korea", ["IN"] = "India", ["AU"] = "Australia", ["UA"] = "Ukraine", ["DK"] = "Denmark", ["NO"] = "Norway", ["BE"] = "Belgium", ["IE"] = "Ireland",
    };

    public RealBackend(FakeEngine g, Action<Action> ui)
    {
        this.g = g; this.ui = ui;
        var S = g.S;
        S.Gate = false; S.SyncT = 1; S.Balance = 0; S.FirstRun = false; S.Warm = false; S.Mode = "manual";
        g.EX.Clear();
    }

    static double Wei(JsonNode? n) => n == null ? 0 : double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) ? w / 1e18 : 0;
    static long Ms(JsonNode? n) => n == null ? 0 : (long)(double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0);
    static double NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Called by the engine's 250 ms tick; polls the service once a second.</summary>
    public void Tick()
    {
        if (polling) return;
        polling = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var st = await link.Send(new { type = "x_state" }, 5000);
                if (st?["status"]?["data"]?["run_mode"]?["type"]?.ToString() == "running" && FakeEngine.Now - lastBalance > 15000)
                {
                    lastBalance = FakeEngine.Now;
                    _ = Task.Run(async () => { var b = await balLink.Send(new { type = "balance" }, 60000); if (b != null) lastBal = b; });
                }
                var bal = lastBal; lastBal = null;
                ui(() => { try { Apply(st, bal); } catch (Exception e) { App.Log("apply: " + e); } });
                await Task.Delay(1000);
            }
            finally { polling = false; }
        });
    }

    void Apply(JsonNode? st, JsonNode? bal)
    {
        var S = g.S;
        if (st == null)
        {
            Core = "service not reachable: " + (link.LastError ?? "?");
            S.Fail = new Failure { Title = "Can’t reach the Gnosis VPN service", Code = "SERVICE" };
            return;
        }
        if (S.Fail?.Code == "SERVICE") S.Fail = null;
        Core = st["core"]?.ToString() ?? "";
        var data = st["status"]?["data"];
        var mode = data?["run_mode"]?["type"]?.ToString();
        switch (mode)
        {
            case null or "init" or "warmup" or "deploying_safe" or "restarting":
                {
                    S.Gate = false;
                    string[] stages = { "initializing", "validating_config", "identifying_node", "connecting_blockchain", "creating_node", "starting_node", "ready" };
                    var stage = data?["run_mode"]?["stage"]?["type"]?.ToString();
                    var i = Math.Max(0, Array.IndexOf(stages, stage ?? ""));
                    S.SyncT = mode == "deploying_safe" ? 30 : mode == null ? 2 : 13 + i * 14;
                    break;
                }
            case "preparing_safe":
                S.SyncT = null; S.Gate = true;
                NodeAddress = data?["run_mode"]?["node_address"]?.ToString() ?? "";
                break;
            case "running":
                S.SyncT = null; S.Gate = false;
                break;
            default:
                S.SyncT = null; S.Fail = new Failure { Title = "The Gnosis VPN core stopped (" + mode + ")", Code = mode!.ToUpperInvariant() };
                return;
        }
        if (mode != "running") return;

        // exits
        var dests = data?["destinations"]?.AsArray();
        var conn = data?["connection"];
        var curId = conn?["destination"]?.ToString();
        if (dests != null)
        {
            var ex = new List<Exit>();
            foreach (var d in dests)
            {
                var x = d?["destination"]; if (x == null) continue;
                var id = x["id"]?.ToString() ?? "?";
                var cc = x["geo"]?["country"]?.ToString() ?? "";
                var routable = d?["route_health"]?["state"]?["type"]?.ToString() is null or "routable";
                var e = new Exit
                {
                    Id = id, Country = COUNTRY.GetValueOrDefault(cc, cc == "" ? "Unknown" : cc), Place = x["meta"]?["description"]?.ToString() ?? "",
                    Ping = 400, Rel = 2, Spots = 5, Slots = 10, Pv = routable ? 0.95 : 0.5, Fr = 3, Measured = routable, Routable = routable,
                };
                e.City = x["meta"]?["name"]?.ToString() ?? $"{e.Country} #{FakeEngine.Xid(id)}";
                ex.Add(e);
            }
            g.EX.Clear(); g.EX.AddRange(ex);
        }
        if (g.EX.Count > 0 && g.ExOrNull(S.Loc) == null) S.Loc = curId ?? g.EX[0].Id;

        // connection
        var kind = conn?["type"]?.ToString();
        var since = Ms(conn?["since"]);
        if (pendingConnect != null && FakeEngine.Now - pendingAt > 8000) pendingConnect = null;
        switch (kind)
        {
            case "connecting":
                if (S.Conn != "connecting") { S.CStart = FakeEngine.Now - Math.Max(0, NowMs - since); S.CDur = 30000; }
                S.Conn = "connecting"; S.Pause = null; if (curId != null) S.Loc = curId; pendingConnect = null;
                break;
            case "reconnecting":
                S.Conn = "reconnecting"; if (curId != null) S.Loc = curId; pendingConnect = null;
                break;
            case "connected":
                if (S.Conn != "connected" && curId != null) S.Recent = new[] { new Recent { Id = curId, At = S.T } }.Concat(S.Recent.Where(r => r.Id != curId)).Take(6).ToList();
                S.Conn = "connected"; S.Pause = null; if (curId != null) S.Loc = curId; pendingConnect = null;
                S.ConnSec = Math.Max(0, (NowMs - since) / 1000);
                if (g.ExOrNull(S.Loc) is Exit cur && conn?["tunnel_ping_rtt"] is JsonNode rtt) cur.Ping = (int)Ms(rtt);
                break;
            default:
                if (pendingConnect == null) { if (S.Conn != "idle") { S.Conn = "idle"; S.Pause = null; } }
                break;
        }

        // the session's PIX payment: data left at this exit, and the exit holding traffic until it sees the payment
        var pix = conn?["pix"];
        if (pix != null && curId != null)
        {
            var left = Ms(pix["bytes_left"]) / 1e6; var chunk = Ms(pix["chunk_bytes"]) / 1e6; var cost = Wei(pix["cost"]);
            if (left > 0) S.Alloc[curId] = left;
            if (chunk > 0 && cost > 0) mbPerWx = chunk / cost;
        }
        var waiting = conn?["stall"]?["awaiting_payment_since"] != null;
        var pixPhase = pix?["phase"]?["type"]?.ToString();
        if (kind == "connected" && (waiting || pixPhase == "payment_failed"))
        {
            if (S.Flash?.Lead != "Waiting for the exit to see your payment.")
                g.SetFlash("warn", "Waiting for the exit to see your payment.", pixPhase == "payment_failed" ? "The last payment failed; the next one is retried automatically." : "Traffic resumes once it arrives.");
        }
        else if (S.Flash?.Lead == "Waiting for the exit to see your payment.") S.Flash = null;

        // balance: the Safe's wxHOPR, shown in MB at the exit's chunk price
        if (bal?["data"] is JsonNode b && b["type"]?.ToString() == "available")
            S.Balance = Math.Round(Wei(b["safe"]) * mbPerWx);
    }

    /// <summary>Actions the service carries out; true when handled here.</summary>
    public bool Handle(string a, string? id)
    {
        var S = g.S;
        string? dest = a switch
        {
            "connect" => S.Loc,
            "pick2" => id,
            "auto" or "bestAvail" => g.Best(S.Loc)?.Id ?? g.Best()?.Id,
            "renew" or "sheetGo" => S.Loc,
            _ => null,
        };
        if (dest != null)
        {
            if (S.Stack.LastOrDefault() == "pick") S.Stack.RemoveAt(S.Stack.Count - 1);
            S.Sheet = null; S.Loc = dest; S.Conn = "connecting"; S.CStart = FakeEngine.Now; S.CDur = 30000; pendingConnect = dest; pendingAt = FakeEngine.Now;
            if (a == "pick2" || a == "auto") S.Mode = a == "auto" ? "auto" : "manual";
            _ = Task.Run(async () => { var r = await cmdLink.Send(new { type = "connect", destination = dest }, 70000, retry: false); App.Log("service: connect -> " + (r?.ToJsonString() is string j ? j[..Math.Min(200, j.Length)] : "no reply")); });
            return true;
        }
        if (a is "disconnect" or "cancel" or "offDo")
        {
            S.Conn = "idle"; S.Pause = null; S.OffConfirm = false; pendingConnect = null;
            _ = Task.Run(async () => { var r = await cmdLink.Send(new { type = "disconnect" }, 70000, retry: false); App.Log("service: disconnect -> " + (r?.ToJsonString() is string j ? j[..Math.Min(200, j.Length)] : "no reply")); });
            return true;
        }
        // demo-only actions make no sense against a real node
        return a is "preset" or "bal" or "sess" or "evOffline" or "evDrop" or "evPay" or "evUpdate" or "evTwo" or "skip" or "speed" or "gatePaid" or "syncSkip" or "failShow" or "gateRestart" or "restart" or "reset";
    }
}
