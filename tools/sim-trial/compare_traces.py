#!/usr/bin/env python3
"""compare_traces.py - the Unreal trial's parity comparator (plan A section 3.5).

Every leg of check (a) (the .NET CLI, the C++ harness, Godot, Unreal) writes one trace format:

    # key: value            header: leg, host, runtime, config, commit, dirty, algo, scenario_sha256, orders_sha256,
                            seed, ai, hash.start_state, hash.canonical_model, hash.content, hash.ruleset,
                            hash.agreement, hash.tick0, units_at_start (native legs add dll_sha256, Unreal adds
                            p_commit, ue_module_sha256, shots, max_steps_per_frame)
    <tick> <HASH8>          body, one line per tick 1..N (the golden body format)
    # digest.<t>, # wide.<t>   at t = 0/300/900/1440 (units digest, wide digest; taken after the step that produced
                            tick t and before the orders of tick t)
    # orders_digest, orders_applied, orders_dropped, alive_end, verdict    trailer
                            (verdict = Verdict[Player1] | Verdict[Player2] << 4; 0 = both undecided)

The reference for a variant ("set") is committed: its golden, its meta sidecar (the header fields every leg shares) and
its trailer sidecar (the digests and order counts every leg must reproduce): Golden/trial-1000{,-ai}.golden.txt,
.meta.txt and .trailer.txt, all written by Trial1000GoldenTests in record mode and checked against the build by it in
normal mode. This tool treats them as a leg named `meta`, and every supplied leg is compared with them (never with
another leg), so a run with a single leg is a full check. Usage:

    compare_traces.py --set main|ai [--final] [--out DIR] [--dll PATH] [--golden-dir DIR] [--orders CSV] name=path ...

Checks per supplied leg (all must pass; exit status 0 iff every check of every leg passes):
  ticks    the body is exactly ticks 1..N once each (N = the golden's length) and every hash equals the golden's;
           the first divergent tick is named
  h0       hash.start_state/canonical_model/content/ruleset/agreement and hash.tick0 equal the meta's
  header   the per-leg keys leg, host, runtime, config, commit, dirty are present and non-empty; every other meta key
           (algo, scenario_sha256, orders_sha256, seed, ai, units_at_start) is present and equals the meta's (hex
           numbers compared as numbers; initial_delay and item_registry are meta-only)
  digests  digest.t and wide.t exist for t in 0/300/900/1440 (those <= N) and equal the trailer sidecar's
  orders   orders_digest equals the FNV-1a 64 this tool computes over the order CSV (which must also equal the
           trailer's); orders_applied, orders_dropped, alive_end and verdict are integers (a malformed value fails)
           and equal the trailer's; orders_applied + orders_dropped equals the CSV rows with tick < N; in the main set
           nothing is dropped; for N = 1440, verdict is 0 and alive_end < 600
--final adds: exactly one `commit` over all legs, every `dirty` is 0, every native leg (any leg not named cli* or
godot*) carries dll_sha256, every dll_sha256 is the same single value and equals the sha256 of --dll (default
NAT/bin/publish/ChimeraSim.dll), every `unreal*` leg has the same single ue_module_sha256. The summary line then reads
    RESULT set=main legs=7 ticks=1440 all_equal=yes commits=1 dirty=0 dll=1 ue_build=1
(legs counts the meta leg). Writes compare.json and parity.png into --out (default: the current directory; give the
main and ai sets different --out directories). Exit 2 = the reference files themselves are missing or inconsistent.
Traces are read as UTF-8 (a UTF-8 BOM is accepted; UTF-16 fails the leg with a message).
"""
import argparse
import hashlib
import json
import os
import re
import sys

REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
DEFAULT_GOLDEN_DIR = os.path.join(REPO, "godot", "ProjectChimera.Sim.Tests", "Golden")
DEFAULT_ORDERS = os.path.join(REPO, "godot", "ProjectChimera.Sim.Tests", "Trial", "trial_1000.orders.csv")
DEFAULT_DLL = os.path.join(REPO, "godot", "ProjectChimera.Sim.Native", "bin", "publish", "ChimeraSim.dll")

