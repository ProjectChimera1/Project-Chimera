"""Unreal trial A14 (plan A section 4 A14): compare Godot's own MainScene [Checksum] lines with the trial golden.

Measured and reported, not gating. Reads the Godot log of a full-MainScene .chmr playback
(`[Checksum] tick=N hash=0x........`, MainScene's checksum sink), the watcher's summary file
(`# key: value`, godot/src/Trial/MainSceneSimTrialReplay.cs) and the golden + meta for the variant, and prints:

  samples=K/N  first_divergent_window=(a,b]  (or none)  first_divergent_tick=T (interval-1 runs)
  start-state rows (the replay path's pre-tick hashes vs the meta; reported)

Writes a JSON summary with --json. Exit 0 = every sample equals the golden and the replay covered tick 1440,
1 = a divergence or a short replay (a finding), 2 = unusable input.

  python tools/sim-trial/compare_mainscene_replay.py --log L --summary S --set ai|main [--json OUT] [--ticks 1440]
"""
import argparse
import json
import os
import re
import sys

R = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
GOLDEN_DIR = os.path.join(R, "godot", "ProjectChimera.Sim.Tests", "Golden")
CHECKSUM_RE = re.compile(r"\[Checksum\] tick=(\d+) hash=0x([0-9A-Fa-f]{8})")
KV_RE = re.compile(r"^#?\s*([A-Za-z0-9_.]+):\s*(.*?)\s*$")
START_KEYS = ["hash.start_state", "hash.canonical_model", "hash.content", "hash.ruleset", "hash.agreement", "hash.tick0"]


def read_kv(path):
    out = {}
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = KV_RE.match(line.rstrip("\n"))
            if m:
                out[m.group(1)] = m.group(2)
    return out


def read_golden(path):
    g = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or not line.strip():
                continue
            t, h = line.split()
            g[int(t)] = h.upper()
    return g


