using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace GnosisVpn.Engine;

/// <summary>
/// The fake VPN backend: a line-by-line port of the prototype's src/shared/engine.js (Gnosis VPN
/// Stage 2.1 demo logic, numbers and copy). The UI reads <see cref="S"/> and calls <see cref="Act"/>;
/// the real Gnosis VPN core later sits behind the same state and action shape.
/// </summary>
public sealed class FakeEngine
{
    // ---------- constants (as in the demo) ----------
    public const double RATE = 0.08;   // MB per second while connected
    public const double COST = 600, LOW = 120, AUTO_AT = 30, SOON = RATE * 300, PAY = 3000;
    public const int BASE = 9 * 3600 + 41 * 60;   // sim clock starts 09:41
    public const double HOLD_S = 3 * 3600;          // data left at an exit stays 3 h
    public const string UPD_V = "1.4.0";
    public const double SYNC_LEN = 120;
    public const string FUND_URL = "https://claude.ai/artifact/WjaBGFUNxuHhUnb4ryRAnz";
    public const string ADDRESS = "0x7a3f9c2e5b81d0c4e6a2b7d48d1c91e3";
    const double MIN_PATH_VALUE = 0.9;

    public static readonly Dictionary<int, string> REL = new() { [3] = "Very reliable", [2] = "Reliable", [1] = "Less reliable" };
    public static readonly Dictionary<string, string> TIPS = new()
    {
        ["fast"] = "Fastest exits first, by ping right now.", ["rel"] = "Exits that stay online longest come first.",
        ["recent"] = "The last 6 exits you used, newest first.", ["alpha"] = "Sorted by country name."
    };

    public readonly List<Exit> EX;
    readonly List<Exit> BASE_EX;
    public State S = new();
    /// <summary>The real Gnosis VPN service; when set, the simulation stops and the service drives the state.</summary>
    public RealBackend? Real;

    // ---------- UI hooks ----------
    public event Action? Render;
    public event Action<string>? Snack;
    public event Action<Note>? HeadsUp;
    public event Action<string>? OpenUrl;
    public event Action? SyncStart;
    public event Action<bool>? SyncEnd;
    /// <summary>Wall-clock timer (setTimeout): ms, callback. Set by the UI to a DispatcherQueue timer.</summary>
    public Action<double, Action> SetTimeout = (ms, a) => { };
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static double Now => Clock.Elapsed.TotalMilliseconds;   // performance.now()

    public FakeEngine()
    {
        EX = new List<Exit>
        {
            new() { Id = "nl", City = "Amsterdam", Country = "Netherlands", Ping = 451, Rel = 2, Spots = 6, Slots = 10, Pv = 0.97, Fr = 3, Measured = true, Busy = "Low" },
            new() { Id = "at", City = "Vienna", Country = "Austria", Ping = 560, Rel = 3, Spots = 4, Slots = 10, Pv = 0.95, Fr = 4, Measured = true, Busy = "Low" },
            new() { Id = "de", City = "Frankfurt", Country = "Germany", Ping = 612, Rel = 3, Spots = 8, Slots = 10, Pv = 0.96, Fr = 4, Measured = true, Busy = "Medium" },
            new() { Id = "pt", City = "Lisbon", Country = "Portugal", Ping = 690, Rel = 2, Spots = 5, Slots = 10, Pv = 0.93, Fr = 2, Measured = false, Busy = "Low" },
            new() { Id = "ch", City = "Zürich", Country = "Switzerland", Ping = 740, Rel = 3, Spots = 1, Slots = 10, Pv = 0.91, Fr = 3, Measured = true, Busy = "High" },
            new() { Id = "se", City = "Stockholm", Country = "Sweden", Ping = 820, Rel = 1, Spots = 7, Slots = 10, Pv = 0.84, Fr = 2, Measured = true, Busy = "Low" },
            new() { Id = "br", City = "São Paulo", Country = "Brazil", Ping = 1307, Rel = 2, Spots = 9, Slots = 10, Pv = 0.92, Fr = 1, Measured = true, Busy = "Low" },
            new() { Id = "gb", City = "London", Country = "United Kingdom", Ping = 380, Rel = 3, Spots = 0, Full = true },
            new() { Id = "pl", City = "Warsaw", Country = "Poland", Ping = 470, Rel = 2, Spots = 0, Full = true },
            new() { Id = "kr", City = "Seoul", Country = "South Korea", Ping = 950, Rel = 2, Spots = 0, Full = true },
        };
        // exits are named by country plus a short random id of their public address
        foreach (var e in EX) { e.Place = e.City; e.City = $"{e.Country} #{Xid(e.Id)}"; }
        BASE_EX = EX.Select(e => e.Clone()).ToList();
        // First launch: the node has no funds yet.
        S.Gate = true; S.Balance = 0; S.Added = 0;
    }

    public static uint Hash32(string str)
    {
        uint h = 0x811c9dc5;
        foreach (var ch in str) { h ^= ch; h = unchecked(h * 0x01000193); }
        return h;
    }
    public static string Xid(string id) => Hash32("pub:" + id).ToString("x8")[..4];

    public Exit Ex(string id) => EX.First(e => e.Id == id);
    public Exit? ExOrNull(string? id) => id == null ? null : EX.FirstOrDefault(e => e.Id == id);
    public static bool Usable(Exit e) => !e.Full && !e.Offline;

