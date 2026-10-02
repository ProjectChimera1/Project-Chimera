#!/usr/bin/env python
"""Write the S3 asset summary (plan-c-scatter.md 4 S3) from the commandlet's report(s), and check from-clean reproducibility.

Usage: python scatter_assets_summary.py <report.json> [<second from-clean report.json> ...] [--out T/Out/scatter_assets/summary.txt]
Every report must be a full run with errors 0. With two or more reports, their deterministic_sha256 values (the whole report but timings: S3's
own reproducibility) and asset_settings_sha256 values (triangles, flags, LODs, usage, dependencies, texture flags: the fields plan S6's from-clean
re-run compares, without the material scalars a look round may change) must be equal. Prints SUMMARY OK reports=<n> sha_equal=<True|n/a> -> <out>, or SUMMARY FAIL <reason> (exit 1).
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
T = os.path.dirname(HERE)
OUT = os.path.join(T, "Out", "scatter_assets", "summary.txt")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("reports", nargs="+")
    ap.add_argument("--out", default=OUT)
    a = ap.parse_args()
    reps = [(p, json.load(open(p, encoding="utf-8"))) for p in a.reports]
    for p, r in reps:
        if not r.get("full_run") or r["checks"]["errors"] != 0:
            print(f"SUMMARY FAIL {p}: full_run={r.get('full_run')} errors={r['checks']['errors']}")
            return 1
    shas = [r["deterministic_sha256"] for _, r in reps]
    sha_equal = (len(set(shas)) == 1) if len(shas) > 1 else None
    if sha_equal is False:
        print(f"SUMMARY FAIL deterministic_sha256 differ: {shas}")
        return 1
    sets = [r.get("asset_settings_sha256") for _, r in reps]
    if None in sets or (len(sets) > 1 and len(set(sets)) != 1):
        print(f"SUMMARY FAIL asset_settings_sha256 missing or differ: {sets}")
        return 1
    p0, r = reps[-1]
    c = r["checks"]
    L = ["S3 scatter import and materials (plan-c-scatter.md 3.6-3.7, 4 S3); written by Tools/scatter_assets_summary.py",
         "command: LOCK PS T/Tools/run_commandlet.ps1 -Script T/Scripts/make_scatter_assets.py -Tag <tag> -Sentinel SCATTER_OK -TimeoutMin 60,",
         "         each run on a deleted Content/Terrain/Scatter (the script refuses a non-empty one)",
         f"engine {r['run']['engine']}, script wall {r['run']['wall_s']} s (last report)"]
    for p, rr in reps:
        L.append(f"from-clean run {p}: t={rr['run']['t']} deterministic_sha256={rr['deterministic_sha256']} "
                 f"asset_settings_sha256={rr['asset_settings_sha256']}")
    L.append("deterministic_sha256 and asset_settings_sha256 equal across the from-clean runs: %s"
             % ("yes (%d runs)" % len(reps) if sha_equal else "n/a (one run)"))
    L.append("S6's from-clean re-run compares asset_settings_sha256 (plan S6: triangle counts, flags and dependencies)")
    n_inst = len(r["instances"])
    L += ["",
          f"checks: errors={c['errors']} usage_ok={c['usage_ok']} deps_ok={int(c['deps_ok'])} flip_ok={int(c['flip_ok'])} vc_ok={int(c['vc_ok'])}",
          f"packages under /Game/Terrain/Scatter: {r['packages']} = {len(r['meshes'])} meshes + {len(r['masters'])} masters + {n_inst} "
          f"material instances + {len(r['textures'])} textures",
          f"outside /Game/Terrain/Scatter: {json.dumps(r['outside_scatter'])}",
          "", "masters (default permutation): ps instructions, two-sided, Nanite usage, blend"]
    for n, m in sorted(r["masters"].items()):
        L.append(f"  {n:22s} ps={m['stats']['num_pixel_shader_instructions']:4d} two_sided={m['two_sided_read_back']!s:5s} nanite={m['nanite']!s:5s} "
                 f"{m['blend']}{' fade=' + m['fade'] if m.get('fade') else ''} translation_errors={m['translation_errors']}")
    sl = c.get("shader_log") or {}
    L.append(f"shader-compile error lines in the run's own log: {sl.get('error_lines')} (scanned={sl.get('scanned')}); "
             "RecompileMaterial's list is translation errors only (it returns before the async compile)")
    tags = r.get("material_tags") or {}
    L.append(f"HasPerInstanceRandom asset-registry tag: {sum(1 for t in tags.values() if t['HasPerInstanceRandom'] == 'False')} of "
             f"{len(tags)} materials 'False'; expression scan per_instance_random False on every master")
    fc = c.get("flip_counts") or {}
    L.append(f"flip-green: {fc.get('normal_maps')} normal maps: {fc.get('flipped')} flipped ({fc.get('flipped_by_interchange')} of "
             f"{fc.get('gltf_normal_maps')} glTF normal maps already by Interchange, gated: F23), plus the flat default normal, exempt")
    L += ["", "imported source triangles vs the generator's or Blender's report (render LOD0 of a Nanite row is the reduced fallback)",
          f"{'mesh':44s} nanite  expected    source renderLOD0  lods  materials (parent)"]
    for p, m in sorted(r["meshes"].items()):
        parents = sorted({r["instances"][x["instance"]]["parent"] for x in m.get("materials") or [] if x.get("instance") in r["instances"]})
        L.append(f"{p.split('/Meshes/')[1]:44s} {m['nanite']!s:6s} {m.get('triangles_expected')!s:>9s} {m['triangles_source']:9d} "
                 f"{m.get('triangles_render_lod0', 0):9d} {m.get('lod_count')!s:>5s}  {','.join(parents)}")
    L += ["", "Nanite fallback (render LOD0) targets and LOD chains"]
    for p, m in sorted(r["meshes"].items()):
        fb = m.get("nanite_fallback") or {}
        if "PERCENT" in str(fb.get("target", "")).upper():
            L.append(f"  {p.split('/Meshes/')[1]:30s} fallback {fb['target']} {fb['percent_triangles']}: {m['triangles_render_lod0']} of "
                     f"{m['triangles_source']} triangles")
        for x in m.get("lods") or []:
            L.append(f"  {p.split('/Meshes/')[1]:30s} LOD{x['lod']} triangles={x['triangles']} screen_size={x['screen_size']}")
    L += ["", "vertex colours of the FINAL saved assets (ASCII FBX exports vs the glb COLOR_0, multisets of whole RGBA tuples):",
          "  source = LOD0 mesh description of the Nanite mesh; render LODn = the non-Nanite _L render data per LOD"]
    for v in r["vc_rows"]:
        what = "source" if v.get("mode") == "final_source_mesh_description" else f"render LOD{v.get('lod')}"
        extra = ""
        if v.get("render_lod0"):
            rl = v["render_lod0"]
            extra = f"  render LOD0 (fallback) corners {rl['corners']}, non-white {rl['non_white_corners']}"
        L.append(f"  {v['mesh']:12s} {what:12s} corners {v.get('glb_corners')} == {v.get('fbx_corners')}  tuples equal "
                 f"{v.get('tuple_multiset_equal')}  max abs byte diff {v.get('max_abs_diff_bytes')}{extra}")
    ve = r.get("vc_expect") or {}
    L.append(f"meshes whose material reads vertex colour ({', '.join(ve.get('masters_reading_vc', []))}): {ve.get('meshes_needing_vc')}, "
             f"HasVertexColors true on {ve.get('meshes_needing_vc_with_vc')} of them, missing {ve.get('missing')}")
    dup = {m['path'].rsplit('/', 1)[1]: m['duplicate_textures_deleted'] for m in r['meshes'].values() if m.get('duplicate_textures_deleted')}
    L.append(f"duplicate Interchange textures deleted (one glTF image, several glTF textures; sizes compared): {dup}")
    L.append("engine dependencies (manifest 'epic' rows): " + ", ".join(d["path"] for d in r.get("engine_dependencies", [])))
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(L) + "\n")
    print(f"SUMMARY OK reports={len(reps)} sha_equal={sha_equal if sha_equal is not None else 'n/a'} -> {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
