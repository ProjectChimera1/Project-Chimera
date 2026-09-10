"""Batch-gate everything in D:/tripo-out against the Chimera engine profile.
Reports the Tier-1 verdict plus the mesh_scale needed to preserve in-engine height."""
import json, os, glob, subprocess, sys, trimesh
REPO="D:/Projects/Project_Chimera"
PROF=REPO+"/tools/asset-gen/config/engine_profiles/godot_chimera.json"
GATE=REPO+"/tools/asset-gen/scripts/qa/trimesh_gate.py"
PY_=sys.executable
man=json.load(open(REPO+"/tools/asset-gen/config/chimera_assets.json",encoding="utf-8"))
by_base={os.path.splitext(os.path.basename(a["mesh_file"]))[0]:a for a in man["assets"]}

def shipped_height(a):
    p=os.path.join(REPO,a["dest"])
    if not os.path.exists(p): return None
    b=trimesh.load(p,force="scene").bounds
    return float(b[1][1]-b[0][1])

rows=[]
for f in sorted(glob.glob("D:/tripo-out/*.glb")):
    stem=os.path.splitext(os.path.basename(f))[0]
    base=stem[:-7] if stem.endswith("_rigged") else stem
    a=by_base.get(base)
    if not a:
        rows.append((os.path.basename(f),"?","NO MANIFEST MATCH","","","")); continue
    kind=a["tri_kind"]
    r=subprocess.run([PY_,GATE,f,"--kind",kind,"--profile",PROF,"--require-textured"],
                     capture_output=True,text=True)
    line=[l for l in (r.stdout or "").splitlines() if l.startswith("GATE_JSON")]
    if not line:
        rows.append((os.path.basename(f),kind,"GATE ERROR","","","")); continue
    g=json.loads(line[0][len("GATE_JSON "):]); m=g["metrics"]
    b=trimesh.load(f,force="scene").bounds; h=float(b[1][1]-b[0][1])
    sh=shipped_height(a)
    scale = (sh*a["mesh_scale"]/h) if (sh and h) else None
    flags=[]
    if m["materials"]>1: flags.append("MATS>1")
    if m.get("inside_out"): flags.append("INSIDE-OUT")
    if not m.get("has_albedo_texture"): flags.append("NO-TEX")
    if not m.get("base_color_factor_white"): flags.append("TINTED-BCF")
    if m.get("extensions"): flags.append("EXT:"+",".join(m["extensions"]))
    if abs(m["min_y"])>0.02: flags.append("ORIGIN-Y")
    rows.append((os.path.basename(f),kind,g["verdict"],m["tris"],
                 ("%.3f"%scale if scale else "-"),
                 " ".join(flags) or ("warn: "+g["warns"][0].split("(")[0].strip() if g["warns"] else "clean")))
w=max((len(r[0]) for r in rows),default=10)
print(f'{"file":{w}} {"kind":9} {"verdict":8} {"tris":>7} {"scale":>7}  notes')
for r in rows: print(f'{r[0]:{w}} {r[1]:9} {r[2]:8} {str(r[3]):>7} {str(r[4]):>7}  {r[5]}')
print(f"\n{len(rows)} file(s).  scale = mesh_scale that preserves the shipped in-engine height.")
