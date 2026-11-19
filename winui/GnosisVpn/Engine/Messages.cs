using System.Collections.Generic;
using System.Linq;

namespace GnosisVpn.Engine;

/// <summary>A message on the Home page (an InfoBar).</summary>
public sealed record Moment(string Tone, string Icon, string Lead, string Text, List<Btn>? Btns, bool Own = false)
{
    public Moment WithOwn(bool own) => this with { Own = own };
}

/// <summary>The Home page's messages and a short code for the current screen (logged by --snap).</summary>
public static class Messages
{
    public static readonly Dictionary<string, string> ToneIcon = new() { ["ok"] = "check_circle", ["warn"] = "warning", ["bad"] = "error", ["info"] = "info", ["lav"] = "info" };

    static Moment? State(AppEngine g)
    {
        var S = g.S; var e = g.Ex(S.Loc);
        if (S.Conn == "reconnecting") return new("warn", "warning", "Reconnecting.", $"Finding a new route to {e.City}.", null);
        if (S.Conn == "connecting") return new("info", "info", $"Connecting to {e.City}.", "", null);
        return null;
    }

    public static List<Moment> Moments(AppEngine g)
    {
        var S = g.S;
        var outp = new List<Moment>(); var seen = new HashSet<string>();
        void Add(Moment? m, bool own) { if (m != null && seen.Add(m.Lead)) outp.Add(m.WithOwn(own)); }
        if (S.Flash != null) Add(new(S.Flash.Tone, ToneIcon.GetValueOrDefault(S.Flash.Tone, "info"), S.Flash.Lead, S.Flash.Text, S.Flash.Btns.Count > 0 ? S.Flash.Btns : null), true);
        Add(State(g), false);
        foreach (var x in S.Fq) Add(new(x.Tone, ToneIcon.GetValueOrDefault(x.Tone, "info"), x.Lead, x.Text, x.Btns.Count > 0 ? x.Btns : null), true);
        int Rank(string t) => t == "bad" ? 0 : t == "warn" ? 1 : 2;
        return outp.Select((m, i) => (m, i)).OrderBy(x => Rank(x.m.Tone)).ThenBy(x => x.i).Select(x => x.m).ToList();
    }

    /// <summary>Screen code for the current state (logged by the test drivers).</summary>
    public static string ScreenCode(AppEngine g)
    {
        var S = g.S;
        if (S.Fail != null) return "E1";
        if (S.SyncT != null) return "I0";
        if (S.Gate) return g.Page == "direct" ? "I-1d" : "I-1";
        return S.Conn switch
        {
            "connecting" => "S00b",
            "reconnecting" => "L1",
            "connected" => "S01",
            _ => g.Page switch { "pick" => "S03s", "settings" => "S19n", "add" => "S18", "direct" => "I-1d", "traffic" => "T", _ => S.Mode == "manual" ? "F1" : "F2" },
        };
    }
}
