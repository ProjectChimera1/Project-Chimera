"""Self-tests of compare_traces.py on a tiny synthetic variant (5 ticks, 3 order rows; one 1440-tick variant for the
verdict/alive_end gates): a good trace passes, and each class of defect (divergent hash, missing tick, wrong H0, wrong
or missing header, a digest or count that differs from the committed trailer even with a single leg, malformed
integers, bad orders digest, BOM/UTF-16 encodings, dirty tree, two commits, wrong or missing DLL hash) fails with the
check that owns it. Run: python -m pytest tools/sim-trial/tests -q"""
import hashlib
import os
import sys

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import compare_traces as ct  # noqa: E402

ROWS = [[0, 1, 4096, 2, 3932160, 0, 0], [0, 2, 4097, 130, -65536, 131072, 0], [3, 1, 4096, 3, 0, 0, 0]]
HASHES = [0x0E7082D2, 0x3CD75213, 0xCD1BECB8, 0x3C15D0C5, 0x06689624]
META = {
    "algo": "29", "scenario_sha256": "a" * 64, "orders_sha256": "b" * 64, "seed": "0xC0FFEE1234567890", "ai": "0",
    "hash.start_state": "0xA68FAB39663A44E2", "hash.canonical_model": "0x26C0BDB524284D74",
    "hash.content": "0x55BB83D5F0A14C4B", "hash.ruleset": "0x1EB102FD18C50DDD", "hash.agreement": "0x5D0605462E565A22",
    "hash.tick0": "0x69843E0A", "units_at_start": "1000", "initial_delay": "4", "item_registry": "2",
}
TRAILER = {"digest.0": "0x1", "wide.0": "0x2", "orders_applied": "3", "orders_dropped": "0", "alive_end": "500",
           "verdict": "0"}


def write_orders(path):
    with open(path, "w", newline="\n") as f:
        f.write("tick,faction,unit_ref,cmd,x_raw,z_raw,slot\n")
        for r in ROWS:
            f.write(",".join(str(x) for x in r) + "\n")


def write_trailer(gdir, base="trial-1000", **over):
    t = dict(TRAILER, orders_digest="0x%016X" % ct.orders_digest(ROWS))
    t.update(over)
    with open(os.path.join(str(gdir), base + ".trailer.txt"), "w", newline="\n") as f:
        f.write("# prose: not a key\n")
        for k, v in t.items():
            if v is not None:
                f.write("%s: %s\n" % (k, v))


def write_meta(gdir):
    with open(os.path.join(str(gdir), "trial-1000.meta.txt"), "w", newline="\n") as f:
        f.write("# prose: not a key\n")
        for k, v in META.items():
            f.write("%s: %s\n" % (k, v))


@pytest.fixture()
def env(tmp_path):
    g = tmp_path / "golden"
    g.mkdir()
    with open(g / "trial-1000.golden.txt", "w", newline="\n") as f:
        f.write("# Project Chimera - test golden.\n# checksum_algo_version: 29\n")
        for i, h in enumerate(HASHES, 1):
            f.write("%d %08X\n" % (i, h))
    write_meta(g)
    write_trailer(g)
    orders = tmp_path / "orders.csv"
    write_orders(orders)
    dll = tmp_path / "ChimeraSim.dll"
    dll.write_bytes(b"dll-bytes")
    return {"tmp": tmp_path, "golden": str(g), "orders": str(orders), "dll": str(dll),
            "dll_sha": hashlib.sha256(b"dll-bytes").hexdigest()}


def good_fields(env, **over):
    f = dict(META)
    for k in ("initial_delay", "item_registry"):
        f.pop(k)
    f.update({"leg": "cli", "host": "sim-trial-cli", "runtime": ".NET 8.0.25", "config": "Release",
              "commit": "c" * 40, "dirty": "0"})
    f.update(TRAILER)
    f["orders_digest"] = "0x%016X" % ct.orders_digest(ROWS)
    f.update(over)
    return f


TRAILER_PREFIXES = ("digest", "wide", "orders", "alive", "verdict")


def write_trace(env, name, fields=None, hashes=None, drop_tick=None, **over):
    f = fields or good_fields(env, **over)
    hs = hashes or HASHES
    p = env["tmp"] / (name + ".txt")
    head = ["leg", "host", "runtime", "config", "commit", "dirty", "algo", "scenario_sha256", "orders_sha256", "seed",
            "ai", "hash.start_state", "hash.canonical_model", "hash.content", "hash.ruleset", "hash.agreement",
            "hash.tick0", "units_at_start"]
    with open(p, "w", newline="\n") as fh:
        for k in head + [k for k in f if k not in head and not k.startswith(TRAILER_PREFIXES)]:
            if k in f:
                fh.write("# %s: %s\n" % (k, f[k]))
        for i, h in enumerate(hs, 1):
            if i != drop_tick:
                fh.write("%d %08x\n" % (i, h))  # lower case on purpose
        for k in [k for k in f if k.startswith(TRAILER_PREFIXES)]:
            fh.write("# %s: %s\n" % (k, f[k]))
    return str(p)


