#!/usr/bin/env python3
"""ctypes smoke test of ChimeraSim.dll's C ABI (Unreal trial A5; the C++ harness of A7 is the full leg).

Loads the published DLL by full path and checks, in one process:
  identity   abi_version == 0x00010000, abi_check(56, 28) == 0 and (1, 1) == -7, build_info fields (aot=1, algo, runtime)
  hashes     the six pre-tick values equal the committed trial-1000 meta (H0 + tick0)
  replay     trial_1000 for 1,440 ticks (frozen orders CSV, checksum interval 1): every tick equals the committed golden,
             units/wide digests at 0/300/900/1440, orders applied/dropped, alive_end and verdict equal the trailer
  rows       read_units count == HighWaterMark rows of 56 bytes, a Python recompute of the units digest from the rows
             equals chimera_units_digest, def ids resolve, read_buildings rows
  errors     bad session -> -2, short buffer -> -5 with the size needed, bad scenario -> -3 / -4 with a message,
             selftest 1-3 -> 0, file_sha256 == hashlib, destroyed id -> -2
Usage: native_abi_smoke.py [--dll PATH] [--ai] [--ticks N]
Exit 0 iff every check passes. Prints one line per check and a final `native_abi_smoke: PASS n/n` line.
"""
import argparse
import ctypes as C
import hashlib
import os
import re
import struct
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
GODOT = os.path.join(ROOT, "godot")
TESTS = os.path.join(GODOT, "ProjectChimera.Sim.Tests")
DEFAULT_DLL = os.path.join(GODOT, "ProjectChimera.Sim.Native", "bin", "publish", "ChimeraSim.dll")
SEED = 0xC0FFEE1234567890


class Unit(C.Structure):
    _fields_ = [("id", C.c_int32), ("ref", C.c_int32), ("pos", C.c_int32 * 3), ("prev", C.c_int32 * 3),
                ("vel", C.c_int32 * 3), ("hp_raw", C.c_int32), ("max_hp_raw", C.c_int32),
                ("faction", C.c_uint8), ("mesh_type", C.c_uint8), ("flags", C.c_uint8), ("command", C.c_uint8)]


class Building(C.Structure):
    _fields_ = [("slot", C.c_int32), ("pos", C.c_int32 * 3), ("hp_raw", C.c_int32), ("max_hp_raw", C.c_int32),
                ("faction", C.c_uint8), ("type", C.c_uint8), ("alive", C.c_uint8), ("pad", C.c_uint8)]


P32, PU32, PU64 = C.POINTER(C.c_int32), C.POINTER(C.c_uint32), C.POINTER(C.c_uint64)


def load(path):
    d = C.CDLL(path)
    sig = {
        "chimera_abi_version": (C.c_int32, []),
        "chimera_abi_check": (C.c_int32, [C.c_int32, C.c_int32]),
        "chimera_build_info": (C.c_int32, [C.c_char_p, C.c_int32, P32]),
        "chimera_session_create": (C.c_int32, [C.c_char_p, C.c_char_p, C.c_uint64, C.c_uint32, P32]),
        "chimera_session_destroy": (C.c_int32, [C.c_int32]),
        "chimera_set_checksum_interval": (C.c_int32, [C.c_int32, C.c_int32]),
        "chimera_last_checksum": (C.c_int32, [C.c_int32, PU32, PU32]),
        "chimera_pre_tick_hashes": (C.c_int32, [C.c_int32, PU64]),
        "chimera_submit_order": (C.c_int32, [C.c_int32] * 7),
        "chimera_step": (C.c_int32, [C.c_int32]),
        "chimera_read_units": (C.c_int32, [C.c_int32, C.POINTER(Unit), C.c_int32, P32]),
        "chimera_read_buildings": (C.c_int32, [C.c_int32, C.POINTER(Building), C.c_int32, P32]),
        "chimera_units_digest": (C.c_int32, [C.c_int32, PU64]),
        "chimera_wide_digest": (C.c_int32, [C.c_int32, PU64]),
        "chimera_unit_def_id": (C.c_int32, [C.c_int32, C.c_int32, C.c_char_p, C.c_int32, P32]),
        "chimera_building_def_id": (C.c_int32, [C.c_int32, C.c_int32, C.c_char_p, C.c_int32, P32]),
        "chimera_verdict": (C.c_int32, [C.c_int32, P32]),
        "chimera_stats": (C.c_int32, [C.c_int32, C.POINTER(C.c_int64)]),
        "chimera_file_sha256": (C.c_int32, [C.c_char_p, C.c_char_p, C.c_int32, P32]),
        "chimera_selftest": (C.c_int32, [C.c_int32]),
        "chimera_last_error": (C.c_int32, [C.c_int32, C.c_char_p, C.c_int32, P32]),
    }
    for name, (res, args) in sig.items():
        f = getattr(d, name)
        f.restype, f.argtypes = res, args
    return d


