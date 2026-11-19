using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace GnosisVpn.Engine;

/// <summary>
/// The app's UI state and its actions. Everything about the VPN itself (exits, connection, payment,
/// balance) comes from the Gnosis VPN service through <see cref="Real"/>; this class only holds what
/// the pages show and handles the UI-only actions (navigation, sorting, search, the view mode).
/// There is no simulated backend.
/// </summary>
public sealed class AppEngine
{
    public static readonly Dictionary<string, string> TIPS = new()
    {
        ["best"] = "Best route quality first.", ["recent"] = "The last 6 exits you used, newest first.", ["alpha"] = "Sorted by country name.",
    };
    /// <summary>Path value below which a route counts as weak (gnosis_vpn-lib's WEAK_ROUTE_VALUE).</summary>
    const double MIN_PATH_VALUE = 0.9;

    public readonly List<Exit> EX = new();
    public State S = new();
    /// <summary>The Gnosis VPN service; set right after construction and always present.</summary>
    public RealBackend Real = null!;

    // ---------- UI hooks ----------
    public event Action? Render;
    public event Action<string>? Snack;
    public event Action<string>? OpenUrl;
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static double Now => Clock.Elapsed.TotalMilliseconds;

    public static uint Hash32(string str)
    {
        uint h = 0x811c9dc5;
        foreach (var ch in str) { h ^= ch; h = unchecked(h * 0x01000193); }
        return h;
    }
    /// <summary>A short id of an exit, shown next to its country.</summary>
    public static string Xid(string id) => Hash32("pub:" + id).ToString("x8")[..4];

    public Exit? ExOrNull(string? id) => id == null ? null : EX.FirstOrDefault(e => e.Id == id);
    /// <summary>The exit, or a placeholder while the service has not listed it (yet).</summary>
    public Exit Ex(string id) => ExOrNull(id) ?? new Exit { Id = id, Country = "Unknown", City = id };

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
    /// <summary>Data left at an exit in this session (MB), as the core reports it.</summary>
    public double Left(string? id = null) => S.Alloc.TryGetValue(id ?? S.Loc, out var v) ? v : 0;
    public bool IsOn => S.Conn is "connected" or "reconnecting";

    // ---------- route grade, from the core's route health ----------
    public Grade GradeOf(Exit? e)
    {
        if (e == null || e.Routable == false) return new Grade("hidden", 0, "Unavailable");
        if (!e.Measured) return new Grade("unmeasured", 0, "Not measured yet", null, -0.5);
        if (e.Pv < MIN_PATH_VALUE) return new Grade("weak", 1, "Weak route", "bad", -1 + e.Pv);
        // the client defines one threshold (WEAK_ROUTE_VALUE 0.9): above it a route is good; the
        // value itself still orders the list
        return new Grade("ok", 4, "Good", "ok", e.Pv);
    }
    public Exit? Best(string? except = null) =>
        EX.Where(e => e.Id != except && GradeOf(e).K == "ok").OrderByDescending(e => GradeOf(e).Sc).FirstOrDefault();

    // ---------- in-app messages ----------
    void Emit() => Render?.Invoke();
    public void Toast(string msg) => Snack?.Invoke(msg);

    public void SetFlash(string tone, string lead, string text, List<Btn>? btns = null)
    {
        if (S.Flash != null && S.Flash.Lead != lead)
        {
            var cur = S.Flash;
            S.Fq = new[] { cur }.Concat(S.Fq.Where(x => x.Lead != lead && x.Lead != cur.Lead)).Take(3).ToList();
        }
        S.Flash = new Flash { Tone = tone, Lead = lead, Text = text, Btns = btns ?? new() };
    }
    static Flash Shift(List<Flash> q) { var f = q[0]; q.RemoveAt(0); return f; }

    static readonly HashSet<string> KeepFlash = new() { "details", "row", "dismiss", "dismissMsg", "q", "ptab", "pall", "copy", "view", "icons" };

    // ---------- actions ----------
    /// <param name="a">action name</param><param name="id">exit id</param><param name="v">value</param><param name="k">message key</param>
    public void Act(string a, string? id = null, string? v = null, string? k = null)
    {
        if (!KeepFlash.Contains(a)) { S.Flash = null; S.Fq = new(); }
        if (Real.Handle(a, id)) { Emit(); return; }
        switch (a)
        {
            case "settings": Go("settings"); break;
            case "back": Back(); break;
            case "icons": S.Icons = v ?? "node"; break;
            case "details": S.Details = !S.Details; break;
            case "row": S.OpenRow = S.OpenRow == id ? null : id; break;
            case "dismiss": S.Flash = S.Fq.Count > 0 ? Shift(S.Fq) : null; break;
            case "dismissMsg":
                {
                    var kk = k ?? "";
                    if (S.Flash != null && S.Flash.Lead == kk) S.Flash = S.Fq.Count > 0 ? Shift(S.Fq) : null;
                    else S.Fq = S.Fq.Where(x => x.Lead != kk).ToList();
                    break;
                }
            case "addData": Go("add"); break;
            case "copy": S.CopiedAt = Now; Toast("Address copied"); break;
            case "gateDirect": Go("direct"); break;
            case "pick": Go("pick"); S.Ptab = "best"; S.Pall = false; S.Q = ""; break;
            case "ptab": S.Ptab = v ?? "best"; break;
            case "pall": S.Pall = true; break;
            case "view": S.Simple = !S.Simple; S.Details = !S.Simple; break;
            case "copyCode": Toast("Failure code copied"); break;
            case "failRetry": S.Fail = null; break;
        }
        Emit();
    }

    // ---------- clock: the UI calls Tick every 250 ms; it polls the service ----------
    double lastTick = Now;
    public void Tick()
    {
        var now = Now; S.T += (now - lastTick) / 1000; lastTick = now;
        Real.Tick(); Emit();
    }
    readonly Random rnd = new();
    /// <summary>Which of the relays the connection animation highlights (cosmetic).</summary>
    public void RelayTick() { if (S.Conn is "connected" or "connecting" or "reconnecting") S.Relay = (S.Relay + 1 + rnd.Next(4)) % 6; }
}