def run(env, legs, *extra, final=False):
    args = ["--set", "main", "--golden-dir", env["golden"], "--orders", env["orders"], "--out", str(env["tmp"] / "out"),
            "--no-png", "--dll", env["dll"]] + list(extra)
    if final:
        args.append("--final")
    return ct.main(args + ["%s=%s" % kv for kv in legs])


def leg_line(out, name):
    return [ln for ln in out.splitlines() if ln.strip().startswith(name + " ")][0]


def test_good_leg_passes(env, capsys):
    p = write_trace(env, "cli")
    assert run(env, [("cli", p)]) == 0
    out = capsys.readouterr().out
    assert "RESULT set=main legs=2 ticks=5 all_equal=yes PASS" in out


def test_divergent_tick_is_named(env, capsys):
    hs = list(HASHES)
    hs[2] ^= 1
    p = write_trace(env, "cli", hashes=hs)
    assert run(env, [("cli", p)]) == 1
    assert "first divergent tick 3" in capsys.readouterr().out


def test_missing_tick_fails(env, capsys):
    p = write_trace(env, "cli", drop_tick=4)
    assert run(env, [("cli", p)]) == 1
    assert "tick set is not exactly" in capsys.readouterr().out


@pytest.mark.parametrize("key,val,needle", [
    ("hash.agreement", "0x1", "hash.agreement"),
    ("hash.tick0", "0x1", "hash.tick0"),
    ("seed", "0x1", "header seed"),
    ("ai", "1", "header ai"),
    ("scenario_sha256", "d" * 64, "header scenario_sha256"),
    ("digest.0", None, "missing digest.0"),
    ("orders_digest", "0x0000000000000001", "orders_digest"),
    ("orders_applied", "2", "orders_applied"),
    ("orders_dropped", "1", "must drop no order"),
])
def test_field_defects_fail(env, capsys, key, val, needle):
    f = good_fields(env)
    if val is None:
        del f[key]
    else:
        f[key] = val
    p = write_trace(env, "cli", fields=f)
    assert run(env, [("cli", p)]) == 1
    assert needle in capsys.readouterr().out


def test_hex_case_and_value_normalised(env):
    f = good_fields(env)
    f["hash.start_state"] = "0xa68fab39663a44e2"  # lower case still equal
    f["seed"] = "0xc0ffee1234567890"
    assert run(env, [("cli", write_trace(env, "cli", fields=f))]) == 0


def test_digest_must_equal_trailer_with_a_single_leg(env, capsys):
    # One leg alone (the shape of A7's cpp=, A8's godot=, A10's unreal_ai=) is still compared with the committed trailer.
    b = write_trace(env, "unreal_ai", fields=good_fields(env, **{"wide.0": "0x9"}))
    assert run(env, [("unreal_ai", b)]) == 1
    out = capsys.readouterr().out
    assert "wide.0: leg 0x9, trailer 0x2" in out and "digests=FAIL" in leg_line(out, "unreal_ai")


def test_wrong_leg_is_blamed_whatever_the_order(env, capsys):
    bad = write_trace(env, "unreal_ai", fields=good_fields(env, **{"digest.0": "0x9"}))
    good = write_trace(env, "cli_ai")
    assert run(env, [("unreal_ai", bad), ("cli_ai", good)]) == 1
    out = capsys.readouterr().out
    assert "<-- FAIL" in leg_line(out, "unreal_ai")
    assert "<-- FAIL" not in leg_line(out, "cli_ai")


@pytest.mark.parametrize("key,val,needle", [
    ("orders_dropped", "", "orders_dropped is not an integer"),
    ("orders_applied", "n/a", "orders_applied is not an integer"),
    ("alive_end", "-", "alive_end is not an integer"),
    ("verdict", "n/a", "verdict is not an integer"),
    ("alive_end", "499", "alive_end: leg 499, trailer 500"),
    ("verdict", "1", "verdict: leg 1, trailer 0"),
])
def test_count_fields_malformed_or_different_fail(env, capsys, key, val, needle):
    p = write_trace(env, "cpp", fields=good_fields(env, **{key: val}))
    assert run(env, [("cpp", p)]) == 1
    out = capsys.readouterr().out
    assert needle in out and "orders=FAIL" in leg_line(out, "cpp")


@pytest.mark.parametrize("key", ["leg", "host", "runtime", "config", "commit", "dirty"])
def test_missing_per_leg_header_fails(env, capsys, key):
    f = good_fields(env)
    del f[key]
    assert run(env, [("cli", write_trace(env, "cli", fields=f))]) == 1
    assert "header is missing %s" % key in capsys.readouterr().out


def test_missing_or_inconsistent_trailer_is_a_setup_error(env, capsys):
    p = write_trace(env, "cli")
    os.remove(os.path.join(env["golden"], "trial-1000.trailer.txt"))
    assert run(env, [("cli", p)]) == 2
    write_trailer(env["golden"], **{"wide.0": None})
    assert run(env, [("cli", p)]) == 2
    assert "is missing wide.0" in capsys.readouterr().err
    write_trailer(env["golden"], verdict="x")
    assert run(env, [("cli", p)]) == 2
    write_trailer(env["golden"], orders_digest="0x0000000000000001")
    assert run(env, [("cli", p)]) == 2
    assert "is not the csv's" in capsys.readouterr().err


