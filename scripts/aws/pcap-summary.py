"""Summarise a pktmon pcapng (stdlib only): per-second outer traffic of the HOPR node.
Usage: pcap-summary.py CAP.pcapng [from_unix to_unix]
Prints per-second UDP bytes in/out (all peers) and TCP bytes (excluding SSH), plus the top UDP peers.
pktmon may record a packet at more than one point of the NIC stack; numbers are relative."""
import struct, sys, collections
path = sys.argv[1]
lo, hi = (float(sys.argv[2]), float(sys.argv[3])) if len(sys.argv) > 3 else (0, 1e12)
data = open(path, "rb").read()
off, le, tsres, link = 0, "<", {}, {}
sec = collections.defaultdict(lambda: [0, 0, 0, 0, 0, 0])  # udp_in, udp_out, tcp_in, tcp_out, pkts_in, pkts_out
peers = collections.Counter()
local = set()
def ip_of(b): return ".".join(map(str, b))
while off + 12 <= len(data):
    btype, blen = struct.unpack_from(le + "II", data, off)
    if btype == 0x0A0D0D0A:
        bom = data[off + 8:off + 12]; le = "<" if bom == b"\x4d\x3c\x2b\x1a" else ">"
        btype, blen = struct.unpack_from(le + "II", data, off); n_if = 0
    if blen < 12: break
    body = data[off + 8: off + blen - 4]
    if btype == 1:
        lt, = struct.unpack_from(le + "H", body, 0); link[len(link)] = lt; tsres[len(tsres)] = 1e-6
        o = 8
        while o + 4 <= len(body):
            code, ln = struct.unpack_from(le + "HH", body, o)
            if code == 0: break
            if code == 9: v = body[o + 4]; tsres[len(tsres) - 1] = (2 ** -(v & 0x7f)) if v & 0x80 else 10 ** -v
            o += 4 + ((ln + 3) & ~3)
    elif btype == 6:
        iface, th, tl, cap, orig = struct.unpack_from(le + "IIIII", body, 0)
        ts = ((th << 32) | tl) * tsres.get(iface, 1e-6)
        pkt = body[20:20 + cap]
        if lo <= ts <= hi and link.get(iface, 1) == 1 and len(pkt) >= 34:
            et = struct.unpack_from(">H", pkt, 12)[0]; p = 14
            if et == 0x8100: et = struct.unpack_from(">H", pkt, 16)[0]; p = 18
            if et == 0x0800:
                ihl = (pkt[p] & 15) * 4; proto = pkt[p + 9]; tot = struct.unpack_from(">H", pkt, p + 2)[0]
                src, dst = ip_of(pkt[p + 12:p + 16]), ip_of(pkt[p + 16:p + 20])
                out = src.startswith("172.31.")
                peer = dst if out else src
                sp, dp = struct.unpack_from(">HH", pkt, p + ihl) if len(pkt) >= p + ihl + 4 else (0, 0)
                s = sec[int(ts)]
                if proto == 17:
                    s[1 if out else 0] += tot; peers[(peer, dp if out else sp)] += tot
                elif proto == 6 and 22 not in (sp, dp):
                    s[3 if out else 2] += tot
                s[5 if out else 4] += 1
    off += blen
if not sec: print("no packets"); sys.exit()
t0 = min(sec)
print("t_rel unix        udp_in_kB udp_out_kB tcp_in_kB tcp_out_kB pk_in pk_out")
for t in range(t0, max(sec) + 1):
    s = sec.get(t, [0] * 6)
    print(f"{t - t0:5d} {t} {s[0] / 1000:9.1f} {s[1] / 1000:10.1f} {s[2] / 1000:9.1f} {s[3] / 1000:10.1f} {s[4]:5d} {s[5]:6d}")
print("top UDP peers (ip, port, kB):")
for (ip, port), b in peers.most_common(12): print(f"  {ip}:{port} {b / 1000:.0f}")