SETS = {"main": "trial-1000", "ai": "trial-1000-ai"}
H0_KEYS = ["hash.start_state", "hash.canonical_model", "hash.content", "hash.ruleset", "hash.agreement", "hash.tick0"]
META_ONLY = {"initial_delay", "item_registry"}
LEG_KEYS = ["leg", "host", "runtime", "config", "commit", "dirty"]  # per-leg header keys (values differ by leg)
COUNT_KEYS = ["orders_applied", "orders_dropped", "alive_end", "verdict"]
DIGEST_TICKS = [0, 300, 900, 1440]
KEY_RE = re.compile(r"^#\s+([a-z0-9_.]+):\s*(.*?)\s*$")
BODY_RE = re.compile(r"^(\d+)\s+([0-9A-Fa-f]{8})\s*$")
INT_RE = re.compile(r"^-?\d+$")
FNV_OFFSET = 14695981039346656037
FNV_PRIME = 1099511628211
MASK64 = (1 << 64) - 1


# ── parsing ────────────────────────────────────────────────────────────────────────────────────────────────────

def parse_trace(path):
    """Return (fields, body, bad) where fields maps `# key: value` keys to strings, body is a list of (tick, hash)
    and bad lists the lines that are neither."""
    fields, body, bad = {}, [], []
    with open(path, "r", encoding="utf-8-sig") as f:
        for ln, raw in enumerate(f, 1):
            line = raw.rstrip("\r\n")
            if not line.strip():
                continue
            m = BODY_RE.match(line)
            if m:
                body.append((int(m.group(1)), int(m.group(2), 16)))
                continue
            m = KEY_RE.match(line)
            if m:
                fields[m.group(1)] = m.group(2)
                continue
            if not line.startswith("#"):
                bad.append("line %d: unparseable '%s'" % (ln, line[:60]))
    return fields, body, bad


def parse_meta(path):
    """A `key: value` sidecar (meta or trailer); lines starting with # are prose."""
    meta = {}
    with open(path, "r", encoding="utf-8-sig") as f:
        for raw in f:
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            k, _, v = line.partition(":")
            meta[k.strip()] = v.strip()
    return meta


def parse_golden(path):
    """Golden body + its `checksum_algo_version`."""
    algo, body = None, []
    with open(path, "r", encoding="utf-8") as f:
        for raw in f:
            line = raw.strip()
            if not line:
                continue
            m = BODY_RE.match(line)
            if m:
                body.append((int(m.group(1)), int(m.group(2), 16)))
                continue
            m = re.match(r"^#\s+checksum_algo_version:\s*(\d+)\s*$", line)
            if m:
                algo = m.group(1)
    return algo, body


def norm(v):
    """Comparable form of a header value: hex numbers become ints, 64-digit sha256 strings are lower-cased, everything
    else compares as written (stripped)."""
    v = (v or "").strip()
    if re.fullmatch(r"0[xX][0-9A-Fa-f]+", v):
        return int(v, 16)
    if re.fullmatch(r"[0-9a-fA-F]{64}", v):
        return v.lower()
    return v


def orders_rows(path):
    rows = []
    with open(path, "r", encoding="utf-8") as f:
        header = f.readline().strip()
        if header != "tick,faction,unit_ref,cmd,x_raw,z_raw,slot":
            raise ValueError("orders csv header is '%s'" % header)
        for raw in f:
            line = raw.strip()
            if line:
                rows.append([int(x) for x in line.split(",")])
    return rows


def orders_digest(rows):
    """FNV-1a 64 over every row in submit order, each of the seven fields as a little-endian int32 (OrderScript.Digest)."""
    h = FNV_OFFSET
    for row in rows:
        for v in row:
            for b in (v & 0xFFFFFFFF).to_bytes(4, "little"):
                h = ((h ^ b) * FNV_PRIME) & MASK64
    return h


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# ── comparison ─────────────────────────────────────────────────────────────────────────────────────────────────

