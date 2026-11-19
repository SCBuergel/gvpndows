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
    /// <summary>Debug: the step a request is in (logged by the poll watchdog when a poll stalls).</summary>
    public volatile string Step = "idle";

    /// <param name="retry">resend once on a broken connection: only for requests that can safely repeat</param>
    public async Task<JsonNode?> Send(object cmd, int timeoutMs = 65000, bool retry = true)
    {
        Step = "gate";
        await gate.WaitAsync();
        var t0 = DateTime.UtcNow;
        try
        {
            for (int attempt = 0; attempt < (retry ? 2 : 1); attempt++)
            {
                try
                {
                    Step = "check connected";
                    if (!Connected)
                    {
                        Step = "connect";
                        pipe?.Dispose();
                        // exactly what the service grants interactive users: read plus FILE_WRITE_DATA
                        // (GENERIC_WRITE would also ask for FILE_APPEND_DATA, the create-instance right)
                        pipe = new NamedPipeClientStream(".", "gnosisvpn", PipeAccessRights.Read | PipeAccessRights.WriteData | PipeAccessRights.Synchronize,
                            PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Anonymous, HandleInheritability.None);
                        await pipe.ConnectAsync(1500);
                        Step = "verify";
                        ServiceCheck.Verify(pipe);
                        rd = new StreamReader(pipe, new UTF8Encoding(false)); wr = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    }
                    Step = "write";
                    await wr!.WriteLineAsync(JsonSerializer.Serialize(cmd));
                    Step = "read";
                    var line = await rd!.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
                    Step = "parse";
                    if (line == null) throw new IOException("service closed the connection");
                    LastError = null;
                    return JsonNode.Parse(line);
                }
                catch (Exception e) when (attempt == 0 && retry) { LastError = e.Message; pipe?.Dispose(); pipe = null; }
            }
            return null;
        }
        catch (Exception e) { LastError = e.Message; App.Log($"service: {JsonSerializer.Serialize(cmd)} failed after {(DateTime.UtcNow - t0).TotalSeconds:0.0} s: {e.Message}"); pipe?.Dispose(); pipe = null; return null; }
        finally { Step = "idle"; gate.Release(); }
    }
}

