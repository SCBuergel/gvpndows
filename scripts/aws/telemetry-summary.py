"""Summarise telemetry.csv: stall windows (>= 3 s with no data down while connected), and what the
core reported then (stall counters, PIX phase, awaiting payment, exit ping). Usage: telemetry-summary.py telemetry.csv"""
import csv, sys
rows = list(csv.DictReader(open(sys.argv[1])))
print(len(rows), "rows")
win, cur = [], None
for r in rows:
    quiet = r["conn"] == "connected" and int(r["down_kbps"] or 0) < 8
    if quiet:
        cur = cur or {"start": r, "rows": []}; cur["rows"].append(r)
    elif cur:
        win.append(cur); cur = None
if cur: win.append(cur)
for w in win:
    if len(w["rows"]) < 3: continue
    a, b = w["rows"][0], w["rows"][-1]
    pings = [x["exit_ping_ms"] for x in w["rows"]]
    print(f"quiet {len(w['rows'])} s from {a['utc']} (conn age {a['conn_age_s']} s): pix {a['pix_phase']}->{b['pix_phase']}, "
          f"paid {a['chunks_paid']}->{b['chunks_paid']}, bytes_left {a['bytes_left']}->{b['bytes_left']}, awaiting_payment {b['awaiting_payment_s']}, "
          f"stall_failed_pings {b['stall_failed_pings']}, up_pps~{a['up_pps']}, exit ping ok {sum(p.isdigit() for p in pings)}/{len(pings)}, dropped {a['dropped']}->{b['dropped']}")
