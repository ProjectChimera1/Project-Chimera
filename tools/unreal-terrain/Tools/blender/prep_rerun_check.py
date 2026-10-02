#!/usr/bin/env python
"""Second-run determinism check of the L1 Blender prep (plan-c-scatter.md 4 S1: "identical vertex hashes on a second run").

Rebuilds every prepared mesh (prep_polyhaven.py, then make_bush.py) into a temporary folder (CHIMERA_SCATTER_PREP / CHIMERA_SCATTER_WORK;
the raw sources are read from ScatterSrc as usual), then compares each row of the temporary report with ScatterSrc/prepared/report.json:
vertex_position_sha256 and the .glb file sha256. The checked folder is never written.
Usage: python prep_rerun_check.py [--keep]   -> PREP RERUN meshes=<n> vertex_hash_equal=<a>/<n> file_sha_equal=<b>/<n> (exit 0 when all equal)
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import prep_polyhaven as pp  # noqa: E402


def main():
    pp.refuse_mirror()
    keep = "--keep" in sys.argv
    ref_path = os.path.join(pp.PREP, "report.json")
    ref = {m["name"]: m for m in json.load(open(ref_path, encoding="utf-8"))["meshes"]}
    tmp = tempfile.mkdtemp(prefix="scatter_prep_rerun_")
    env = dict(os.environ, CHIMERA_SCATTER_SRC=pp.SRC, CHIMERA_SCATTER_PREP=os.path.join(tmp, "prepared"),
               CHIMERA_SCATTER_WORK=os.path.join(tmp, "work"))
    for script in ("prep_polyhaven.py", "make_bush.py"):
        p = subprocess.run([sys.executable, os.path.join(HERE, script)], env=env, capture_output=True, text=True)
        last = (p.stdout.strip().splitlines() or [""])[-1]
        print(f"  {script}: {last}")
        if p.returncode != 0:
            print(f"PREP RERUN FAIL {script} exit {p.returncode}\n{p.stdout[-2000:]}\n{p.stderr[-2000:]}")
            return 1
    new = {m["name"]: m for m in json.load(open(os.path.join(tmp, "prepared", "report.json"), encoding="utf-8"))["meshes"]}
    names = sorted(set(ref) | set(new))
    vh = sum(1 for n in names if n in ref and n in new and ref[n]["vertex_position_sha256"] == new[n]["vertex_position_sha256"])
    fs = sum(1 for n in names if n in ref and n in new and ref[n]["sha256"] == new[n]["sha256"])
    for n in names:
        if n not in ref or n not in new or ref[n]["sha256"] != new[n]["sha256"]:
            print(f"  DIFF {n}: checked {ref.get(n, {}).get('sha256')} rerun {new.get(n, {}).get('sha256')}")
    if keep:
        print(f"  kept {tmp}")
    else:
        shutil.rmtree(tmp, ignore_errors=True)
    ok = vh == fs == len(names)
    print(f"PREP RERUN {'OK' if ok else 'FAIL'} meshes={len(names)} vertex_hash_equal={vh}/{len(names)} file_sha_equal={fs}/{len(names)}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