def text(d, fn, *args):
    """Call a string-out export `fn(*args, buf, cap, len)`; returns (rc, string)."""
    buf, n = C.create_string_buffer(4096), C.c_int32(0)
    rc = fn(*args, buf, 4096, C.byref(n))
    return rc, buf.raw[:n.value].decode("utf-8")


def parse_kv(path):
    kv = {}
    for line in open(path, encoding="utf-8"):
        if line.startswith("#") or ":" not in line:
            continue
        k, v = line.split(":", 1)
        kv[k.strip()] = v.strip()
    return kv


def golden(path):
    out = {}
    for line in open(path, encoding="utf-8"):
        if line[:1].isdigit():
            t, h = line.split()
            out[int(t)] = int(h, 16)
    return out


def fnv_units(units):
    """Recompute WorldDigest.UnitsDigest from the rows a host holds (FNV-1a 64 over little-endian int32s)."""
    h = 14695981039346656037
    for u in units:
        vals = [u.id, u.ref, *u.pos, *u.prev, *u.vel, u.hp_raw, u.max_hp_raw, u.faction, u.mesh_type, u.flags, u.command]
        for v in vals:
            for b in struct.pack("<I", v & 0xFFFFFFFF):
                h = ((h ^ b) * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    return h


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dll", default=DEFAULT_DLL)
    ap.add_argument("--ai", action="store_true")
    ap.add_argument("--ticks", type=int, default=1440)
    a = ap.parse_args()
    results = []

    def check(name, ok, detail=""):
        results.append(bool(ok))
        print(("PASS " if ok else "FAIL ") + name + (" " + detail if detail else ""))

    dll = os.path.abspath(a.dll)
    d = load(dll)
    check("abi_version", d.chimera_abi_version() == 0x00010000)
    check("abi_check 56/28", d.chimera_abi_check(56, 28) == 0)
    check("abi_check mismatch -> -7", d.chimera_abi_check(1, 1) == -7)
    rc, info = text(d, d.chimera_build_info)
    print("build_info:", info)
    check("build_info", rc == 0 and re.match(r"^abi=1\.0;algo=\d+;commit=\S+;dirty=[01];runtime=.+;aot=1$", info) is not None)
    check("sizeof ChimeraUnit/Building", C.sizeof(Unit) == 56 and C.sizeof(Building) == 28)

    tag = "-ai" if a.ai else ""
    scen = os.path.join(TESTS, "Trial", "trial_1000.json")
    orders_path = os.path.join(TESTS, "Trial", "trial_1000.orders.csv")
    gold = golden(os.path.join(TESTS, "Golden", f"trial-1000{tag}.golden.txt"))
    meta = parse_kv(os.path.join(TESTS, "Golden", f"trial-1000{tag}.meta.txt"))
    trailer = parse_kv(os.path.join(TESTS, "Golden", f"trial-1000{tag}.trailer.txt"))

    sid = C.c_int32(0)
    rc = d.chimera_session_create(GODOT.encode(), scen.encode(), SEED, 1 if a.ai else 0, C.byref(sid))
    check("session_create", rc == 0 and sid.value > 0, f"rc={rc} id={sid.value}")
    s = sid.value
    pre = (C.c_uint64 * 6)()
    check("pre_tick_hashes rc", d.chimera_pre_tick_hashes(s, pre) == 0)
    keys = ["hash.start_state", "hash.canonical_model", "hash.content", "hash.ruleset", "hash.agreement", "hash.tick0"]
    ok = all(pre[i] == int(meta[k], 16) for i, k in enumerate(keys))
    check("H0 + tick0 == committed meta", ok, " ".join(f"{pre[i]:#x}" for i in range(6)))

    rows = {}
    for line in open(orders_path, encoding="utf-8").read().splitlines()[1:]:
        if line.strip():
            r = [int(x) for x in line.split(",")]
            rows.setdefault(r[0], []).append(r)
    check("set_checksum_interval", d.chimera_set_checksum_interval(s, 1) == 0)

    applied = dropped = bad = 0
    first_diff = None
    digests = {}
    u64 = C.c_uint64()

    def digest_at(t):
        d.chimera_units_digest(s, C.byref(u64)); ud = u64.value
        d.chimera_wide_digest(s, C.byref(u64)); digests[t] = (ud, u64.value)

    digest_at(0)
    tick, hsh = C.c_uint32(), C.c_uint32()
    for t in range(a.ticks):
        for r in rows.get(t, []):
            rc = d.chimera_submit_order(s, r[1], r[2], r[3], r[4], r[5], r[6])
            if rc == 0: applied += 1
            elif rc == 1: dropped += 1
            else: bad += 1
        if d.chimera_step(s) != 0:
            check("step", False, f"tick {t + 1}")
            break
        d.chimera_last_checksum(s, C.byref(tick), C.byref(hsh))
        if first_diff is None and (tick.value != t + 1 or hsh.value != gold[t + 1]):
            first_diff = (t + 1, tick.value, hsh.value, gold[t + 1])
        if t + 1 in (300, 900, 1440):
            digest_at(t + 1)
    check(f"replay {a.ticks} ticks equal golden", first_diff is None, "" if first_diff is None else f"first divergence {first_diff}")
    check("submit_order no errors", bad == 0, f"applied={applied} dropped={dropped} errors={bad}")
    if a.ticks == 1440:
        for t in (0, 300, 900, 1440):
            check(f"digest.{t}/wide.{t} == trailer", digests[t] == (int(trailer[f"digest.{t}"], 16), int(trailer[f"wide.{t}"], 16)))
        check("orders applied/dropped == trailer", (applied, dropped) == (int(trailer["orders_applied"]), int(trailer["orders_dropped"])))
        n = C.c_int32(0)
        d.chimera_read_units(s, None, 0, C.byref(n))
        arr = (Unit * n.value)()
        alive = 0
        rc = d.chimera_read_units(s, arr, n.value, C.byref(n))
        alive = sum(1 for u in arr if u.flags & 1)
        check("alive_end == trailer", rc == 0 and alive == int(trailer["alive_end"]), f"alive={alive} rows={n.value}")
        d.chimera_units_digest(s, C.byref(u64))
        check("python recompute of units digest from rows", fnv_units(arr) == u64.value == int(trailer["digest.1440"], 16))
        v = C.c_int32(-1)
        check("verdict == trailer", d.chimera_verdict(s, C.byref(v)) == 0 and v.value == int(trailer["verdict"]), f"verdict={v.value}")
    # read_units short buffer -> -5 with the size needed
    n = C.c_int32(0)
    one = (Unit * 1)()
    rc = d.chimera_read_units(s, one, 1, C.byref(n))
    check("read_units short buffer -> -5 + count", rc == -5 and n.value > 1, f"rc={rc} count={n.value}")
    # def ids
    rc, did = text(d, d.chimera_unit_def_id, s, 0)
    check("unit_def_id", rc == 0 and len(did) > 0, f"id0={did!r}")
    rc, _ = text(d, d.chimera_unit_def_id, s, -1)
    check("unit_def_id out of range -> -1", rc == -1)
    # the size query (NULL, 0) leaves the session's error slot alone
    _, err_before = text(d, d.chimera_last_error, s)
    n = C.c_int32(0)
    rc = d.chimera_read_units(s, None, 0, C.byref(n))
    _, err_after = text(d, d.chimera_last_error, s)
    check("size query -> -5 + count, last_error untouched", rc == -5 and n.value > 1 and err_after == err_before and err_before != "",
          f"rc={rc} count={n.value} err={err_after!r}")
    # faction ordinals: 0 Neutral, 1 alpha, 2 beta (the header's rule); submit_order rejects >= 9
    allu = (Unit * n.value)()
    d.chimera_read_units(s, allu, n.value, C.byref(n))
    facs = sorted({u.faction for u in allu if u.flags & 1})
    check("live unit factions within 1..2 (alpha=1, beta=2)", set(facs) <= {1, 2} and len(facs) > 0, f"factions={facs}")
    check("submit_order faction 9 -> -1", d.chimera_submit_order(s, 9, 0, 0, 0, 0, 0) == -1)
    nb = C.c_int32(0)
    d.chimera_read_buildings(s, None, 0, C.byref(nb))
    barr = (Building * max(nb.value, 1))()
    rc = d.chimera_read_buildings(s, barr, nb.value, C.byref(nb))
    rc2, bdef = text(d, d.chimera_building_def_id, s, 0)
    check("read_buildings + building_def_id", rc == 0 and nb.value >= 2 and rc2 == 0 and bdef != "",
          f"count={nb.value} def0={bdef!r} hp0={barr[0].hp_raw} alive0={barr[0].alive}")
    st = (C.c_int64 * 8)()
    check("stats", d.chimera_stats(s, st) == 0 and st[5] == a.ticks and st[0] > 0, " ".join(str(st[i]) for i in range(8)))
    check("stats process-wide (session 0)", d.chimera_stats(0, st) == 0 and st[5] == 0)

    # errors and selftests
    check("bad session -> -2", d.chimera_step(999) == -2)
    rc, msg = text(d, d.chimera_last_error, 0)
    check("last_error(0) names the bad session", rc == 0 and "999" in msg, repr(msg))
    for k in (1, 2, 3):
        check(f"selftest {k}", d.chimera_selftest(k) == 0)
    check("selftest 9 -> -1", d.chimera_selftest(9) == -1)
    tmp = C.c_int32(0)
    rc = d.chimera_session_create(GODOT.encode(), os.path.join(tempfile.gettempdir(), "no_such_scenario.json").encode(), SEED, 0, C.byref(tmp))
    rc_msg, msg = text(d, d.chimera_last_error, 0)
    check("missing scenario -> -3 + message", rc == -3 and tmp.value == 0 and len(msg) > 0, f"rc={rc} msg={msg[:90]!r}")
    bad_json = os.path.join(tempfile.gettempdir(), "chimera_bad_scenario.json")
    open(bad_json, "w").write('{"definitely": "not a scenario"')
    rc = d.chimera_session_create(GODOT.encode(), bad_json.encode(), SEED, 0, C.byref(tmp))
    rc_msg, msg = text(d, d.chimera_last_error, 0)
    check("unparseable scenario -> -3", rc == -3, f"rc={rc} msg={msg[:90]!r}")
    import json
    rejected = os.path.join(tempfile.gettempdir(), "chimera_rejected_scenario.json")
    doc = json.load(open(scen, encoding="utf-8"))
    doc["units"][0]["unit_id"] = "no_such_unit_definition"
    json.dump(doc, open(rejected, "w", encoding="utf-8"))
    rc = d.chimera_session_create(GODOT.encode(), rejected.encode(), SEED, 0, C.byref(tmp))
    rc_msg, msg = text(d, d.chimera_last_error, 0)
    check("validator-rejected scenario -> -4 + message", rc == -4 and tmp.value == 0 and "no_such_unit_definition" in msg, f"rc={rc} msg={msg[:140]!r}")
    check("create with unknown flag bit -> -1", d.chimera_session_create(GODOT.encode(), scen.encode(), SEED, 2, C.byref(tmp)) == -1)
    for p in (bad_json, rejected):
        try: os.remove(p)
        except OSError: pass
    want = hashlib.sha256(open(orders_path, "rb").read()).hexdigest()
    rc, got = text(d, d.chimera_file_sha256, orders_path.encode())
    check("file_sha256 == hashlib", rc == 0 and got == want)
    rc, got = text(d, d.chimera_file_sha256, dll.encode())
    check("file_sha256 of the DLL itself", rc == 0 and got == hashlib.sha256(open(dll, "rb").read()).hexdigest())
    n = C.c_int32(0)
    rc = d.chimera_file_sha256(orders_path.encode(), C.create_string_buffer(8), 8, C.byref(n))
    check("file_sha256 short buffer -> -5 + length 64", rc == -5 and n.value == 64)
    rc = d.chimera_file_sha256(b"Z:/definitely/not/here.bin", C.create_string_buffer(80), 80, C.byref(n))
    check("file_sha256 missing file -> -3", rc == -3)
    check("session_destroy", d.chimera_session_destroy(s) == 0)
    check("destroyed id -> -2", d.chimera_step(s) == -2 and d.chimera_session_destroy(s) == -2)

    passed = sum(results)
    print(f"native_abi_smoke: {'PASS' if passed == len(results) else 'FAIL'} {passed}/{len(results)}")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
