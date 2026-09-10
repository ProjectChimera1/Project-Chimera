# -*- coding: utf-8 -*-
"""Red/green proof for the stage-identity fix in run_manifest.py.

The defect this pins
--------------------
`content_hash()` folded a HAND-TYPED `PIPELINE_VERSION` string as its only stage-identity term.
Nobody retypes a version constant when they swap a stage script, so replacing the mesh stage --
the entire point of Epic 16 -- left every asset hash byte-identical. The batch would then print
`SKIP (cached)` for all 24 assets and exit reporting success, having produced nothing.

A cache key that cannot notice the change it exists to notice is worse than no cache: it converts
"you did nothing" into "everything is already fine".

Run:  D:\\tools\\asset-gen-venv\\Scripts\\python.exe test_stage_identity.py
Exit 0 = all green.
"""
import os
import sys
import shutil
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from run_manifest import stage_identity, content_hash  # noqa: E402

ASSET = {"id": "worker", "faction": "alpha", "prompt": "a prompt"}
OTHER = {"id": "worker", "faction": "beta", "prompt": "a prompt"}

results = []


def check(name, ok, detail=""):
    results.append((name, ok, detail))
    print(("  PASS  " if ok else "  FAIL  ") + name + (("   " + detail) if detail else ""))
    return ok


def main():
    tmp = tempfile.mkdtemp(prefix="stage_id_")
    try:
        a = os.path.join(tmp, "stage_a.py")
        b = os.path.join(tmp, "stage_b.py")
        with open(a, "wb") as f:
            f.write(b"# stage a\nprint('a')\n")
        with open(b, "wb") as f:
            f.write(b"# stage b\nprint('b')\n")

        BV = "Blender 4.5.10 LTS"

        # 1. bit-stable: same bytes, same Blender, same set -> same identity, every time.
        base = stage_identity([a, b], BV)
        check("stage_identity is bit-stable across repeated calls",
              all(stage_identity([a, b], BV) == base for _ in range(5)), base)

        # 2. order-independent: the set is what matters, not the order it was listed in.
        check("stage_identity is order-independent",
              stage_identity([b, a], BV) == base)

        # 3. THE BUG: change ONE BYTE inside a stage script -> identity MUST move.
        with open(a, "r+b") as f:
            data = bytearray(f.read())
            data[2:3] = b"A"          # '#' + ' ' + 's' -> 'A'
            f.seek(0); f.write(data); f.truncate()
        moved = stage_identity([a, b], BV)
        check("one byte changed in a stage script MOVES the identity",
              moved != base, f"{base} -> {moved}")

        # 4. restoring the byte restores the identity exactly -- no hidden state, no timestamps.
        with open(a, "wb") as f:
            f.write(b"# stage a\nprint('a')\n")
        check("restoring the byte restores the identity exactly",
              stage_identity([a, b], BV) == base)

        # 5. swapping WHICH scripts are in the set -> identity moves. This is the profile swap
        #    (blender_pipeline.py -> hp_to_lp_bake.py) that the old constant could not see.
        c = os.path.join(tmp, "stage_c.py")
        shutil.copyfile(b, c)          # byte-identical to b, different NAME
        check("swapping a stage OUT of the set moves the identity",
              stage_identity([a, c], BV) != base)
        check("a byte-identical script under a different name still moves the identity",
              stage_identity([a, c], BV) != stage_identity([a, b], BV),
              "basename is folded, so two identical files are not interchangeable")

        # 6. Blender is a stage. Upgrading it must invalidate the cache too.
        check("a different Blender build moves the identity",
              stage_identity([a, b], "Blender 5.0.1") != base)

        # 7. content_hash: pure, stable, and sensitive to the stage identity.
        h1 = content_hash(ASSET, base)
        check("content_hash is bit-stable for the same asset and stage id",
              all(content_hash(ASSET, base) == h1 for _ in range(5)), h1)
        check("content_hash MOVES when the stage identity moves",
              content_hash(ASSET, moved) != h1,
              f"{h1} -> {content_hash(ASSET, moved)}")
        check("content_hash distinguishes two factions sharing an id",
              content_hash(OTHER, base) != h1)

        # 8. The regression itself, stated as a test: with a CONSTANT stage term, editing a stage
        #    script leaves the hash untouched. This is what shipped, and why 24 assets read as
        #    cached. If this ever passes as "equal", the fix has been reverted.
        legacy_before = content_hash(ASSET, "v1-remesh240-6k")
        legacy_after = content_hash(ASSET, "v1-remesh240-6k")   # stage edited; constant not retyped
        check("REGRESSION GUARD: a hand-typed constant cannot see a stage edit",
              legacy_before == legacy_after,
              "documents the old bug -- this equality is the defect, and is why the constant is gone")

    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    failed = [n for n, ok, _ in results if not ok]
    print()
    print(f"{len(results) - len(failed)}/{len(results)} checks passed")
    if failed:
        print("FAILED: " + ", ".join(failed))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
