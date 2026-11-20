using System.Collections.Generic;

namespace GnosisVpn.Engine;

/// <summary>An exit, from the service's destination list and its route health.</summary>
public sealed class Exit
{
    public required string Id;
    /// <summary>Display name: the exit's name from the network, or "Country #xxxx".</summary>
    public string City = "";
    public string Place = "";
    public required string Country;
    /// <summary>ISO 3166 country code from the exit's geo data ("DE"), or "".</summary>
    public string Cc = "";
    /// <summary>Round trip to the exit in ms (the probe's status round trip, or the tunnel ping); 0 = unknown.</summary>
    public int Ping;
    /// <summary>The exit's reported load (its own health summary), or "".</summary>
    public string Load = "";
    /// <summary>Best path value of the last route walk, in (0, 1]; higher is better.</summary>
    public double Pv;
    /// <summary>A route walk has found paths, so <see cref="Pv"/> is real.</summary>
    public bool Measured;
    public bool? Routable;
}

/// <summary>Route grade (routeGrade() in gnosis_vpn-app): k = hidden | weak | unmeasured | ok.</summary>
public readonly record struct Grade(string K, int N, string Label, string? Tone = null, double? Sc = null);

public sealed class Btn
{
    public Btn(string label, string act) { Label = label; Act = act; }
    public string Label { get; }
    public string Act { get; }
}

/// <summary>An in-app message (S.Flash and the queue S.Fq).</summary>
public sealed class Flash
{
    public required string Tone;
    public required string Lead;
    public string Text = "";
    public List<Btn> Btns = new();
}

public sealed class Recent { public required string Id; public double At; }
public sealed class Failure { public required string Title, Code; }

/// <summary>What the pages show. VPN facts come from the service (RealBackend.Apply).</summary>
public sealed class State
{
    public string Conn = "idle";           // idle | connecting | connected | reconnecting
    public string Loc = "";
    public string Mode = "auto";           // auto | manual
    /// <summary>Data left per exit (MB), from the core's PIX chunk accounting.</summary>
    public Dictionary<string, double> Alloc = new();
    /// <summary>The node's Safe balance in MB at the PIX price per byte.</summary>
    public double Balance;
    public string Icons = "country";       // country | node | flag | none
    public double T, ConnSec;
    /// <summary>Seconds until this session's paid data runs out at the current rate (the core's
    /// pix.empty_in), or null while the core cannot tell.</summary>
    public double? EmptyIn;
    /// <summary>When the current connect attempt started (connecting animation), and its expected length.</summary>
    public double CStart, CDur = 30000;
    public List<string> Stack = new();
    public bool Details, Simple = true, BalanceOpen;
    public Flash? Flash;
    public List<Flash> Fq = new();
    public string Q = "";
    public string? OpenRow;
    public List<Recent> Recent = new();
    public string Ptab = "best";
    public bool Pall;
    public int Relay = 2;
    /// <summary>The node waits for funds (Fund first screen).</summary>
    public bool Gate;
    /// <summary>Progress of the core's start-up (seconds on the 0..120 scale of the sync screen), or null.</summary>
    public double? SyncT;
    public double CopiedAt = -1e9;
    public Failure? Fail;
}