def test_bom_is_accepted_and_utf16_fails_cleanly(env, capsys):
    p = write_trace(env, "cli")
    with open(p, encoding="utf-8") as f:
        text = f.read()
    with open(p, "w", encoding="utf-8-sig", newline="\n") as f:
        f.write(text)
    assert run(env, [("cli", p)]) == 0
    with open(p, "w", encoding="utf-16", newline="\n") as f:
        f.write(text)
    assert run(env, [("cli", p)]) == 1
    assert "not UTF-8" in capsys.readouterr().out


def test_1440_tick_gates(tmp_path, capsys):
    # verdict == 0 and alive_end < 600 apply at N = 1440, even when the trailer itself carries the bad value.
    g = tmp_path / "golden"
    g.mkdir()
    hs = [0x1000 + i for i in range(1440)]
    with open(g / "trial-1000.golden.txt", "w", newline="\n") as f:
        f.write("# checksum_algo_version: 29\n")
        for i, h in enumerate(hs, 1):
            f.write("%d %08X\n" % (i, h))
    write_meta(g)
    digests = {"%s.%d" % (p, t): "0x%X" % (t + 7) for t in (0, 300, 900, 1440) for p in ("digest", "wide")}
    orders = tmp_path / "orders.csv"
    write_orders(orders)
    envd = {"tmp": tmp_path, "golden": str(g), "orders": str(orders), "dll": str(tmp_path / "none.dll")}

    def case(alive, verdict):
        write_trailer(g, alive_end=str(alive), verdict=str(verdict), **digests)
        f = good_fields(envd, alive_end=str(alive), verdict=str(verdict), **digests)
        return run(envd, [("cli", write_trace(envd, "cli", fields=f, hashes=hs))])

    assert case(500, 0) == 0
    assert "RESULT set=main legs=2 ticks=1440 all_equal=yes PASS" in capsys.readouterr().out
    assert case(600, 0) == 1
    assert "alive_end 600, expected < 600" in capsys.readouterr().out
    assert case(500, 2) == 1
    assert "verdict is 2, expected 0" in capsys.readouterr().out


def test_final_passes_with_one_commit_and_dll(env, capsys):
    ex = {"dll_sha256": env["dll_sha"], "ue_module_sha256": "e" * 64}
    legs = [("cli", write_trace(env, "cli")),
            ("unreal", write_trace(env, "unreal", fields=good_fields(env, **ex))),
            ("cpp", write_trace(env, "cpp", fields=good_fields(env, dll_sha256=env["dll_sha"])))]
    assert run(env, legs, final=True) == 0
    assert "commits=1 dirty=0 dll=1 ue_build=1 PASS" in capsys.readouterr().out


@pytest.mark.parametrize("over,needle", [
    ({"dirty": "1"}, "dirty != 0"),
    ({"commit": "f" * 40}, "exactly one commit"),
    ({"dll_sha256": "9" * 64}, "is not the published DLL"),
])
def test_final_provenance_failures(env, capsys, over, needle):
    ex = {"dll_sha256": env["dll_sha"], "ue_module_sha256": "e" * 64}
    ex2 = dict(ex)
    ex2.update(over)
    legs = [("cli", write_trace(env, "cli")), ("unreal", write_trace(env, "unreal", fields=good_fields(env, **ex2)))]
    assert run(env, legs, final=True) == 1
    assert needle in capsys.readouterr().out


def test_final_needs_dll_hash_on_every_native_leg(env, capsys):
    legs = [("cli", write_trace(env, "cli")), ("godot", write_trace(env, "godot")),
            ("cpp", write_trace(env, "cpp")),
            ("unreal", write_trace(env, "unreal", fields=good_fields(env, dll_sha256=env["dll_sha"],
                                                                     ue_module_sha256="e" * 64)))]
    assert run(env, legs, final=True) == 1
    out = capsys.readouterr().out
    assert "cpp: no dll_sha256 header" in out
    assert "godot: no dll_sha256" not in out and "cli: no dll_sha256" not in out


def test_final_needs_unreal_build_hash(env, capsys):
    legs = [("cli", write_trace(env, "cli")),
            ("unreal", write_trace(env, "unreal", fields=good_fields(env, dll_sha256=env["dll_sha"])))]
    assert run(env, legs, final=True) == 1
    assert "no ue_module_sha256" in capsys.readouterr().out


def test_parity_png_marks_a_one_tick_divergence(env):
    pytest.importorskip("matplotlib")
    hs = list(HASHES)
    hs[1] ^= 1
    p = write_trace(env, "cpp", hashes=hs)
    args = ["--set", "main", "--golden-dir", env["golden"], "--orders", env["orders"], "--out", str(env["tmp"] / "png"),
            "cpp=" + p]
    assert ct.main(args) == 1
    assert (env["tmp"] / "png" / "parity.png").stat().st_size > 5000