def check_leg(name, path, golden, meta, trailer, n, rows, set_name):
    """Compare one leg with the committed golden, meta and trailer; returns a result dict (ok flags + messages)."""
    res = {"name": name, "path": path, "ok": True, "messages": [], "equal_ticks": 0, "total_ticks": n,
           "first_divergence": None, "h0_equal": 0, "h0_total": len(H0_KEYS), "header_ok": True, "digests_ok": True,
           "orders_ok": True, "per_tick_equal": [None] * n}

    def fail(msg):
        res["ok"] = False
        res["messages"].append(msg)

    def unreadable(msg):
        fail(msg)
        res.update(fields={}, header_ok=False, digests_ok=False, orders_ok=False)
        return res

    try:
        fields, body, bad = parse_trace(path)
    except OSError as e:
        return unreadable("cannot read trace: %s" % e)
    except UnicodeDecodeError as e:
        return unreadable("trace is not UTF-8 text (write it as UTF-8, not UTF-16): %s" % e)
    res["fields"] = fields
    for b in bad:
        fail(b)

    # ticks
    ticks_seen = [t for t, _ in body]
    expected = list(range(1, n + 1))
    if sorted(ticks_seen) != expected:
        missing = sorted(set(expected) - set(ticks_seen))
        extra = sorted(set(ticks_seen) - set(expected))
        dupes = len(ticks_seen) - len(set(ticks_seen))
        fail("tick set is not exactly 1..%d once each (missing %d%s, extra %d, duplicated %d)" % (
            n, len(missing), (" first=%d" % missing[0]) if missing else "", len(extra), dupes))
    gold = dict(golden)
    got = {}
    for t, h in body:
        got.setdefault(t, h)
    first = None
    for t in range(1, n + 1):
        if t not in got:
            res["per_tick_equal"][t - 1] = None
            continue
        eq = got[t] == gold[t]
        res["per_tick_equal"][t - 1] = eq
        if eq:
            res["equal_ticks"] += 1
        elif first is None:
            first = t
    if first is not None:
        res["first_divergence"] = {"tick": first, "expected": "%08X" % gold[first], "actual": "%08X" % got[first]}
        fail("first divergent tick %d: golden %08X, leg %08X" % (first, gold[first], got[first]))

    # h0
    for k in H0_KEYS:
        if k not in fields:
            fail("header is missing %s" % k)
        elif norm(fields[k]) == norm(meta[k]):
            res["h0_equal"] += 1
        else:
            fail("%s: leg %s, meta %s" % (k, fields[k], meta[k]))

    # header presence (per-leg keys) and equality (shared keys)
    for k in LEG_KEYS:
        if k not in fields or not fields[k].strip():
            res["header_ok"] = False
            fail("header is missing %s" % k)
    for k, mv in meta.items():
        if k in META_ONLY or k in H0_KEYS:
            continue
        if k not in fields:
            res["header_ok"] = False
            fail("header is missing %s" % k)
        elif norm(fields[k]) != norm(mv):
            res["header_ok"] = False
            fail("header %s: leg %s, meta %s" % (k, fields[k], mv))

    # digests, against the committed trailer
    for t in [t for t in DIGEST_TICKS if t <= n]:
        for pre in ("digest", "wide"):
            k = "%s.%d" % (pre, t)
            if k not in fields:
                res["digests_ok"] = False
                fail("trailer is missing %s" % k)
            elif norm(fields[k]) != norm(trailer[k]):
                res["digests_ok"] = False
                fail("%s: leg %s, trailer %s" % (k, fields[k], trailer[k]))

    # orders and end-state counts
    exp_rows = [r for r in rows if r[0] < n]
    exp_digest = orders_digest(rows)
    if "orders_digest" not in fields:
        res["orders_ok"] = False
        fail("trailer is missing orders_digest")
    elif norm(fields["orders_digest"]) != exp_digest:
        res["orders_ok"] = False
        fail("orders_digest: leg %s, computed from the csv 0x%016X" % (fields["orders_digest"], exp_digest))
    vals = {}
    for k in COUNT_KEYS:
        if k not in fields:
            res["orders_ok"] = False
            fail("trailer is missing %s" % k)
        elif not INT_RE.match(fields[k].strip()):
            res["orders_ok"] = False
            fail("%s is not an integer: '%s'" % (k, fields[k]))
        else:
            vals[k] = int(fields[k])
            if vals[k] != int(trailer[k]):
                res["orders_ok"] = False
                fail("%s: leg %d, trailer %s" % (k, vals[k], trailer[k]))
    if "orders_applied" in vals and "orders_dropped" in vals:
        applied, dropped = vals["orders_applied"], vals["orders_dropped"]
        if applied + dropped != len(exp_rows):
            res["orders_ok"] = False
            fail("orders_applied %d + orders_dropped %d != %d csv rows with tick < %d" % (applied, dropped, len(exp_rows), n))
    if set_name == "main" and vals.get("orders_dropped", 0) != 0:
        res["orders_ok"] = False
        fail("main set must drop no order, orders_dropped=%d" % vals["orders_dropped"])
    if n == 1440:
        if "verdict" in vals and vals["verdict"] != 0:
            res["orders_ok"] = False
            fail("verdict is %d, expected 0" % vals["verdict"])
        if "alive_end" in vals and vals["alive_end"] >= 600:
            res["orders_ok"] = False
            fail("alive_end %d, expected < 600" % vals["alive_end"])
    return res