/// <summary>Trust check: the pipe's server must be the running GnosisVPN service (the process the
/// Service Control Manager reports for it). Any local process could otherwise create
/// \\.\pipe\gnosisvpn first and make the app show its answers.</summary>
public static class ServiceCheck
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr OpenSCManager(string? machine, string? db, uint access);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatusEx(IntPtr svc, int level, byte[] buf, int size, out int needed);
    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    static extern bool CloseServiceHandle(IntPtr h);

    /// <summary>Process id of the GnosisVPN service, or 0 if it is not running.</summary>
    public static uint ServicePid()
    {
        var scm = OpenSCManager(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
        if (scm == IntPtr.Zero) return 0;
        try
        {
            var svc = OpenService(scm, "GnosisVPN", 0x0004 /* SERVICE_QUERY_STATUS */);
            if (svc == IntPtr.Zero) return 0;
            try
            {
                var buf = new byte[36];   // SERVICE_STATUS_PROCESS
                if (!QueryServiceStatusEx(svc, 0 /* SC_STATUS_PROCESS_INFO */, buf, buf.Length, out _)) return 0;
                return BitConverter.ToUInt32(buf, 4) == 4 /* SERVICE_RUNNING */ ? BitConverter.ToUInt32(buf, 28) : 0;
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    public static void Verify(NamedPipeClientStream pipe)
    {
        if (Environment.GetEnvironmentVariable("GNOSISVPN_TRUST_PIPE") == "1") return;   // developer: --serve in a console
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server)) throw new IOException("cannot identify the pipe's server");
        var svc = ServicePid();
        if (svc == 0 || svc != server) throw new UnauthorizedAccessException($"\\\\.\\pipe\\gnosisvpn is not served by the Gnosis VPN service (server pid {server}, service pid {svc})");
    }
}

/// <summary>Drives the UI's state (<see cref="State"/>) from the real core: run
/// mode → which screen, the destination list → exits, the connection and its PIX payment state →
/// status, data left and messages. Connect, Disconnect and exit picks go to the service; every other
/// action stays in the UI. Numbers the core does not report (latency, load) are left neutral.</summary>
public sealed class RealBackend
{
    readonly AppEngine g;
    // separate connections: status polls never wait behind a slow command or the balance query
    readonly ServiceLink link = new(), cmdLink = new(), balLink = new(), logLink = new();
    double lastForward;
    JsonNode? lastBal;
    readonly Action<Action> ui;
    string? lastMode;
    /// <summary>Route health per exit id, from the last status (why an exit cannot be reached).</summary>
    readonly Dictionary<string, JsonNode?> health = new();
    string? attemptDest; bool attemptSeen; double attemptAt; int polls; double lastPollLog, pollStart, lastStuckLog; volatile string afterSend = "";
    bool polling; double lastBalance = -1e9; string? pendingConnect; double pendingAt;
    const double PENDING_MS = 180_000;
    public string NodeAddress { get; private set; } = "";
    /// <summary>Funds on the node and what the core asks for before it can start (wxHOPR, xDAI), while it waits for funds.</summary>
    public double HaveWx { get; private set; }
    public double HaveXdai { get; private set; }
    public double NeedWx { get; private set; }
    public double NeedXdai { get; private set; }
    /// <summary>Size of one paid chunk of data at the current exit (MB).</summary>
    public double ChunkMb { get; private set; } = 660;
    public string Core { get; private set; } = "connecting to the service";
    public bool ServiceUp => link.Connected;
    /// <summary>DEBUG FEATURE ONLY: the service is a test build that streams its log to the
    /// developer (Cargo feature debug-relay). Release builds report false and the app then shows no
    /// "Send log to developer" button.</summary>
    public bool DebugRelay { get; private set; }
    public string? LastError => link.LastError;
    /// <summary>MB per wxHOPR: the status's pix_price_per_byte, else the session's bytes bought per
    /// cost (both cover every chunk committed) once known (660 MB for 5.94 wxHOPR on prod).</summary>
    double mbPerWx = 660.160512 / 5.941444608;

    static readonly Dictionary<string, string> COUNTRY = new()
    {
        ["DE"] = "Germany", ["LT"] = "Lithuania", ["NL"] = "Netherlands", ["FR"] = "France", ["AT"] = "Austria", ["CH"] = "Switzerland", ["SE"] = "Sweden",
        ["FI"] = "Finland", ["PL"] = "Poland", ["CZ"] = "Czechia", ["GB"] = "United Kingdom", ["US"] = "United States", ["CA"] = "Canada", ["ES"] = "Spain",
        ["PT"] = "Portugal", ["IT"] = "Italy", ["RS"] = "Serbia", ["RO"] = "Romania", ["BG"] = "Bulgaria", ["SK"] = "Slovakia", ["SI"] = "Slovenia", ["HU"] = "Hungary",
        ["BR"] = "Brazil", ["JP"] = "Japan", ["SG"] = "Singapore", ["KR"] = "South Korea", ["IN"] = "India", ["AU"] = "Australia", ["UA"] = "Ukraine", ["DK"] = "Denmark", ["NO"] = "Norway", ["BE"] = "Belgium", ["IE"] = "Ireland",
    };

    public RealBackend(AppEngine g, Action<Action> ui)
    {
        this.g = g; this.ui = ui;
        var S = g.S;
        S.Gate = false; S.SyncT = 1; S.Balance = 0; S.Mode = "manual";
        g.EX.Clear();
    }

    static double Wei(JsonNode? n) => n == null ? 0 : double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) ? w / 1e18 : 0;
    static long Ms(JsonNode? n) => n == null ? 0 : (long)(double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0);
    static double NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Called by the engine's 250 ms tick; polls the service once a second.</summary>
    public void Tick()
    {
        if (polling)
        {
            if (AppEngine.Now - pollStart > 10000 && AppEngine.Now - lastStuckLog > 10000) { lastStuckLog = AppEngine.Now; App.Log($"poll stuck for {(AppEngine.Now - pollStart) / 1000:0} s at step {link.Step} (poll #{polls}, after-send {afterSend})"); }
            return;
        }
        polling = true; pollStart = AppEngine.Now; afterSend = "send";
        _ = Task.Run(async () =>
        {
            try
            {
                var st = await link.Send(new { type = "x_state" }, 5000);
                polls++; afterSend = "got reply";
                if (AppEngine.Now - lastPollLog > 60000) { lastPollLog = AppEngine.Now; App.Log($"poll #{polls}: {(st == null ? "no reply" : "mode " + (st["status"]?["data"]?["run_mode"]?["type"]?.ToString() ?? "-") + ", core " + st["core"])}"); }
                if (st?["status"]?["data"]?["run_mode"]?["type"]?.ToString() == "running" && AppEngine.Now - lastBalance > 15000)
                {
                    lastBalance = AppEngine.Now;
                    _ = Task.Run(async () => { var b = await balLink.Send(new { type = "balance" }, 60000); if (b != null) lastBal = b; });
                }
                // DEBUG FEATURE ONLY: the app's log goes along with the service's log stream
                if (DebugRelay && !App.Forward.IsEmpty && AppEngine.Now - lastForward > 10000)
                {
                    lastForward = AppEngine.Now;
                    var lines = new List<string>();
                    while (lines.Count < 50 && App.Forward.TryDequeue(out var l)) lines.Add(l);
                    _ = Task.Run(() => logLink.Send(new { type = "x_app_log", lines }, 5000, retry: false));
                }
                afterSend = "balance"; var bal = lastBal; lastBal = null;
                afterSend = "dispatch";
                ui(() => { try { Apply(st, bal); } catch (Exception e) { App.Log("apply: " + e); } });
                afterSend = "delay";
                await Task.Delay(1000);
                afterSend = "done";
            }
            catch (Exception e) { App.Log("poll: " + e); }
            finally { polling = false; }
        });
    }

    /// <summary>DEBUG FEATURE ONLY: asks a debug-build service to send its next log message now.</summary>
    public void SendLog(Action<string> say)
    {
        _ = Task.Run(async () =>
        {
            var r = await cmdLink.Send(new { type = "x_send_log" }, 5000, retry: false);
            var msg = r?["ok"] != null ? "Log sent to the developer." : "Could not send the log: " + (r?["error"]?.ToString() ?? cmdLink.LastError ?? "no answer");
            ui(() => say(msg));
        });
    }

    /// <summary>For --selftest: feed a service reply as if it came from the pipe.</summary>
    public void ApplyForTest(JsonNode? st, JsonNode? bal) => Apply(st, bal);
    /// <summary>For --selftest: feed the service's answer to a connect.</summary>
    public void ConnectReplyForTest(string dest, JsonNode? r) => ConnectReply(dest, r);

    /// <summary>Polls in a row without a reply from the service.</summary>
    int missed;

    /// <summary>The service or the core went away, and with it the tunnel: the app must not keep
    /// showing a connection. A visible notice when one was shown.</summary>
    void DropConnection(string why)
    {
        var S = g.S;
        if (S.Conn != "idle") g.SetFlash("bad", "Your VPN connection ended.", why); // beside, not instead of, an S.Fail screen
        S.Conn = "idle"; S.ConnSec = 0; pendingConnect = null; attemptDest = null; S.Alloc.Clear();
    }

    void Apply(JsonNode? st, JsonNode? bal)
    {
        var S = g.S;
        // one missed poll is not enough to show the connection as gone
        missed = st == null ? missed + 1 : 0;
        if (missed >= 2) DropConnection("The Gnosis VPN service stopped.");
        if (st == null)
        {
            Core = "service not reachable: " + (link.LastError ?? "?");
            var untrusted = link.LastError?.Contains("not served by") == true;
            S.Fail = new Failure { Title = untrusted ? "Can’t verify the Gnosis VPN service" : "Can’t reach the Gnosis VPN service", Code = "SERVICE" };
            return;
        }
        if (S.Fail?.Code == "SERVICE") S.Fail = null;
        Core = st["core"]?.ToString() ?? "";
        DebugRelay = st["debug_relay"]?.GetValue<bool>() == true;
        var data = st["status"]?["data"];
        var mode = data?["run_mode"]?["type"]?.ToString();
        // the PIX price from the config (client 1.1+), so the balance reads as data before any session
        var price = Wei(data?["pix_price_per_byte"]);
        if (price > 0) mbPerWx = 1e-6 / price;
        if (mode != lastMode) { App.Log($"backend: run mode {lastMode ?? "-"} -> {mode ?? "-"}"); lastMode = mode; }
        if (mode != "running") DropConnection("The Gnosis VPN core stopped, and the tunnel with it.");
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
                // what the node holds and what the core asks for (wei)
                HaveWx = Wei(data?["run_mode"]?["node_wxhopr"]); HaveXdai = Wei(data?["run_mode"]?["node_xdai"]);
                NeedWx = Wei(data?["run_mode"]?["balance_recommendation"]?["wxhopr"]); NeedXdai = Wei(data?["run_mode"]?["balance_recommendation"]?["xdai"]);
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
                var rh = d?["route_health"];
                var routable = rh?["state"]?["type"]?.ToString() is null or "routable";
                var walk = rh?["walk"];
                var probe = rh?["quick_probe"];
                var check = probe?["type"]?.ToString() == "checked" ? probe : probe?["last"];
                var e = new Exit
                {
                    Id = id, Country = COUNTRY.GetValueOrDefault(cc, cc == "" ? "Unknown" : cc), Place = x["meta"]?["description"]?.ToString() ?? "",
                    Routable = routable,
                    Measured = walk?["type"]?.ToString() == "paths",
                    Pv = walk?["best_value"]?.GetValue<double>() ?? 0,
                    Ping = (int)Ms(check?["status_rtt"]),
                };
                e.City = x["meta"]?["name"]?.ToString() ?? $"{e.Country} #{AppEngine.Xid(id)}";
                ex.Add(e);
            }
            g.EX.Clear(); g.EX.AddRange(ex);
            health.Clear();
            foreach (var d in dests) if (d?["destination"]?["id"]?.ToString() is string hid) health[hid] = d?["route_health"];
        }
        if (g.EX.Count > 0 && g.ExOrNull(S.Loc) == null) S.Loc = curId ?? g.EX[0].Id;

        // connection
        var kind = conn?["type"]?.ToString();
        // From verify_ping on, the tunnel and its routes are up and carry traffic; the core is only
        // checking the exit's ping and tuning the session. Show that as connected.
        if (kind == "connecting" && conn?["phase"]?["type"]?.ToString() is "verify_ping" or "adjust_to_main" or "connection_established") kind = "connected";
        var since = Ms(conn?["since"]);
        // After Connect the core first opens a probe session to the exit (about a minute) while its
        // status still says disconnected: the app shows "Connecting" for up to 3 minutes meanwhile.
        if (pendingConnect != null && AppEngine.Now - pendingAt > PENDING_MS) pendingConnect = null;
        // The exit a connect is headed to: the status's target says so, or the core has not taken the
        // connect up yet. Until the connection is about that exit, another exit's state (the old one
        // while switching, or the 3 s old cached status) is not shown.
        var target = data?["target"]?.ToString();
        if (attemptDest != null && (target == attemptDest || curId == attemptDest && kind is "connecting" or "reconnecting")) attemptSeen = true;
        bool headed = attemptDest != null && (target == attemptDest || pendingConnect != null && !attemptSeen);
        if (headed && curId != attemptDest)
        {
            if (S.Conn != "connecting") { S.CStart = AppEngine.Now; S.CDur = 90000; }
            S.Conn = "connecting"; S.Loc = attemptDest!;
        }
        else switch (kind)
        {
            case "connecting":
                if (S.Conn != "connecting") { S.CStart = AppEngine.Now - Math.Max(0, NowMs - since); S.CDur = 30000; }
                S.Conn = "connecting"; if (curId != null) S.Loc = curId; pendingConnect = null;
                break;
            case "reconnecting":
                S.Conn = "reconnecting"; if (curId != null) S.Loc = curId; pendingConnect = null;
                break;
            case "connected":
                if (S.Conn != "connected" && curId != null) S.Recent = new[] { new Recent { Id = curId, At = S.T } }.Concat(S.Recent.Where(r => r.Id != curId)).Take(6).ToList();
                S.Conn = "connected"; if (curId != null) S.Loc = curId; pendingConnect = null;
                S.ConnSec = Math.Max(0, (NowMs - since) / 1000);
                if (g.ExOrNull(S.Loc) is Exit cur && conn?["tunnel_ping_rtt"] is JsonNode rtt && Ms(rtt) > 0) cur.Ping = (int)Ms(rtt);
                break;
            default:
                if (!headed && S.Conn != "idle") S.Conn = "idle";
                break;
        }

        // A connect attempt the core gave up on (back to disconnected without the user asking): say why.
        if (attemptDest != null)
        {
            if (kind == "connected" && curId == attemptDest) attemptDest = null;
            // the core is no longer headed there (it dropped back, or never started within PENDING_MS)
            // and is not connecting to it
            else if (!headed && !(curId == attemptDest && kind is "connecting" or "reconnecting"))
            {
                var name = g.ExOrNull(attemptDest)?.City ?? attemptDest;
                App.Log($"backend: connect to {attemptDest} ended without a connection: {WhyNot(attemptDest)}");
                g.SetFlash("bad", $"Couldn’t connect to {name}.", WhyNot(attemptDest));
                attemptDest = null; pendingConnect = null;
            }
        }

        // the session's PIX payment: data left at this exit, and the exit holding traffic until it sees the payment
        var pix = conn?["pix"];
        if (pix != null && curId != null)
        {
            var chunk = Ms(pix["chunk_bytes"]) / 1e6;
            var bought = Ms(pix["bytes_bought"]) / 1e6; var cost = Wei(pix["cost"]);
            if (price <= 0 && bought > 0 && cost > 0) mbPerWx = bought / cost;
            if (chunk > 0) ChunkMb = chunk;
        }
        // Data left belongs to the session: only the current exit's, while its session is up (0 when used up).
        var live = kind is "connecting" or "reconnecting" or "connected" ? curId : null;
        foreach (var id in S.Alloc.Keys.Where(k => k != live).ToList()) S.Alloc.Remove(id);
        if (live != null && pix?["bytes_left"] is JsonNode bl) S.Alloc[live] = Ms(bl) / 1e6;
        else if (live != null) S.Alloc.Remove(live);
        var waiting = conn?["stall"]?["awaiting_payment_since"] != null;
        var pixPhase = pix?["phase"]?["type"]?.ToString();
        if (kind == "connected" && (waiting || pixPhase == "payment_failed"))
        {
            if (S.Flash?.Lead != "Waiting for the exit to see your payment.")
                g.SetFlash("warn", "Waiting for the exit to see your payment.", pixPhase == "payment_failed" ? "The last payment failed; the next one is retried automatically." : "Traffic resumes once it arrives.");
        }
        else if (S.Flash?.Lead == "Waiting for the exit to see your payment.") S.Flash = null;

        // balance: what can still pay exits, the Safe's wxHOPR plus the Curvy PIX pool's (null without
        // a pool), shown in MB at the PIX price
        if (bal?["data"] is JsonNode b && b["type"]?.ToString() == "available")
            S.Balance = Math.Round((Wei(b["safe"]) + Wei(b["pix_pool"])) * mbPerWx);
    }

    /// <summary>The core's answer to connect: refusals are shown at once instead of a silent return to idle.</summary>
    void ConnectReply(string dest, JsonNode? r)
    {
        var S = g.S;
        var name = g.ExOrNull(dest)?.City ?? dest;
        var resp = r?["data"] is JsonObject o && o["type"] != null ? o : r;
        var type = resp?["type"]?.ToString();
        string? why = r == null ? "The Gnosis VPN service did not answer (" + (cmdLink.LastError ?? "no reply") + ")."
            : r["error"] is JsonNode err ? err.ToString()
            : type == "unable_to_connect" ? Health(AsView(resp?["route_health"]), null)
            : type == "destination_not_found" ? "This exit is no longer listed. Pick another one."
            : type == "destination_ambiguous" ? "Several exits match this one. Pick it again from the list."
            : null;
        if (why != null)
        {
            if (S.Loc == dest) { S.Conn = "idle"; }
            pendingConnect = null; attemptDest = null;
            g.SetFlash("bad", $"Couldn’t connect to {name}.", why);
            return;
        }
        if (type == "waiting_to_connect")
            g.SetFlash("info", $"Waiting for a route to {name}.", Health(AsView(resp?["route_health"]), null));
    }

    /// <summary>A connect reply's bare route health state, in the status's view form that Health reads.</summary>
    static JsonObject AsView(JsonNode? state) => new() { ["state"] = state?.DeepClone() };

    /// <summary>Why an exit cannot be reached, in words, from its route health in the status.</summary>
    string WhyNot(string id) => Health(health.GetValueOrDefault(id), id);

    static string Health(JsonNode? h, string? id)
    {
        var state = h?["state"]?["type"]?.ToString();
        var reason = h?["state"]?["reason"];
        var walk = h?["walk"];
        var probeErr = h?["quick_probe"]?["last"]?["error"]?.ToString() ?? h?["quick_probe"]?["error"]?.ToString();
        string text = state switch
        {
            "unrecoverable" => reason?["type"]?.ToString() switch
            {
                "not_allowed" => "This exit needs a route type the app does not allow.",
                "incompatible_api_version" => "This exit runs a version this app cannot talk to.",
                "cannot_open_session" => "The exit refused the session: " + reason?["error"],
                var t => "The exit cannot be used (" + t + ").",
            },
            "not_routable" => walk?["type"]?.ToString() switch
            {
                "not_announced" => "The exit has not announced itself on the network, so there is no way to reach it.",
                "no_path" => $"No relay path to the exit right now ({walk?["forward_count"]} forward, {walk?["return_count"]} return).",
                _ => "There is no route to this exit right now.",
            },
            _ => "The exit did not answer the connection attempt.",
        };
        var last = h?["last_error"]?.ToString() ?? probeErr;
        return last is { Length: > 0 } ? $"{text} Last error: {last}" : text;
    }

    /// <summary>Actions the service carries out; true when handled here.</summary>
    public bool Handle(string a, string? id)
    {
        var S = g.S;
        string? dest = a switch
        {
            "connect" => S.Loc,
            "pick2" => id,
            "auto" => g.Best(S.Loc)?.Id ?? g.Best()?.Id,
            _ => null,
        };
        // the tray and flyout offer Connect before the exits are known: never connect to no exit
        if (dest != null && g.ExOrNull(dest) == null)
        {
            App.Log($"backend: {a} ignored, '{dest}' is not a known exit");
            return true;
        }
        if (dest != null)
        {
            if (S.Stack.LastOrDefault() == "pick") S.Stack.RemoveAt(S.Stack.Count - 1);
            S.Loc = dest; S.Conn = "connecting"; S.CStart = AppEngine.Now; S.CDur = 90000; pendingConnect = dest; pendingAt = AppEngine.Now;
            if (a == "pick2" || a == "auto") S.Mode = a == "auto" ? "auto" : "manual";
            attemptDest = dest; attemptSeen = false; attemptAt = AppEngine.Now;
            _ = Task.Run(async () =>
            {
                var r = await cmdLink.Send(new { type = "connect", destination = dest }, 70000, retry: false);
                App.Log("service: connect -> " + (r?.ToJsonString() is string j ? j[..Math.Min(300, j.Length)] : "no reply: " + cmdLink.LastError));
                ui(() => ConnectReply(dest, r));
            });
            return true;
        }
        if (a is "disconnect" or "cancel")
        {
            attemptDest = null;
            S.Conn = "idle"; pendingConnect = null;
            _ = Task.Run(async () => { var r = await cmdLink.Send(new { type = "disconnect" }, 70000, retry: false); App.Log("service: disconnect -> " + (r?.ToJsonString() is string j ? j[..Math.Min(200, j.Length)] : "no reply")); });
            return true;
        }
        return false;
    }
}
