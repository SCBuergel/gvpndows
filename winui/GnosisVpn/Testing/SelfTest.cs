using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using GnosisVpn.Engine;

namespace GnosisVpn.Testing;

/// <summary>`GnosisVPN.exe --selftest [OUT.txt]`: checks that need no window and no service: the
/// real-backend mapping (fed with service replies as the pipe sends them), which actions go to the
/// service, and that every shipped asset exists.
/// Exit code 0 = all passed. The build runs it after publishing.</summary>
public static class SelfTest
{
    static readonly List<string> fails = new();
    static int checks;
    static void Check(bool ok, string what) { checks++; if (!ok) fails.Add(what); }

    const string DESTS = """
        [{"destination":{"id":"gvpn-ger-1-j9os","meta":{"name":"GVPN-GER-1","description":null,"other":{}},"geo":{"country":"DE"}},"route_health":{"state":{"type":"routable"},"walk":{"type":"paths","walked_at":1,"count":3,"distinct_first_relays":2,"best_relays":[],"best_value":0.995,"return_count":3,"return_best_relays":[],"return_best_value":0.99},"quick_probe":{"type":"checked","checked_at":1,"versions":{"api":[]},"api_version":null,"load":{},"status_rtt":412}}},
         {"destination":{"id":"gvpn-lt-1-ynm1","meta":{"name":"GVPN-LT-1","description":"Exit node","other":{}},"geo":{"country":"LT"}},"route_health":{"state":{"type":"routable"},"walk":{"type":"paths","walked_at":1,"count":1,"distinct_first_relays":1,"best_relays":[],"best_value":0.85,"return_count":1,"return_best_relays":[],"return_best_value":0.8}}},
         {"destination":{"id":"gvpn-kenya-1-tzm3","meta":{"name":"GVPN-KENYA-1","other":{}},"geo":{"country":"KE"}},"route_health":{"state":{"type":"not_routable"}}}]
        """;
    static JsonNode State(string runMode, string conn = "{\"type\":\"disconnected\"}") => JsonNode.Parse(
        "{\"core\":\"running\",\"started\":true,\"status\":{\"type\":\"status\",\"data\":{\"run_mode\":" + runMode +
        ",\"destinations\":" + DESTS + ",\"connection\":" + conn + "}}}")!;

    public static int Run(string[] a)
    {
        var i = Array.IndexOf(a, "--selftest");
        var outPath = i + 1 < a.Length ? a[i + 1] : Path.Combine(AppContext.BaseDirectory, "selftest.txt");
        try { Checks(); } catch (Exception e) { fails.Add("exception: " + e); }
        var report = (fails.Count == 0 ? $"SELFTEST OK ({checks} checks)" : $"SELFTEST FAILED ({fails.Count} of {checks})") + Environment.NewLine + string.Join(Environment.NewLine, fails);
        File.WriteAllText(outPath, report);
        return fails.Count == 0 ? 0 : 1;
    }