def is_native(name):
    """A leg that loads the NativeAOT DLL: every leg except the managed ones (cli*, godot*)."""
    return not name.startswith(("cli", "godot"))


def final_checks(legs, dll_path):
    """The --final provenance checks over the supplied legs (not the meta leg)."""
    out = {"commits": [], "dirty_legs": [], "dll_hashes": [], "ue_hashes": [], "dll_file_sha256": None, "messages": [], "ok": True}

    def fail(msg):
        out["ok"] = False
        out["messages"].append(msg)

    commits, dll, ue = set(), set(), set()
    for r in legs:
        f = r.get("fields", {})
        c = f.get("commit")
        if c is None:
            fail("%s: no commit header" % r["name"])
        else:
            commits.add(c)
        if f.get("dirty", "?") != "0":
            out["dirty_legs"].append(r["name"])
        if "dll_sha256" in f:
            dll.add(f["dll_sha256"].lower())
        elif is_native(r["name"]):
            fail("%s: no dll_sha256 header (a native leg carries the hash of the DLL it loaded)" % r["name"])
        if r["name"].startswith("unreal"):
            if "ue_module_sha256" not in f:
                fail("%s: no ue_module_sha256 header" % r["name"])
            else:
                ue.add(f["ue_module_sha256"].lower())
    out["commits"], out["dll_hashes"], out["ue_hashes"] = sorted(commits), sorted(dll), sorted(ue)
    if len(commits) != 1:
        fail("expected exactly one commit, found %d: %s" % (len(commits), ", ".join(sorted(commits))))
    if out["dirty_legs"]:
        fail("dirty != 0 in: %s" % ", ".join(out["dirty_legs"]))
    if len(dll) != 1:
        fail("expected exactly one dll_sha256, found %d" % len(dll))
    if len(ue) != 1:
        fail("expected exactly one ue_module_sha256, found %d" % len(ue))
    if os.path.isfile(dll_path):
        out["dll_file_sha256"] = sha256_file(dll_path)
        if len(dll) == 1 and out["dll_file_sha256"] != next(iter(dll)):
            fail("dll_sha256 %s is not the published DLL %s (%s)" % (next(iter(dll)), dll_path, out["dll_file_sha256"]))
    else:
        fail("published DLL not found: %s" % dll_path)
    return out


# ── picture ────────────────────────────────────────────────────────────────────────────────────────────────────