    // ---------- navigation stack ----------
    public string? Page => S.Stack.Count > 0 ? S.Stack[^1] : null;
    public void Go(string p) { if (Page != p) S.Stack.Add(p); }
    public void Home() => S.Stack.Clear();
    public void Back() { if (S.Stack.Count > 0) S.Stack.RemoveAt(S.Stack.Count - 1); }

    // ---------- formatting ----------
    public static string Mb(double v) => v >= 1000 ? Trim1(v / 1000) + " GB" : Math.Max(0, Math.Round(v, MidpointRounding.AwayFromZero)) + " MB";
    static string Trim1(double v) { var s = v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture); return s.EndsWith(".0") ? s[..^2] : s; }
    public static string P2(double n) => ((int)Math.Floor(n)).ToString("00");
    public static string Hms(double s) => P2(s / 3600) + ":" + P2(s % 3600 / 60) + ":" + P2(s % 60);
    public static string FmtT(double t) { var s = BASE + t; return P2(s / 3600 % 24) + ":" + P2(s % 3600 / 60); }
    public string ClockText => FmtT(S.T);
    public double Left(string? id = null) => S.Alloc.TryGetValue(id ?? S.Loc, out var v) ? v : 0;
    public int MinsLeft(double? v = null) => (int)Math.Max(1, Math.Round((v ?? Left()) / RATE / 60, MidpointRounding.AwayFromZero));
    public static string Dur(int m) => m >= 60 ? $"{m / 60} h {m % 60} min" : $"{m} min";
    public string UsePhrase()
    {
        var m = S.Balance; if (!(m > 0)) return "No data left.";
        string F(double h)
        {
            if (h < 1) { var n = Math.Max(1, (int)Math.Round(h * 60, MidpointRounding.AwayFromZero)); return n + (n == 1 ? " minute" : " minutes"); }
            var r = h < 10 ? Math.Round(h * 2, MidpointRounding.AwayFromZero) / 2 : Math.Round(h, MidpointRounding.AwayFromZero);
            return Trim1(r).Replace(".0", "") + (r == 1 ? " hour" : " hours");
        }
        double hd = m / 3000, br = m / 100;
        return $"Enough for roughly {F(hd)} of HD video or {(br >= 10 ? Math.Floor(br) + "+ hours" : F(br))} of browsing.";
    }
    public bool IsOn => S.Conn is "connected" or "reconnecting";
    public double HeldLeft(string id) { var at = S.AllocAt.TryGetValue(id, out var v) ? v : S.T - 600; return Math.Max(0, HOLD_S - (S.T - at)); }
    public bool Warm => S.Warm && S.T < S.WarmEnd;

    // ---------- route grade ----------
    public Grade GradeOf(Exit? e)
    {
        if (e == null || e.Full || e.Offline || e.Routable == false || (e.Measured && e.Slots > 0 && e.Spots <= 0 && !(IsOn && e.Id == S.Loc)))
            return new Grade("hidden", 0, "Unavailable");
        if (e.Pv < MIN_PATH_VALUE) return new Grade("weak", 1, "Weak route", "bad", -1);
        if (!e.Measured) return new Grade("unmeasured", 0, "Not measured yet", null, -0.5);
        var own = IsOn && e.Id == S.Loc ? 1 : 0;
        var latency = 1 - Math.Min(e.Ping / 2.0, 1000) / 1000;
        var capacity = Math.Min(1, (e.Spots + own) / (double)e.Slots);
        var diversity = Math.Min(e.Fr, 4) / 4.0;
        var sc = 0.5 * latency + 0.3 * capacity + 0.2 * diversity;
        var n = sc >= 0.75 ? 4 : sc >= 0.5 ? 3 : sc >= 0.25 ? 2 : 1;
        return new Grade("ok", n, new[] { "", "Poor", "Fair", "Good", "Excellent" }[n], n >= 3 ? "ok" : n == 2 ? "warn" : "bad", sc);
    }
    public Exit? Best(string? except = null) =>
        EX.Where(e => Usable(e) && e.Id != except && GradeOf(e).K == "ok").OrderByDescending(e => GradeOf(e).Sc).FirstOrDefault();

    // ---------- system notifications: rules and texts ----------
    public static readonly List<Rule> RULES = new()
    {
        new() { K = "u1", Code = "U1", Name = "Plenty of data", Text = "Never. Everything is fine.", Silent = true },
        new() { K = "u2", Code = "U2", Name = "Renewing automatically", Text = "Never. Expected; the app shows it in place.", Silent = true },
        new() { K = "u3", Code = "U3 · N3", Name = "Exit running low", Text = "Only with Auto-renew off." },
        new() { K = "u4", Code = "U4 · N4", Name = "Balance running low", Text = "Always, also with Auto-renew on." },
        new() { K = "u5", Code = "U5 · N5", Name = "Balance empty", Text = "Always, also with Auto-renew on." },
        new() { K = "u6", Code = "U6 · N6", Name = "Internet pauses soon", Text = "Always, when no renewal will happen." },
        new() { K = "a3", Code = "A3", Name = "Internet paused, no data", Text = "Only with Auto-renew off (journey A3)." },
        new() { K = "n8", Code = "N8 · N7", Name = "Payment received", Text = "Always. N7 adds the Lockdown reminder." },
        new() { K = "off", Code = "Proposed", Name = "Exit went offline", Text = "Not defined on the canvas yet.", Proposed = true },
        new() { K = "upd", Code = "Proposed", Name = "Update available", Text = "Once per version, after it has downloaded. Never while connecting.", Proposed = true },
    };
    static List<Btn> B(params (string l, string a)[] x) => x.Select(t => new Btn(t.l, t.a)).ToList();
    Note NUpd() => new() { Rule = "upd", Proposed = true, Title = "Update available", Body = $"Gnosis VPN {UPD_V} is ready. Restarting takes a few seconds.", Acts = B(("Restart to update", "updNow"), ("Later", "nclose")) };
    Note NU1() => new() { Rule = "u1", Silent = true, Title = "Plenty of data", Body = "Nothing to do, so nothing is sent." };
    Note NU2() => new() { Rule = "u2", Silent = true, Title = "Renewing automatically", Body = $"600 MB set aside at {Ex(S.Loc).City}. Shown in the app only." };
    Note NU3() => new() { Rule = "u3", Title = "Exit running low", Body = $"{Math.Round(Left())} MB left at {Ex(S.Loc).City}, about {MinsLeft()} min at this rate.", Acts = B(("Renew · 600 MB", "renew"), ("Close", "nclose")) };
    Note NU4() => new() { Rule = "u4", Title = "Balance running low", Body = $"{Mb(S.Balance)} left. {UsePhrase()}", Acts = B(("Add data", "addData"), ("Close", "nclose")) };
    Note NU5() => new() { Rule = "u5", Title = "Balance empty", Body = "No data left to renew. Add data to keep browsing.", Acts = B(("Add data", "addData"), ("Close", "nclose")) };
    Note NU6() => new() { Rule = "u6", Title = "Internet pauses soon", Body = $"About {MinsLeft()} min of data left at {Ex(S.Loc).City}.", Acts = S.Balance >= COST ? B(("Renew · 600 MB", "renew"), ("Close", "nclose")) : B(("Add data", "addData"), ("Close", "nclose")) };
    Note NA3() => new() { Rule = "a3", Title = "Internet paused", Body = $"No data left at {Ex(S.Loc).City}. Your traffic stays private while paused.", Acts = B(("Renew and reconnect", "renew"), ("Close", "nclose")) };
    Note NN8(bool ld) => new() { Rule = "n8", Code = ld ? "N7" : "N8", Title = "Payment received", Body = $"3 GB added. Your balance is {Mb(S.Balance)}.{(ld ? " Lockdown is still off." : "")}", Acts = ld ? B(("Turn Lockdown back on", "lockOn"), ("Close", "nclose")) : B(("Close", "nclose")) };
    Note NOff() => new() { Rule = "off", Proposed = true, Title = "Internet paused", Body = $"{Ex(S.Loc).City} went offline. The {Math.Round(S.Lost)} MB left there is lost.", Acts = B(("Connect to best available", "bestAvail"), ("Close", "nclose")) };

    void Fire(string key, Note n)
    {
        if (S.Fired.ContainsKey(key)) return;
        S.Fired[key] = S.T;
        n.At = S.T; n.Id = key + ":" + S.T.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        S.Notes.Insert(0, n);
        if (S.Notes.Count > 30) S.Notes.RemoveAt(S.Notes.Count - 1);
        if (!n.Silent) { S.Unread++; HeadsUp?.Invoke(n); }
    }

    // ---------- engine ----------
    void Emit() => Render?.Invoke();
    void Toast(string msg) => Snack?.Invoke(msg);

    public void SetFlash(string tone, string lead, string text, List<Btn>? btns = null)
    {
        if (S.Flash != null && S.Flash.Lead != lead)
        {
            var cur = S.Flash;
            S.Fq = new[] { cur }.Concat(S.Fq.Where(x => x.Lead != lead && x.Lead != cur.Lead)).Take(3).ToList();
        }
        S.Flash = new Flash { Tone = tone, Lead = lead, Text = text, Btns = btns ?? new() };
    }

    void Step(double dt)
    {
        foreach (var id in S.Alloc.Keys.ToList())
            if (id != S.Loc && S.Alloc[id] > 0 && HeldLeft(id) <= 0) S.Alloc[id] = 0;
        if (S.Gate) { if (S.Pay != null && S.T >= S.Pay) PaymentArrived(); return; }
        if (S.SyncT != null) return;
        if (S.Conn == "idle") { S.ArCount = 0; S.ArDecl = -1; }
        if (S.Conn == "connected")
        {
            S.ConnSec += dt;
            S.Alloc[S.Loc] = Math.Max(0, Left() - RATE * dt);
            double a = Left(); bool canRenew = S.Balance >= COST;
            if (S.AutoRenew && a <= AUTO_AT && canRenew) AutoRenewNow();
            else
            {
                if (!S.AutoRenew && a <= LOW) Fire("u3:" + S.AllocN, NU3());
                if (a <= SOON && !(S.AutoRenew && canRenew)) Fire("u6:" + S.AllocN, NU6());
                // Auto-renew off: ask to renew when the internet is about to pause, once per 600 MB
                if (a <= SOON && a > 0 && !S.AutoRenew && canRenew && S.Sheet == null && S.AskedSoon != S.AllocN)
                { S.AskedSoon = S.AllocN; S.Sheet = new Sheet { Type = "quota", Id = S.Loc, Renew = true, Soon = true }; }
                if (a <= 0) PauseFor(canRenew ? "nodata" : "balance");
            }
        }
        if (S.Balance > 0 && S.Balance <= COST) Fire("u4:" + S.BalEp, NU4());
        if (S.Balance < COST && ((S.Conn == "connected" && Left() <= LOW) || S.Pause == "balance")) Fire("u5:" + S.BalEp, NU5());
        if (S.Pay != null && S.T >= S.Pay) PaymentArrived();
        if (S.Pause == "renewfail" && S.T >= S.RetryAt) { if (S.FailRenew) S.RetryAt = S.T + 30; else RenewNow(); }
        if (S.Conn == "idle" && S.Mode == "auto" && !S.Checking && Page == null && S.T >= S.NextCheck) RunCheck();
    }

    void AutoRenewNow()
    {
        var sh = S.Sheet;
        if (sh != null && sh.Check)
        {
            if (S.T >= sh.Until)
            {
                S.Sheet = null; S.ArDecl = S.AllocN; PauseFor("nodata");
                SetFlash("warn", "Auto-renew paused.", "No answer for 5 minutes. Renew to keep going.", B(("Renew", "renew")));
            }
            return;
        }
        if (!S.ArPast4 && S.ArCount >= 4 && S.Balance >= COST * 4)
        {
            if (S.ArDecl == S.AllocN) { if (Left() <= 0) PauseFor("nodata"); return; }
            S.Sheet = new Sheet { Type = "quota", Id = S.Loc, Check = true, Until = S.T + 300 }; return;
        }
        if (S.FailRenew) { PauseFor("renewfail"); return; }
        S.Balance -= COST; S.Alloc[S.Loc] = Left() + COST; S.AllocN++; S.AllocAt[S.Loc] = S.T; S.ArCount++;
        SetFlash("ok", "Renewed automatically.", $"600 MB set aside at {Ex(S.Loc).City}. Tip: switching exits now and then makes your traffic harder to link.");
        Fire("u2:" + S.AllocN, NU2());
    }

    void PauseFor(string reason)
    {
        S.Conn = "paused"; S.Pause = reason; S.OffConfirm = false; S.Flash = null; S.Fq = new(); S.Better = null;
        if (reason == "nodata" && !S.AutoRenew)
        {
            Fire("a3:" + S.AllocN, NA3());
            if (S.Balance >= COST) S.Sheet = new Sheet { Type = "quota", Id = S.Loc, Renew = true };
        }
        if (reason == "renewfail") S.RetryAt = S.T + 30;
        if (reason == "offline") Fire("off:" + S.T, NOff());
    }

    void RunCheck()
    {
        S.Checking = true;
        SetTimeout(1500, () =>
        {
            S.Checking = false; S.NextCheck = S.T + 60;
            var v = Ex("at"); if (v.Ping > 233) v.Ping = 233;
            var after = Best()?.Id;
            if (S.Conn == "idle" && S.Mode == "auto" && after != null && after != S.Loc)
            {
                S.Loc = after; S.SwapAnim = true;
                SetFlash("info", $"{Ex(after).City} is now the best exit.", "Auto keeps this up to date until you connect.");
                SetTimeout(700, () => { S.SwapAnim = false; Emit(); });
            }
            Emit();
        });
    }

    void RequestConnect(string id, bool viaList = false)
    {
        var e = Ex(id);
        if (!Usable(e)) return;
        if (S.NextFull && viaList && id != S.Loc)
        {
            e.Full = true; S.NextFull = false; S.FullMsg = new FullMsg { Id = id, Alt = Best(id)?.Id }; Emit(); return;
        }
        if (Page == "pick") Back();
        var needs = !(Left(id) > 0) && !S.Free;
        if (needs && S.Balance < COST) { S.Sheet = new Sheet { Type = "nobal", Id = id }; Emit(); return; }
        if (S.Popover && needs && S.AutoRenew && !S.ArIntro) { S.Sheet = new Sheet { Type = "quota", Id = id, Intro = true }; Emit(); return; }   // once per installation
        if (S.Popover && !(Left(id) > 0) && !S.AutoRenew) { S.Sheet = new Sheet { Type = "quota", Id = id }; Emit(); return; }                    // off: ask every time
        DoConnect(id);
    }

    public string ConnLbl => !S.Popover && !S.Free && !(Left(S.Loc) > 0) ? "Connect · 600 MB" : "Connect";

    void DoConnect(string id)
    {
        var fromPause = S.Conn == "paused";
        Home(); S.Sheet = null; S.FullMsg = null; S.Flash = null; S.Fq = new(); S.OffConfirm = false; S.Better = null;
        S.Loc = id; S.Conn = "connecting"; S.Pause = null; S.CStart = Now; S.CDur = 12000 / Math.Min(S.Speed, 4);
        var tok = new object(); S.CTok = tok;
        SetTimeout(S.CDur, () => { if (S.CTok == tok && S.Conn == "connecting") FinishConnect(fromPause); Emit(); });
        Emit();
    }

    void FinishConnect(bool fromPause)
    {
        var id = S.Loc;
        if (!(Left(id) > 0))
        {
            if (S.Free) S.Free = false; else S.Balance -= COST;
            S.Alloc[id] = COST; S.AllocN++; S.AllocAt[id] = S.T;
        }
        S.Conn = "connected"; S.ConnSec = 0; S.FirstRun = false;
        S.Recent = new[] { new Recent { Id = id, At = S.T } }.Concat(S.Recent.Where(r => r.Id != id)).Take(6).ToList();
        Fire("u1", NU1());
        if (fromPause) BackOnline();
    }

    void BackOnline()
    {
        if (!S.AutoRenew && !S.Offered)
        {
            S.Offered = true;
            SetFlash("ok", "Back online.", $"600 MB set aside at {Ex(S.Loc).City}. Renew automatically next time?", B(("Turn on Auto-renew", "arOn"), ("Not now", "dismiss")));
        }
        else SetFlash("ok", "Back online.", $"600 MB set aside at {Ex(S.Loc).City}.");
    }

    void RenewNow()
    {
        if (S.Balance < COST) { if (S.Conn == "paused") S.Pause = "balance"; else S.Sheet = new Sheet { Type = "nobal", Id = S.Loc }; Emit(); return; }
        if (S.FailRenew) { if (S.Conn != "paused" || S.Pause != "renewfail") PauseFor("renewfail"); else S.RetryAt = S.T + 30; Emit(); return; }
        var wasPaused = S.Conn == "paused";
        S.Balance -= COST; S.Alloc[S.Loc] = Left() + COST; S.AllocN++; S.AllocAt[S.Loc] = S.T; S.Flash = null; S.Fq = new();
        if (wasPaused)
        {
            S.Conn = "reconnecting"; S.Pause = null; S.OffConfirm = false;
            SetTimeout(1800, () => { if (S.Conn == "reconnecting") { S.Conn = "connected"; BackOnline(); } Emit(); });
        }
        else SetFlash("ok", "Renewed.", $"600 MB set aside at {Ex(S.Loc).City}.");
        Emit();
    }

    void Disconnect()
    {
        S.Conn = "idle"; S.Pause = null; S.OffConfirm = false; S.Flash = null; S.Fq = new(); S.Better = null; S.CTok = null; S.NextCheck = S.T + 20;
        Emit();
    }

    void PaymentArrived()
    {
        if (S.Gate) { S.Gate = false; S.Pay = null; S.Balance = PAY; S.Added = PAY; S.BalEp++; Home(); StartSync(); return; }
        S.Pay = null; S.Balance += PAY; S.Added += PAY; S.BalEp++;
        Fire("n8:" + S.T, NN8(S.LdOffPay));
        var bal = Mb(S.Balance);
        if (S.LdOffPay) SetFlash("ok", "Payment received.", $"3 GB added; your balance is {bal}. You can connect again.");
        else if (IsOn)
        {
            if (S.AutoRenew) SetFlash("ok", "Payment received.", $"3 GB added; your balance is {bal}. Auto-renew uses it when this exit runs low.");
            else SetFlash("ok", "Payment received.", $"3 GB added; your balance is {bal}. Renew automatically when an exit runs low?", B(("Turn on Auto-renew", "arOn"), ("Not now", "dismiss")));
        }
        else if (S.Conn == "paused") { SetFlash("ok", "Payment received.", $"3 GB added; your balance is {bal}."); if (S.Pause == "balance") S.Pause = "nodata"; }
        else if (S.AutoRenew) SetFlash("ok", "Payment received.", $"3 GB added; your balance is {bal}. Connect when you’re ready; renewals then run on their own.");
        else SetFlash("ok", "Payment received.", $"3 GB added; your balance is {bal}. Connecting sets aside 600 MB of it.", B(("Turn on Auto-renew", "arOn"), ("Not now", "dismiss")));
    }

    void ExitOffline()
    {
        var e = Ex(S.Loc);
        if (IsOn) { S.Lost = Left(); S.Alloc[S.Loc] = 0; e.Offline = true; PauseFor("offline"); }
        else
        {
            e.Offline = true; S.Alloc[e.Id] = 0; var b = Best();
            if (b != null) { S.Loc = b.Id; S.Mode = "auto"; }
            SetFlash("info", $"{e.City} went offline.", b != null ? $"Auto picked {b.City} instead." : "");
        }
    }

    void ConnDrop()
    {
        if (S.Conn != "connected") { Toast("Connect first: the drop happens to a live connection."); return; }
        S.Conn = "reconnecting";
        SetTimeout(4000, () => { if (S.Conn == "reconnecting") { S.Conn = "connected"; SetFlash("ok", "Reconnected.", $"Back through {Ex(S.Loc).City}."); } Emit(); });
    }

    void LdFeedback(bool on)
    {
        if (on) SetFlash("ok", "Lockdown on.", IsOn ? "If the VPN drops, nothing gets out." : "Nothing leaves this PC without the VPN.");
        else SetFlash("info", "Lockdown off.", IsOn ? "If the VPN drops, traffic goes direct." : "Traffic goes direct while the VPN is off.");
    }

    void StartSync()
    {
        S.Conn = "idle"; S.Pause = null; Home(); S.Sheet = null; S.Flash = null; S.Fq = new(); S.CTok = null; S.SyncT = 0;
        SyncStart?.Invoke();
    }

    public void EndSync()
    {
        S.SyncT = null; SyncEnd?.Invoke(false);
        if (S.StartConnect && !S.FirstRun) RequestConnect(S.Loc);
    }

    /// <summary>A fresh start, as on a page load of the prototype: the node has no funds yet.</summary>
    public void Reload() { S = new State { Gate = true, Balance = 0, Added = 0 }; ResetExits(); lastTick = Now; }

    void ResetExits() { for (int i = 0; i < EX.Count; i++) { EX[i].CopyFrom(BASE_EX[i]); EX[i].Offline = false; } }

    // ---------- presets (Demo controls → Jump to) ----------
    public void Preset(string k)
    {
        var o = S;
        S = new State
        {
            Speed = o.Speed, Notes = o.Notes, Fired = o.Fired, AutoRenew = o.AutoRenew, Popover = o.Popover, Topup = o.Topup, ArPast4 = o.ArPast4,
            Lockdown = o.Lockdown, AlwaysOn = o.AlwaysOn, StartConnect = o.StartConnect, Icons = o.Icons, Simple = o.Simple, Details = o.Details,
            ArIntro = k == "s00" ? false : o.ArIntro,
        };
        ResetExits();
        SyncEnd?.Invoke(true);
        void On(string id, double a, double sec = 754) { S.Loc = id; S.Alloc[id] = a; S.AllocN++; S.Conn = "connected"; S.ConnSec = sec; S.FirstRun = false; S.Free = false; }
        switch (k)
        {
            case "s01": On("nl", 560, 20); S.Balance = 4800; break;
            case "arc": On("nl", 30.4, 1900); S.Balance = 4800; S.AutoRenew = true; S.ArPast4 = false; S.ArCount = 4; S.Sheet = new Sheet { Type = "quota", Id = "nl", Check = true, Until = S.T + 300 }; break;
            case "s07": On("br", 126); S.AutoRenew = false; S.Balance = 3600; break;
            case "s08": On("br", 0); S.AutoRenew = false; S.Balance = 3600; PauseFor("nodata"); break;
            case "s10": On("br", 180); S.Balance = 3600; ExitOffline(); break;
            case "s13": On("nl", 410); S.Balance = 600; break;
            case "s14": On("br", 0); S.Balance = 0; PauseFor("balance"); break;
            case "s18c": S.FirstRun = false; S.Free = false; S.Balance = 0; S.Lockdown = false; S.LdOffPay = true; S.Pay = S.T + 120; S.Loc = "br"; S.Mode = "manual"; break;
            case "l1": On("nl", 410); ConnDrop(); break;
        }
        S.Warm = k == "s00"; S.WarmEnd = S.T + 180;
        if (S.Conn != "idle" || k == "s18c")
        {
            var hist = new List<Recent> { new() { Id = S.Loc, At = 0 } };
            int i = 0;
            foreach (var x in new[] { "ch", "de", "at", "pt" }.Where(x => x != S.Loc)) hist.Add(new Recent { Id = x, At = -3600 * (++i) });
            S.Recent = hist.Take(6).ToList();
        }
        Emit();
    }

    static readonly HashSet<string> KeepFlash = new() { "qual", "details", "sort", "row", "tab", "nclose", "q", "ptab", "pall", "moreMsgs", "balance", "tip" };
    static readonly HashSet<string> KeepFlash2 = new() { "dismiss", "dismissMsg", "arOn", "lockOn", "ldNot", "preset", "speed", "skip", "tgl", "bal", "sess", "mode", "theme", "codes", "rules" };

    /// <summary>Generic boolean fields that "tog" / "tgl" flip (S[d.k] = !S[d.k] in the prototype).</summary>
    bool Flip(string k)
    {
        switch (k)
        {
            case "autoRenew": return S.AutoRenew = !S.AutoRenew;
            case "lockdown": return S.Lockdown = !S.Lockdown;
            case "startConnect": return S.StartConnect = !S.StartConnect;
            case "topup": return S.Topup = !S.Topup;
            case "warm": return S.Warm = !S.Warm;
            case "popover": return S.Popover = !S.Popover;
            case "nextFull": return S.NextFull = !S.NextFull;
            case "failRenew": return S.FailRenew = !S.FailRenew;
            case "alwaysOn": return S.AlwaysOn = !S.AlwaysOn;
        }
        return false;
    }
    public bool Get(string k) => k switch
    {
        "autoRenew" => S.AutoRenew, "lockdown" => S.Lockdown, "startConnect" => S.StartConnect, "topup" => S.Topup, "warm" => S.Warm,
        "popover" => S.Popover, "nextFull" => S.NextFull, "failRenew" => S.FailRenew, "alwaysOn" => S.AlwaysOn, _ => false
    };

    // ---------- actions ----------
    /// <param name="a">action name</param><param name="id">d.id</param><param name="v">d.v</param><param name="k">d.k</param>
    public void Act(string a, string? id = null, string? v = null, string? k = null)
    {
        if (!KeepFlash.Contains(a) && !a.StartsWith("ev") && !KeepFlash2.Contains(a)) { S.Flash = null; S.Fq = new(); }
        if (Real != null && Real.Handle(a, id)) { Emit(); return; }
        switch (a)
        {
            case "connect": RequestConnect(S.Loc); break;
            case "cancel": S.CTok = null; S.Conn = "idle"; break;
            case "disconnect": Disconnect(); return;
            case "settings": Go("settings"); S.Stab = "general"; break;
            case "usage": S.Sheet = null; Go("settings"); S.Stab = "usage"; break;
            case "back": Back(); break;
            case "icons": S.Icons = v ?? "node"; break;
            case "arReset": S.ArIntro = false; S.Sheet = null; Toast("Auto-renew intro will show on the next Connect."); break;
            case "traffic": Go("traffic"); break;
            case "demo": Go("demo"); break;
            // Windows: Gnosis VPN runs its own kill switch in the Windows firewall, so Lockdown is an in-app toggle.
            case "lockTap": S.Lockdown = !S.Lockdown || S.LdOffPay; S.LdOffPay = false; LdFeedback(S.Lockdown); Toast(S.Lockdown ? "Lockdown on. Nothing leaves without the VPN." : "Lockdown off."); break;
            case "details": S.Details = !S.Details; break;
            case "sort": S.Sort = v ?? "fast"; break;
            case "row": S.OpenRow = S.OpenRow == id ? null : id; break;
            case "auto":
                {
                    var b = Best(); S.Mode = "auto"; if (b == null) break;
                    if (IsOn || S.Conn == "paused") RequestConnect(b.Id, true); else { S.Loc = b.Id; Home(); }
                    Emit(); return;
                }
            case "renew": S.Sheet = null; RenewNow(); return;
            case "bestAvail": { var b = Best(S.Loc); if (b != null) { S.Mode = "auto"; RequestConnect(b.Id); } Emit(); return; }
            case "switchBetter": S.Mode = "manual"; if (S.Better != null) RequestConnect(S.Better); return;
            case "notNow": S.Better = null; break;
            case "dismiss": S.Flash = S.Fq.Count > 0 ? Shift(S.Fq) : null; break;
            case "dismissMsg":
                {
                    var kk = k ?? "";
                    if (S.Flash != null && S.Flash.Lead == kk) S.Flash = S.Fq.Count > 0 ? Shift(S.Fq) : null;
                    else if (kk == "Please fund your VPN.") S.Topup = false;
                    else S.Fq = S.Fq.Where(x => x.Lead != kk).ToList();
                    break;
                }
            case "arOn": S.AutoRenew = true; S.Flash = null; S.Fq = new(); Toast("Auto-renew is on for all exits."); break;
            case "offAsk": S.OffConfirm = true; break;
            case "offCancel": S.OffConfirm = false; break;
            case "offDo": S.Conn = "idle"; S.Pause = null; S.OffConfirm = false; break;
            case "payOut": Go("payout"); break;
            case "payGo": S.Lockdown = false; S.LdOffPay = true; S.Conn = "idle"; S.Pause = null; S.Stack = new() { "add" }; break;
            case "addData":
                S.Sheet = null;
                if (S.Conn == "paused" && S.Pause == "balance" && S.Lockdown) { Go("payout"); break; }
                Go("add"); break;
            case "funding": OpenUrl?.Invoke(FUND_URL); if (S.Pay == null) S.Pay = S.T + 120; Toast("Prototype: the funding site opens in the browser. The payment lands about 2 min later."); break;
            case "copy": S.CopiedAt = Now; Toast("Address copied"); break;
            case "share": Toast("Prototype: the share sheet opens with your address."); break;
            case "lockOn": S.Lockdown = true; S.LdOffPay = false; Toast("Lockdown is on."); break;
            case "ldNot": S.LdOffPay = false; break;
            case "tab": S.Stab = v ?? "general"; break;
            case "tog":
                if (k == null) break;
                Flip(k);
                if (k == "autoRenew" && S.AutoRenew) S.Offered = true;
                if (k == "lockdown") { S.LdOffPay = false; LdFeedback(S.Lockdown); }
                break;
            case "sheetNo": if (S.Sheet is { Check: true }) S.ArDecl = S.AllocN; S.Sheet = null; break;
            case "gateFund": OpenUrl?.Invoke(FUND_URL); if (S.Pay == null) S.Pay = S.T + 120; break;
            case "gateDirect": Go("direct"); if (S.Pay == null) S.Pay = S.T + 120; break;
            case "gatePaid": S.Pay = S.T; PaymentArrived(); break;
            case "gateRestart":
                S.Balance = 0; S.Added = 0; S.Pay = null; Home(); S.Sheet = null; S.Flash = null; S.Fq = new(); S.Conn = "idle"; S.SyncT = null;
                SyncEnd?.Invoke(true); S.Gate = true; S.Fail = null; break;
            case "togHide": S.Popover = !S.Popover; break;
            case "togAR": S.AutoRenew = !S.AutoRenew; if (S.AutoRenew) S.Offered = true; break;
            case "togP4": if (S.AutoRenew) S.ArPast4 = !S.ArPast4; break;
            case "arCheckGo": S.ArCount = 0; S.Sheet = null; AutoRenewNow(); break;
            case "arCheckNo": S.ArDecl = S.AllocN; S.Sheet = null; break;
            case "arPastOn": S.ArPast4 = true; S.Sheet = null; AutoRenewNow(); Toast("Auto-renew keeps going without asking."); break;
            case "sheetGo":
                {
                    var sh = S.Sheet; if (sh == null) break; var sid = sh.Id;
                    if (sh.Dont) S.AskSwitch = false; if (sh.Intro) S.ArIntro = true; S.Sheet = null;
                    if (sh.Renew) { RenewNow(); Emit(); return; }
                    DoConnect(sid); return;
                }
            case "arOffGo":
                {
                    if (S.Sheet == null) break; var sid = S.Sheet.Id;
                    S.AutoRenew = false; S.ArIntro = true; S.Sheet = null; DoConnect(sid);
                    Toast("Auto-renew is off. We'll ask before setting data aside."); return;
                }
            case "dont": if (S.Sheet != null) S.Sheet.Dont = !S.Sheet.Dont; break;
            case "arOnGo":
                {
                    var sh = S.Sheet; if (sh == null) break; var sid = sh.Id;
                    S.AutoRenew = true; S.ArIntro = true; S.Offered = true; S.Sheet = null;
                    if (sh.Renew) { RenewNow(); Emit(); return; }
                    DoConnect(sid); return;
                }
            case "stay": S.Sheet = null; Home(); break;
            case "logsExport": Toast("Prototype: the file picker opens to save the logs."); break;
            case "logsUpload": Toast("Logs sent to support."); break;
            case "nclose": break;
            case "pick": Go("pick"); S.Ptab = "best"; S.Pall = false; S.Q = ""; break;
            case "pick2": S.Mode = "manual"; if (id != null) RequestConnect(id, true); return;
            case "ptab": S.Ptab = v ?? "best"; break;
            case "pall": S.Pall = true; break;
            case "view": S.Simple = !S.Simple; S.Details = !S.Simple; break;
            // demo controls
            case "preset": if (v != null) Preset(v); return;
            case "speed": S.Speed = double.Parse(v ?? "1", System.Globalization.CultureInfo.InvariantCulture); break;
            case "skip": { for (int i = 0; i < 300; i++) { S.T += 1; Step(1); } S.T = Math.Round(S.T); break; }
            case "sess":
                {
                    if (!IsOn) { S.Conn = "connected"; S.ConnSec = 0; S.FirstRun = false; S.Free = false; S.Pause = null; Home(); }
                    if (S.Balance < 1200) S.Balance = 4800;
                    var mbv = double.Parse(v ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                    S.Alloc[S.Loc] = mbv; S.Sheet = null;
                    foreach (var key in S.Fired.Keys.ToList()) if (System.Text.RegularExpressions.Regex.IsMatch(key, "^(u3|u6|a3|u2)(:|$)")) S.Fired.Remove(key);
                    Toast($"{v} MB left in this session at {Ex(S.Loc).City}."); break;
                }
            case "bal": S.Balance = double.Parse(v ?? "0", System.Globalization.CultureInfo.InvariantCulture); if (S.Balance > COST) S.BalEp++; break;
            case "evOffline": if (S.Conn == "connecting") break; ExitOffline(); break;
            case "evDrop": ConnDrop(); break;
            case "evTwo": LdFeedback(true); S.Update = UPD_V; SetFlash("lav", "Update available.", $"Gnosis VPN {UPD_V} is ready.", B(("Restart to update", "updNow"), ("Later", "updLater"))); break;
            case "evUpdate":
                if (S.Conn == "connecting") break; S.Update = UPD_V; Fire("upd:" + UPD_V + ":" + S.T, NUpd());
                SetFlash("lav", "Update available.", $"Gnosis VPN {UPD_V} is ready.", B(("Restart to update", "updNow"), ("Later", "updLater"))); break;
            case "updLater": S.Flash = S.Fq.Count > 0 ? Shift(S.Fq) : null; break;
            case "updNow":
                {
                    var was = IsOn; S.Update = null; S.Flash = null; S.Fq = new();
                    if (was)
                    {
                        S.Conn = "reconnecting";
                        SetTimeout(2600, () => { if (S.Conn == "reconnecting") { S.Conn = "connected"; SetFlash("ok", "Updated.", $"You're on Gnosis VPN {UPD_V} and connected again."); } Emit(); });
                    }
                    else SetFlash("ok", "Updated.", $"You're on Gnosis VPN {UPD_V}.");
                    break;
                }
            case "evPay": S.Pay = S.T; PaymentArrived(); break;
            case "tgl": if (k != null) Flip(k); break;
            case "fundScreen": Go("add"); S.Sheet = null; break;
            case "restart": S.Fail = null; StartSync(); break;
            case "failShow": S.Fail = new Failure { Title = "Can’t connect to Blokli", Code = "BLINDTEXT" }; Home(); S.Sheet = null; break;
            case "failRetry": S.Fail = null; StartSync(); break;
            case "copyCode": Toast("Failure code copied"); break;
            case "docs": Toast("Prototype: opens the Gnosis VPN docs in the browser."); break;
            case "support": Toast("Prototype: opens support in the browser, with the failure code."); break;
            case "syncSkip": EndSync(); break;
            case "reset": { var sp = S.Speed; S = new State { Speed = sp }; ResetExits(); SyncEnd?.Invoke(true); break; }
            case "clearLog": S.Notes = new(); S.Fired = new(); S.Unread = 0; break;
        }
        Emit();
    }

    static Flash Shift(List<Flash> q) { var f = q[0]; q.RemoveAt(0); return f; }

    // ---------- clock (setInterval 250 ms in the prototype; the UI calls Tick) ----------
    double lastTick = Now;
    public void Tick()
    {
        var now = Now; var dt = (now - lastTick) / 1000 * S.Speed; lastTick = now;
        if (Real != null) { S.T += dt; Real.Tick(); Emit(); return; }
        if (S.SyncT != null) { S.SyncT += dt; if (S.SyncT >= SYNC_LEN) EndSync(); }
        S.T += dt; Step(dt); Emit();
    }
    readonly Random rnd = new();
    public void RelayTick() { if (S.Conn is "connected" or "connecting" or "reconnecting") S.Relay = (S.Relay + 1 + rnd.Next(4)) % 6; }
}