def same_hex(a, b):
    try:
        return a is not None and b is not None and int(a, 16) == int(b, 16)
    except ValueError:
        return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", required=True)
    ap.add_argument("--summary", required=True)
    ap.add_argument("--set", choices=["ai", "main"], required=True)
    ap.add_argument("--ticks", type=int, default=1440)
    ap.add_argument("--json")
    ap.add_argument("--managed", help="ChmrTool verify trace (state lines of the managed control), optional")
    a = ap.parse_args()

    suffix = "-ai" if a.set == "ai" else ""
    golden = read_golden(os.path.join(GOLDEN_DIR, f"trial-1000{suffix}.golden.txt"))
    meta = read_kv(os.path.join(GOLDEN_DIR, f"trial-1000{suffix}.meta.txt"))
    trailer = read_kv(os.path.join(GOLDEN_DIR, f"trial-1000{suffix}.trailer.txt"))
    if not os.path.exists(a.log):
        print(f"missing log {a.log}")
        return 2
    summary = read_kv(a.summary) if os.path.exists(a.summary) else {}

    samples = {}
    dup_conflicts = 0
    with open(a.log, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = CHECKSUM_RE.search(line)
            if m:
                t, h = int(m.group(1)), m.group(2).upper()
                if t in samples and samples[t] != h:
                    dup_conflicts += 1
                samples.setdefault(t, h)
    in_range = sorted(t for t in samples if 1 <= t <= a.ticks)
    if not in_range:
        print("no [Checksum] lines in range; the replay did not step")
        return 2
    ticks = sorted(samples)
    interval = min((b - c for b, c in zip(ticks[1:], ticks[:-1])), default=0) or ticks[0]
    expected = list(range(interval, a.ticks + 1, interval))
    equal = [t for t in expected if samples.get(t) == golden.get(t)]
    missing = [t for t in expected if t not in samples]
    diverged = [t for t in expected if t in samples and samples[t] != golden.get(t)]
    first = diverged[0] if diverged else None
    window = f"({first - interval},{first}]" if first is not None else "none"

    print(f"set={a.set} interval={interval} samples={len(equal)}/{len(expected)} missing={len(missing)} "
          f"diverged={len(diverged)} dup_conflicts={dup_conflicts}")
    if first is not None:
        print(f"first_divergent_window={window} first_divergent_sample={first} "
              f"game=0x{samples[first]} golden=0x{golden.get(first)}")
        last_equal = max((t for t in equal if t < first), default=0)
        print(f"last_equal_sample_before_divergence={last_equal}")
        if interval == 1:
            print(f"first_divergent_tick={first}")
    else:
        print("first_divergent_window=none")
    if missing:
        print(f"missing_samples={missing[:10]}{' ...' if len(missing) > 10 else ''}")

    start_rows = []
    for k in START_KEYS:
        ok = same_hex(summary.get(k), meta.get(k))
        start_rows.append({"key": k, "replay": summary.get(k), "meta": meta.get(k), "equal": ok})
        print(f"{k:22} replay={summary.get(k)!s:20} meta={meta.get(k)!s:20} {'ok' if ok else 'DIFF'} (reported)")
    for k in ["digest.0", "wide.0"]:
        ok = same_hex(summary.get(k), trailer.get(k))
        start_rows.append({"key": k, "replay": summary.get(k), "trailer": trailer.get(k), "equal": ok})
        print(f"{k:22} replay={summary.get(k)!s:20} trailer={trailer.get(k)!s:17} {'ok' if ok else 'DIFF'} (reported)")
    # Unfolded-state check at the trailer ticks: units + wide digests (the wide one folds CommandState, which
    # SimChecksum does not, plan A F40) and the command-state census, vs the golden trailer and the managed control.
    managed = read_kv(a.managed) if a.managed and os.path.exists(a.managed) else {}
    state_rows = []
    for t in (300, 900, 1440):
        line = summary.get(f"state.{t}")
        fields = dict(kv.split("=", 1) for kv in line.split()) if line else {}
        mline = managed.get(f"state.{t}")
        mfields = dict(kv.split("=", 1) for kv in mline.split()) if mline else {}
        d_ok = same_hex(fields.get("digest"), trailer.get(f"digest.{t}"))
        w_ok = same_hex(fields.get("wide"), trailer.get(f"wide.{t}"))
        state_rows.append({"tick": t, "replay": fields, "managed": mfields, "digest_equal": d_ok, "wide_equal": w_ok})
        print(f"state.{t}: digest {'ok' if d_ok else 'DIFF'} wide {'ok' if w_ok else 'DIFF'} (vs trailer; reported)")
        print(f"  mainscene: {line}")
        if mline:
            print(f"  managed:   {mline}")
    start_match = sum(1 for r in start_rows[:6] if r["equal"])
    print(f"start_match={start_match}/6 ai_plan_mask={summary.get('ai_plan_mask')} tick_at_start={summary.get('tick_at_start')} "
          f"rng_state_at_start={summary.get('rng_state_at_start')}")
    for k in ["end_reason", "tick_at_end", "alive_end", "verdict", "frames", "multi_step_frames", "max_ticks_per_frame",
              "replay_wall_s", "fps_mean", "runtime"]:
        print(f"{k}={summary.get(k)}")
    if "alive_end" in trailer:
        print(f"golden_trailer_alive_end={trailer.get('alive_end')}")

    result = {
        "set": a.set, "interval": interval, "samples_equal": len(equal), "samples_expected": len(expected),
        "missing": missing, "diverged": diverged, "first_divergent_sample": first, "first_divergent_window": window,
        "game_hash_at_first": samples.get(first) if first else None, "golden_hash_at_first": golden.get(first) if first else None,
        "start_rows": start_rows, "start_match": start_match, "state_rows": state_rows, "summary": summary, "log": os.path.abspath(a.log),
    }
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(result, f, indent=1)
    ok = not diverged and not missing
    print("RESULT " + (f"1440/1440 ({len(equal)}/{len(expected)} samples)" if ok else f"DIVERGED first window {window}" if diverged else "SHORT replay"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