def draw_parity(path, set_name, n, results, passed, final_info):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.colors import ListedColormap
    import numpy as np

    rows = [{"name": "meta (golden)", "per_tick": [True] * n, "label": "%d/%d" % (n, n), "ok": True,
             "detail": "reference (golden + meta + trailer)"}]
    for r in results:
        rows.append({"name": r["name"], "per_tick": r["per_tick_equal"], "ok": r["ok"],
                     "label": "%d/%d" % (r["equal_ticks"], n),
                     "detail": "H0 %d/%d  header %s  digests %s  orders %s" % (
                         r["h0_equal"], r["h0_total"], "ok" if r["header_ok"] else "FAIL",
                         "ok" if r["digests_ok"] else "FAIL", "ok" if r["orders_ok"] else "FAIL")})
    k = len(rows)
    data = np.zeros((k, n), dtype=int)  # 0 grey (missing), 1 green (equal), 2 red (differs)
    for i, row in enumerate(rows):
        for t, v in enumerate(row["per_tick"]):
            data[i, t] = 0 if v is None else (1 if v else 2)
    green, red, grey = "#2a9d57", "#d62839", "#b8b8b8"
    cmap = ListedColormap([grey, green, red])
    fig_h = 2.4 + 0.75 * k
    fig, ax = plt.subplots(figsize=(14, fig_h), dpi=100)
    ax.imshow(data, aspect="auto", cmap=cmap, vmin=0, vmax=2, interpolation="nearest", extent=(0.5, n + 0.5, k - 0.5, -0.5))
    # A divergence can last one tick, which the downsampled image would hide: mark each leg's first divergent tick
    # with a thick red bar across its row and a label.
    for i, r in enumerate(results, 1):
        fd = r.get("first_divergence")
        if fd:
            t = fd["tick"]
            left = t < n * 0.75
            ax.plot([t, t], [i - 0.45, i + 0.45], color=red, linewidth=5, solid_capstyle="butt")
            ax.text(t + n * 0.01 if left else t - n * 0.01, i - 0.25, "first divergence t=%d" % t,
                    ha="left" if left else "right", va="center", fontsize=12, fontweight="bold", color="white",
                    bbox=dict(boxstyle="round,pad=0.2", facecolor=red, edgecolor="none"))
    ax.set_yticks(range(k))
    ax.set_yticklabels([r["name"] for r in rows], fontsize=15)
    xt = [t for t in [1, 300, 900, 1440] if t <= n]
    if n not in xt:
        xt.append(n)
    ax.set_xticks(sorted(set(xt)))
    ax.tick_params(axis="x", labelsize=14)
    ax.set_xlabel("sim tick (green = checksum equals the golden)", fontsize=14)
    for i, row in enumerate(rows):
        ax.text(n * 1.01, i - 0.12, row["label"], va="center", ha="left", fontsize=15, fontweight="bold",
                color=green if row["ok"] else red, clip_on=False)
        ax.text(n * 1.01, i + 0.22, row["detail"], va="center", ha="left", fontsize=11, color="#444444", clip_on=False)
    for s in ("top", "right", "left", "bottom"):
        ax.spines[s].set_visible(False)
    verdict = "PASS" if passed else "FAIL"
    title = "Trial parity, set %s: %s" % (set_name, verdict)
    if final_info is not None:
        title += "   (commits=%d dirty=%d dll=%d ue_build=%d)" % (
            len(final_info["commits"]), len(final_info["dirty_legs"]), len(final_info["dll_hashes"]), len(final_info["ue_hashes"]))
    fig.suptitle(title, fontsize=19, fontweight="bold", color=green if passed else red, x=0.02, ha="left")
    fig.subplots_adjust(left=0.13, right=0.70, top=0.84, bottom=0.2 if k < 4 else 0.14)
    fig.savefig(path, facecolor="white")
    plt.close(fig)


# ── main ───────────────────────────────────────────────────────────────────────────────────────────────────────