    static void Checks()
    {
        // ---- real backend: run modes
        var g = new AppEngine(); var rb = new RealBackend(g, f => f()); g.Real = rb;
        rb.ApplyForTest(State("{\"type\":\"preparing_safe\",\"node_address\":\"0x976909B05A46B60c0944b1afDFfb611d4C9D0d06\",\"node_wxhopr\":\"0\",\"node_xdai\":\"1000000000000000\",\"balance_recommendation\":{\"wxhopr\":\"215350000002225467152\",\"xdai\":\"5000000000000000\"}}"), null);
        Check(g.S.Gate && g.S.SyncT == null, "preparing_safe shows Fund first");
        Check(rb.NodeAddress == "0x976909B05A46B60c0944b1afDFfb611d4C9D0d06", "node address taken from preparing_safe");
        Check(Math.Abs(rb.NeedWx - 215.35) < 0.001 && Math.Abs(rb.NeedXdai - 0.005) < 1e-9 && Math.Abs(rb.HaveXdai - 0.001) < 1e-9, "funding amounts from the core's recommendation");
        rb.ApplyForTest(State("{\"type\":\"warmup\",\"stage\":{\"type\":\"starting_node\"}}"), null);
        Check(!g.S.Gate && g.S.SyncT > 0, "warmup shows Syncing");
        rb.ApplyForTest(null, null);
        Check(g.S.Fail?.Code == "SERVICE", "no reply shows the service failure page");

        // ---- running, connected, with PIX data
        var since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 90_000;
        var connected = $"{{\"type\":\"connected\",\"destination\":\"gvpn-lt-1-ynm1\",\"since\":{since},\"tunnel_ping_rtt\":74,\"stall\":null," +
                        "\"pix\":{\"phase\":{\"type\":\"funded\"},\"chunks_committed\":1,\"bytes_left\":639202344,\"chunk_bytes\":660160512,\"bytes_bought\":660160512,\"cost\":\"5941444608000000000\"}}";
        rb.ApplyForTest(State("{\"type\":\"running\"}", connected), JsonNode.Parse("{\"type\":\"balance\",\"data\":{\"type\":\"available\",\"safe\":\"24660000000000000000\"}}"));
        Check(g.S.Fail == null, "a reply clears the failure page");
        Check(g.EX.Count == 3 && g.EX[0].Country == "Germany" && g.EX[1].City == "GVPN-LT-1", "destinations become exits");
        Check(g.GradeOf(g.EX[2]).K == "hidden", "a not-routable exit is not offered");
        Check(g.GradeOf(g.EX[0]).Label == "Good" && g.EX[0].Ping == 412, "route grade from the walk's path value, latency from the probe");
        Check(g.GradeOf(g.EX[1]).K == "weak", "a low path value is a weak route");
        Check(g.Best()?.Id == "gvpn-ger-1-j9os", "best exit by route quality");
        Check(g.S.Conn == "connected" && g.S.Loc == "gvpn-lt-1-ynm1", "connected to the reported destination");
        Check(Math.Abs(g.S.ConnSec - 90) < 5, "connection time from since");
        Check(Math.Round(g.Left("gvpn-lt-1-ynm1")) == 639, "MB left from PIX bytes_left");
        Check(g.S.EmptyIn == null && Ui.Pages.Expiry(g) == "639 MB left · Auto-renew on", "expiry line without empty_in: data left and renewal");
        rb.ApplyForTest(State("{\"type\":\"running\"}", connected.Replace("\"bytes_left\":639202344", "\"bytes_left\":639202344,\"empty_in\":1080000")), null);
        Check(g.S.EmptyIn == 1080 && Ui.Pages.Expiry(g) == "Expires in 18 min · 639 MB left · Auto-renew on", "expiry line from pix.empty_in");
        Check(g.EX[0].Cc == "DE" && g.EX[2].Country == "Kenya" && RealBackend.CountryName("KE") == "Kenya" && RealBackend.CountryName("") == "Unknown", "country names for any ISO code (KE is Kenya)");
        Check(LogExport.RunElevated(new[] { "--export-logs", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "other.zip") }) == 3
              && LogExport.RunElevated(new[] { "--export-logs", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GnosisVPN-logs-missing.zip") }) == 3, "elevated log export touches only an existing GnosisVPN-logs-*.zip");
        {
            var z = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GnosisVPN-logs-selftest-{Environment.ProcessId}.zip");
            using (System.IO.Compression.ZipFile.Open(z, System.IO.Compression.ZipArchiveMode.Create)) { }
            var rc = LogExport.RunElevated(new[] { "--export-logs", z });
            Check(rc == 0, $"elevated log export adds to the zip (rc {rc})");
            System.IO.File.Delete(z);
        }
        Check(AppEngine.FundingUrl("0x976909B05A46B60c0944b1afDFfb611d4C9D0d06") == "https://funding.vpn.gnosis.eth.limo/?address=0x976909B05A46B60c0944b1afDFfb611d4C9D0d06"
              && AppEngine.FundingUrl("") == null && AppEngine.FundingUrl("0x12&x=1") == null, "funding portal link carries the VPN address");
        Check(g.ExOrNull("gvpn-lt-1-ynm1")?.Ping == 74, "ping from tunnel_ping_rtt");
        Check(Math.Abs(g.S.Balance - Math.Round(24.66 * 660.160512 / 5.941444608)) < 1, "balance in MB at the chunk price");
        // three chunks: bytes_bought and cost cover all of them, so the rate and the balance stay the same
        var safe = JsonNode.Parse("{\"type\":\"balance\",\"data\":{\"type\":\"available\",\"safe\":\"24660000000000000000\"}}");
        var three = connected.Replace("\"chunks_committed\":1", "\"chunks_committed\":3").Replace("\"bytes_bought\":660160512", "\"bytes_bought\":1980481536").Replace("5941444608000000000", "17824333824000000000");
        rb.ApplyForTest(State("{\"type\":\"running\"}", three), safe);
        Check(Math.Abs(g.S.Balance - Math.Round(24.66 * 660.160512 / 5.941444608)) < 1, "balance unchanged after three PIX chunks");
        // without pix_price_per_byte the session's rate applies: here twice the price, half the MB
        rb.ApplyForTest(State("{\"type\":\"running\"}", three.Replace("17824333824000000000", "35648667648000000000")), safe);
        Check(Math.Abs(g.S.Balance - Math.Round(24.66 * 1e-6 / 1.8e-8)) < 1, "balance at the session's bytes_bought / cost");
        // the status's pix_price_per_byte (here twice the session's) wins over the session's rate
        var priced = State("{\"type\":\"running\"}", three); priced["status"]!["data"]!["pix_price_per_byte"] = "18000000000";
        rb.ApplyForTest(priced, safe);
        Check(Math.Abs(g.S.Balance - Math.Round(24.66 * 1e-6 / 1.8e-8)) < 1, "balance at pix_price_per_byte, not the session's rate");
        // R7-6: the Curvy PIX pool pays exits too, so it counts; without pix_pool only the Safe
        rb.ApplyForTest(priced, JsonNode.Parse("{\"type\":\"balance\",\"data\":{\"type\":\"available\",\"safe\":\"1000000000000000000\",\"pix_pool\":\"35350000000000000000\"}}"));
        Check(Math.Abs(g.S.Balance - Math.Round(36.35 * 1e-6 / 1.8e-8)) < 1, "balance counts the Safe and the PIX pool");
        rb.ApplyForTest(priced, JsonNode.Parse("{\"type\":\"balance\",\"data\":{\"type\":\"available\",\"safe\":\"1000000000000000000\",\"pix_pool\":null}}"));
        Check(Math.Abs(g.S.Balance - Math.Round(1e-6 / 1.8e-8)) < 1, "balance without a PIX pool is the Safe's");
        rb.ApplyForTest(priced, safe);
        Check(Messages.ScreenCode(g) == "S01", "connected screen");

        // ---- the exit waits for the payment
        var waiting = connected.Replace("\"stall\":null", "\"stall\":{\"since\":1,\"failed_pings\":1,\"reconnect_at\":3,\"awaiting_payment_since\":1}");
        rb.ApplyForTest(State("{\"type\":\"running\"}", waiting), null);
        Check(g.S.Flash?.Lead == "Waiting for the exit to see your payment.", "payment wait message");
        rb.ApplyForTest(State("{\"type\":\"running\"}", connected), null);
        Check(g.S.Flash == null, "payment wait message clears");

        // ---- the service or the core goes away: the connection is not shown any longer
        rb.ApplyForTest(null, null);
        Check(g.S.Conn == "connected", "one missed poll keeps the connection shown");
        rb.ApplyForTest(null, null);
        Check(g.S.Conn == "idle" && g.S.ConnSec == 0 && g.S.Alloc.Count == 0 && g.S.Flash?.Tone == "bad", "no service: idle, with a notice");
        rb.ApplyForTest(State("{\"type\":\"running\"}", connected), null);
        Check(g.S.Conn == "connected" && g.S.Fail == null, "connected again once the service answers");
        rb.ApplyForTest(JsonNode.Parse("{\"core\":\"starting\",\"started\":true,\"status\":null}"), null);
        Check(g.S.Conn == "idle", "no core status: idle");
        rb.ApplyForTest(State("{\"type\":\"running\"}", connected), null);
        rb.ApplyForTest(State("{\"type\":\"warmup\",\"stage\":{\"type\":\"starting_node\"}}", connected), null);
        Check(g.S.Conn == "idle" && g.S.SyncT > 0, "core warming up again: idle");
        rb.ApplyForTest(State("{\"type\":\"running\"}", connected), null);
        g.S.Flash = null; g.S.Fq = new();

        // ---- data left belongs to the session (R7-4)
        rb.ApplyForTest(State("{\"type\":\"running\"}", connected.Replace("\"bytes_left\":639202344", "\"bytes_left\":0")), null);
        Check(g.S.Alloc.TryGetValue("gvpn-lt-1-ynm1", out var used) && used == 0, "a used-up chunk shows 0 MB left");
        rb.ApplyForTest(State("{\"type\":\"running\"}", connected.Replace("\"bytes_left\":639202344", "\"bytes_left\":null")), null);
        Check(g.S.Alloc.Count == 0, "no bytes_left, no data left");
        rb.ApplyForTest(State("{\"type\":\"running\"}", connected), null);
        rb.ApplyForTest(State("{\"type\":\"running\"}"), null);
        Check(g.S.Alloc.Count == 0 && g.Left("gvpn-lt-1-ynm1") == 0, "data left goes when the session ends");
        rb.ApplyForTest(State("{\"type\":\"running\"}", connected), null);

        // ---- R7-8: after Disconnect the service's cached status (same connection) does not bring it back
        var run = "{\"type\":\"running\"}";
        rb.Handle("disconnect", null); var recent0 = g.S.Recent;
        rb.ApplyForTest(State(run, connected), null);
        Check(g.S.Conn == "idle" && ReferenceEquals(g.S.Recent, recent0), "a stale status after Disconnect stays idle, Recent untouched");
        rb.ApplyForTest(State(run, waiting), null);
        Check(g.S.Flash?.Lead != "Waiting for the exit to see your payment.", "no payment wait message from a stale status");
        rb.ApplyForTest(State(run), null); rb.ApplyForTest(State(run, connected), null);
        Check(g.S.Conn == "connected", "after a disconnected status the connection shows again");
        rb.Handle("disconnect", null); rb.AgeDisconnectForTest(11_000);
        rb.ApplyForTest(State(run, connected), null);
        Check(g.S.Conn == "connected", "the stale-status guard ends after 10 s");
        rb.Handle("disconnect", null);
        rb.ApplyForTest(State(run, connected.Replace($"\"since\":{since}", $"\"since\":{since + 1000}")), null);
        Check(g.S.Conn == "connected", "a newer connection is shown at once");
        rb.ApplyForTest(State(run, connected), null);

        // ---- actions: connect / switch / disconnect go to the service
        Check(rb.Handle("pick2", "gvpn-ger-1-j9os") && g.S.Conn == "connecting" && g.S.Loc == "gvpn-ger-1-j9os", "a pick connects through the service");
        rb.ApplyForTest(State("{\"type\":\"running\"}", $"{{\"type\":\"connecting\",\"destination\":\"gvpn-ger-1-j9os\",\"since\":{since},\"phase\":{{\"type\":\"init\"}}}}"), null);
        Check(g.S.Conn == "connecting", "connecting reported");
        Check(rb.Handle("disconnect", null) && g.S.Conn == "idle", "disconnect");
        Check(!rb.Handle("view", null) && !rb.Handle("ptab", null), "UI-only actions stay in the UI");

        // ---- switching exits while connected (R7-7): the old exit's state does not end the new connect
        const string GER = "gvpn-ger-1-j9os";
        rb.ApplyForTest(State("{\"type\":\"running\"}", connected), null);
        rb.Handle("pick2", GER);
        rb.ApplyForTest(State("{\"type\":\"running\"}", connected), null);
        Check(g.S.Conn == "connecting" && g.S.Loc == GER, "switching: the old exit's status keeps the new connect shown");
        var toGer = State("{\"type\":\"running\"}"); toGer["status"]!["data"]!["target"] = GER;
        rb.ApplyForTest(toGer, null);
        Check(g.S.Conn == "connecting" && g.S.Loc == GER, "switching: the new exit's probe shows connecting");
        // the core keeps the target but latched the exit unrecoverable (it refused the session)
        JsonNode GerDead() { var n = State("{\"type\":\"running\"}"); n["status"]!["data"]!["target"] = GER;
            n["status"]!["data"]!["destinations"]![0]!["route_health"]!["state"] = JsonNode.Parse("{\"type\":\"unrecoverable\",\"reason\":{\"type\":\"cannot_open_session\",\"error\":\"x\"}}"); return n; }
        g.S.Flash = null; g.S.Fq = new();
        rb.ApplyForTest(GerDead(), null);
        Check(g.S.Conn == "idle" && g.S.Flash?.Lead == "Couldn’t connect to GVPN-GER-1." && g.S.Flash?.Text == "The exit refused the session: x", "switching: the new exit's failure is reported");
        // the same from idle
        g.S.Flash = null; g.S.Fq = new();
        rb.Handle("pick2", GER);
        rb.ApplyForTest(toGer, null);
        Check(g.S.Conn == "connecting" && g.S.Flash == null, "connect from idle: the probe shows connecting");
        rb.ApplyForTest(GerDead(), null);
        Check(g.S.Conn == "idle" && g.S.Flash?.Lead == "Couldn’t connect to GVPN-GER-1.", "connect from idle: an unrecoverable exit is reported");
        g.S.Flash = null; g.S.Fq = new();

        // ---- connect replies and connects to no known exit
        g.S.Loc = "";
        Check(rb.Handle("connect", null) && g.S.Conn == "idle", "connect with no exit is ignored");
        Check(rb.Handle("pick2", "gvpn-gone-1") && g.S.Conn == "idle", "connect to an unknown exit is ignored");
        const string LT = "\"destination\":{\"id\":\"gvpn-lt-1-ynm1\",\"meta\":{\"name\":\"GVPN-LT-1\",\"other\":{}}}";
        void Reply(string json) { g.S.Flash = null; g.S.Loc = "gvpn-lt-1-ynm1"; g.S.Conn = "connecting"; rb.ConnectReplyForTest("gvpn-lt-1-ynm1", JsonNode.Parse(json)); }
        Reply("{\"type\":\"unable_to_connect\"," + LT + ",\"route_health\":{\"type\":\"unrecoverable\",\"reason\":{\"type\":\"incompatible_api_version\",\"offered\":[]}}}");
        Check(g.S.Conn == "idle" && g.S.Flash?.Text == "This exit runs a version this app cannot talk to.", "unable_to_connect shows its reason");
        Reply("{\"type\":\"waiting_to_connect\"," + LT + ",\"route_health\":{\"type\":\"not_routable\"}}");
        Check(g.S.Conn == "connecting" && g.S.Flash?.Text == "There is no route to this exit right now.", "waiting_to_connect shows its reason");
        Reply("{\"type\":\"destination_not_found\"}");
        Check(g.S.Conn == "idle" && g.S.Flash?.Tone == "bad", "destination_not_found is a refusal");
        Reply("{\"type\":\"destination_ambiguous\",\"candidates\":[\"a\",\"b\"]}");
        Check(g.S.Conn == "idle" && g.S.Flash?.Tone == "bad", "destination_ambiguous is a refusal");

        // ---- R7-10: a state's message goes when the state ends; other messages stay
        const string LTID = "gvpn-lt-1-ynm1", ROUTE_WAIT = "Waiting for a route to GVPN-LT-1.", PAY_WAIT = "Waiting for the exit to see your payment.";
        g.S.Flash = null; g.S.Fq = new();
        rb.AttemptForTest(LTID); g.S.Loc = LTID; g.S.Conn = "connecting";
        rb.ConnectReplyForTest(LTID, JsonNode.Parse("{\"type\":\"waiting_to_connect\"," + LT + ",\"route_health\":{\"type\":\"not_routable\"}}"));
        var toLt = State(run); toLt["status"]!["data"]!["target"] = LTID;
        rb.ApplyForTest(toLt, null);
        Check(g.S.Flash?.Lead == ROUTE_WAIT, "waiting for a route stays while the core waits for it");
        g.SetFlash("bad", "Other.", "x");
        rb.ApplyForTest(State(run, connected), null);
        Check(g.S.Conn == "connected" && g.S.Flash?.Lead == "Other." && g.S.Fq.All(f => f.Lead != ROUTE_WAIT), "waiting for a route goes once connected, shown or queued");
        g.S.Flash = null; g.S.Fq = new();
        rb.ApplyForTest(State(run, waiting), null);
        g.SetFlash("bad", "Your VPN connection ended.", "x");
        Check(g.S.Fq.Any(f => f.Lead == PAY_WAIT), "the payment wait message is queued under a newer one");
        rb.ApplyForTest(State(run), null);
        Check(g.S.Flash?.Lead == "Your VPN connection ended." && g.S.Fq.All(f => f.Lead != PAY_WAIT), "a queued payment wait message goes with the connection");
        rb.ApplyForTest(State(run, waiting), null);
        rb.ApplyForTest(State(run, connected), null);
        Check(g.S.Flash?.Lead == "Your VPN connection ended.", "retracting the shown payment wait message moves the next one up");
        g.S.Flash = null; g.S.Fq = new();

        // ---- shipped assets
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets");
        foreach (var name in new[] { "app.ico", "appicon-32.png", "appicon-256.png", "tray-off.ico", "tray-ok.ico", "tray-warn.ico", "tray-bad.ico", "logo-light.svg", "logo-dark.svg", "gvis-light.svg", "gvis-dark.svg" })
        { var p = Path.Combine(dir, name); Check(File.Exists(p) && new FileInfo(p).Length > 200, "asset " + name); }
        Check(Directory.Exists(Path.Combine(dir, "flags")) && Directory.GetFiles(Path.Combine(dir, "flags")).Length >= 10, "flags");
        Check(Ui.Icons.Paths.Count >= 100 && Ui.Tokens.Light.Count == Ui.Tokens.Dark.Count, "generated icons and tokens");
    }
}
