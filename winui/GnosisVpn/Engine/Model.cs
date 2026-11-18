using System.Collections.Generic;

namespace GnosisVpn.Engine;

/// <summary>An exit node, as in the prototype's EX list (engine.js).</summary>
public sealed class Exit
{
    public required string Id;
    /// <summary>Display name: "Country #xxxx" (country plus a short id of its public address).</summary>
    public string City = "";
    public string Place = "";
    public required string Country;
    public int Ping;
    public int Rel;
    public int Spots;
    public int Slots;
    public double Pv;
    public int Fr;
    public bool Measured;
    public string Busy = "";
    public bool Full;
    public bool Offline;
    public bool? Routable;

    public Exit Clone() => (Exit)MemberwiseClone();
    public void CopyFrom(Exit o)
    {
        City = o.City; Place = o.Place; Ping = o.Ping; Rel = o.Rel; Spots = o.Spots; Slots = o.Slots; Pv = o.Pv;
        Fr = o.Fr; Measured = o.Measured; Busy = o.Busy; Full = o.Full; Offline = o.Offline; Routable = o.Routable;
    }
}

/// <summary>Route grade (routeGrade() in gnosis_vpn-app): k = hidden | weak | unmeasured | ok.</summary>
public readonly record struct Grade(string K, int N, string Label, string? Tone = null, double? Sc = null);

public sealed class Btn
{
    public Btn(string label, string act) { Label = label; Act = act; }
    public string Label { get; }
    public string Act { get; }
}

/// <summary>An in-app message (S.flash and the queue S.fq).</summary>
public sealed class Flash
{
    public required string Tone;
    public required string Lead;
    public string Text = "";
    public List<Btn> Btns = new();
}

/// <summary>Dialogs ("sheets" in the demo): quota (600 MB), nobal, switch, balance (the flyout).</summary>
public sealed class Sheet
{
    public required string Type;
    public string Id = "";
    public bool Renew, Soon, Intro, Check, Dont;
    public double Until;
}

/// <summary>A system notification (toast) and its rule.</summary>
public sealed class Note
{
    public required string Rule;
    public string? Code;
    public required string Title;
    public required string Body;
    public List<Btn> Acts = new();
    public bool Silent, Proposed, Gone, Seen;
    public double At;
    public string Id = "";
}

public sealed class Recent { public required string Id; public double At; }

public sealed class Rule
{
    public required string K, Code, Name, Text;
    public bool Silent, Proposed;
}

public sealed class FullMsg { public required string Id; public string? Alt; }

public sealed class Failure { public required string Title, Code; }

/// <summary>The whole state (engine.js fresh()). Field names follow the prototype.</summary>
public sealed class State
{
    public string Conn = "idle";           // idle | connecting | connected | reconnecting | paused
    public string? Pause;                  // nodata | offline | balance | renewfail
    public string Loc = "de";
    public string Mode = "auto";           // auto | manual
    public Dictionary<string, double> Alloc = new();
    public int AllocN;
    public bool Free;
    public double Balance = 4800, Added = 4800;
    public int BalEp;
    public bool AutoRenew = true, Lockdown = true, AlwaysOn = true, StartConnect = true, Topup, Warm = true;
    public double WarmEnd = 180;
    public bool Popover;
    public string Icons = "node";
    public bool ArIntro, AskSwitch = true;
    public int ArCount;
    public bool ArPast4;
    public int ArDecl = -1;
    public double T, ConnSec;
    public double Speed = 1;
    public bool FirstRun = true;
    public List<string> Stack = new();
    public string Stab = "general";
    public Sheet? Sheet;
    public bool OffConfirm, Details, Simple = true;
    public Flash? Flash;
    public List<Flash> Fq = new();
    public bool LdOffPay;
    public double? Pay;
    public bool Offered;
    public string? Better;
    public string Sort = "fast", Q = "";
    public string? OpenRow;
    public FullMsg? FullMsg;
    public double Lost, RetryAt;
    public double NextCheck = 40;
    public bool Checking, SwapAnim, FailRenew, NextFull;
    public Dictionary<string, double> AllocAt = new();
    public List<Note> Notes = new();
    public Dictionary<string, double> Fired = new();
    public List<Recent> Recent = new();
    public string Ptab = "best";
    public bool Pall;
    public int Relay = 2;
    public int Unread;
    public bool Gate;
    public double? SyncT;
    public Failure? Fail;
    public string? Update;
    public int? AskedSoon;
    public double CStart, CDur;
    public object? CTok;
    public double CopiedAt = -1e9;
}