def load_reference(golden_dir, base, orders_path):
    """Load and cross-check the committed reference (golden, meta, trailer, order csv); raises ValueError on any
    inconsistency."""
    golden_path = os.path.join(golden_dir, base + ".golden.txt")
    meta_path = os.path.join(golden_dir, base + ".meta.txt")
    trailer_path = os.path.join(golden_dir, base + ".trailer.txt")
    algo, golden = parse_golden(golden_path)
    meta = parse_meta(meta_path)
    trailer = parse_meta(trailer_path)
    rows = orders_rows(orders_path)
    if algo is not None:
        meta["algo"] = algo if "algo" not in meta else meta["algo"]
        if norm(meta["algo"]) != norm(algo):
            raise ValueError("meta algo %s != golden checksum_algo_version %s" % (meta["algo"], algo))
    n = len(golden)
    if n == 0 or [t for t, _ in golden] != list(range(1, n + 1)):
        raise ValueError("the golden is not ticks 1..N (N=%d)" % n)
    missing = [k for k in H0_KEYS + ["algo", "scenario_sha256", "orders_sha256", "seed", "ai", "units_at_start"] if k not in meta]
    if missing:
        raise ValueError("meta is missing %s" % ", ".join(missing))
    t_missing = ["%s.%d" % (p, t) for t in DIGEST_TICKS if t <= n for p in ("digest", "wide")
                 if "%s.%d" % (p, t) not in trailer]
    t_missing += [k for k in ["orders_digest"] + COUNT_KEYS if k not in trailer]
    if t_missing:
        raise ValueError("trailer %s is missing %s" % (trailer_path, ", ".join(t_missing)))
    bad = [k for k in COUNT_KEYS if not INT_RE.match(trailer[k])]
    if bad:
        raise ValueError("trailer %s has non-integer %s" % (trailer_path, ", ".join(bad)))
    if norm(trailer["orders_digest"]) != orders_digest(rows):
        raise ValueError("trailer orders_digest %s is not the csv's 0x%016X (%s)" % (
            trailer["orders_digest"], orders_digest(rows), orders_path))
    return {"golden_path": golden_path, "meta_path": meta_path, "trailer_path": trailer_path, "golden": golden,
            "meta": meta, "trailer": trailer, "rows": rows, "n": n}


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--set", required=True, choices=sorted(SETS), help="which golden + meta + trailer to compare against")
    ap.add_argument("--final", action="store_true", help="also require one commit, dirty=0, one dll, one ue build")
    ap.add_argument("--out", default=".", help="directory for compare.json and parity.png (default: cwd)")
    ap.add_argument("--dll", default=DEFAULT_DLL, help="the published ChimeraSim.dll (--final)")
    ap.add_argument("--golden-dir", default=DEFAULT_GOLDEN_DIR)
    ap.add_argument("--orders", default=DEFAULT_ORDERS, help="the frozen order csv")
    ap.add_argument("--no-png", action="store_true", help="skip parity.png")
    ap.add_argument("legs", nargs="+", metavar="name=path")
    a = ap.parse_args(argv)

    legs = []
    for item in a.legs:
        name, sep, path = item.partition("=")
        if not sep or not name or not path:
            ap.error("leg '%s' is not name=path" % item)
        legs.append((name, path))
    names = [n for n, _ in legs]
    if len(set(names)) != len(names) or "meta" in names:
        ap.error("leg names must be unique and not 'meta'")

    try:
        ref = load_reference(a.golden_dir, SETS[a.set], a.orders)
    except (OSError, ValueError) as e:
        print("error: %s" % e, file=sys.stderr)
        return 2
    n = ref["n"]

    results = [check_leg(name, path, ref["golden"], ref["meta"], ref["trailer"], n, ref["rows"], a.set)
               for name, path in legs]

    final_info = final_checks(results, a.dll) if a.final else None
    passed = all(r["ok"] for r in results) and (final_info is None or final_info["ok"])

    print("set=%s golden=%s meta=%s trailer=%s" % (a.set, os.path.basename(ref["golden_path"]),
                                                  os.path.basename(ref["meta_path"]), os.path.basename(ref["trailer_path"])))
    print("  %-14s %d/%d (reference: golden + meta + trailer)" % ("meta", n, n))
    for r in results:
        print("  %-14s %d/%d H0=%d/%d header=%s digests=%s orders=%s%s" % (
            r["name"], r["equal_ticks"], n, r["h0_equal"], r["h0_total"], "ok" if r["header_ok"] else "FAIL",
            "ok" if r["digests_ok"] else "FAIL", "ok" if r["orders_ok"] else "FAIL", "" if r["ok"] else "  <-- FAIL"))
        for m in r["messages"]:
            print("      - " + m)
    if final_info is not None:
        for m in final_info["messages"]:
            print("  final: " + m)
    all_equal = "yes" if all(r["equal_ticks"] == n and r["ok"] for r in results) else "no"
    summary = "RESULT set=%s legs=%d ticks=%d all_equal=%s" % (a.set, len(results) + 1, n, all_equal)
    if final_info is not None:
        summary += " commits=%d dirty=%d dll=%d ue_build=%d" % (
            len(final_info["commits"]), len(final_info["dirty_legs"]), len(final_info["dll_hashes"]), len(final_info["ue_hashes"]))
    summary += " " + ("PASS" if passed else "FAIL")
    print(summary)

    os.makedirs(a.out, exist_ok=True)
    doc = {"set": a.set, "ticks": n, "passed": passed, "final": final_info, "summary": summary,
           "golden": ref["golden_path"], "meta": ref["meta_path"], "trailer": ref["trailer_path"], "orders": a.orders,
           "reference": "committed golden + meta + trailer; every leg is compared with them, never with another leg",
           "legs": [{k: v for k, v in r.items() if k not in ("per_tick_equal", "fields")} for r in results]}
    with open(os.path.join(a.out, "compare.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(doc, f, indent=1)
        f.write("\n")
    if not a.no_png:
        draw_parity(os.path.join(a.out, "parity.png"), a.set, n, results, passed, final_info)
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
