# -*- coding: utf-8 -*-
"""Emit a Chimera concept-plate ComfyUI workflow (Flux GGUF), matching the node types
already proven in Alec's flux_clinical_workflow.json.

  build_workflow.py bolt_sanctum              single front plate
  build_workflow.py bolt_sanctum --twoview    front+back sheet, auto-split into two PNGs

--twoview generates ONE latent containing both views and crops it in the graph. That is
deliberate: two separate generations of "front" and "back" produce two DIFFERENT buildings,
which is worse than the invented back it was meant to replace. One latent is one building.
"""
import io, json, os, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
WFDIR = r"D:\tools\ComfyUI_windows_portable\ComfyUI\user\default\workflows"

args = [a for a in sys.argv[1:]]
TWO = "--twoview" in args
ANGLED = "--angled" in args
args = [a for a in args if not a.startswith("--")]
ASSET = args[0] if args else "bolt_sanctum"

cmd = [sys.executable, os.path.join(HERE, "tripo_block_to_flux.py"), ASSET]
if TWO: cmd.append("--twoview")
if ANGLED: cmd.append("--angled")
prompt = subprocess.run(cmd, capture_output=True, text=True,
                        encoding="utf-8").stdout.split("\n---")[0].strip()

# Panel geometry. Buildings are wide, so each panel is 4:3 landscape.
import re as _re
_rb = io.open("D:/tripo-input/TRIPO_RUNBOOK.md", encoding="utf-8").read()
_m = _re.search(r"### `" + _re.escape(ASSET) + r"`(.*?)```(.*?)```", _rb, _re.S)
IS_CHAR = bool(_m) and "this is the character" in _m.group(2)
PANEL_W, PANEL_H = (768, 1024) if IS_CHAR else (1024, 768)
W, H = (PANEL_W * 2, PANEL_H) if TWO else ((832, 1216) if IS_CHAR else (1216, 832))

links, _lid = [], [0]
N = []
def node(nid, typ, pos, size, widgets, inputs=None, outputs=None, title=None):
    N.append(dict(id=nid, type=typ, pos=pos, size=size, flags={}, order=len(N), mode=0,
                  inputs=inputs or [], outputs=outputs or [],
                  properties={"Node name for S&R": typ}, widgets_values=widgets,
                  **({"title": title} if title else {})))

node(1, "UnetLoaderGGUF", [40,60], [400,60], ["flux1-dev-Q5_K_S.gguf"],
     outputs=[{"name":"MODEL","type":"MODEL","links":[],"slot_index":0}], title="Flux model (GGUF)")
node(2, "DualCLIPLoaderGGUF", [40,170], [400,130],
     ["t5-v1_1-xxl-encoder-Q5_K_M.gguf","clip_l.safetensors","flux"],
     outputs=[{"name":"CLIP","type":"CLIP","links":[],"slot_index":0}], title="Text encoders")
node(3, "VAELoader", [40,340], [400,60], ["ae.safetensors"],
     outputs=[{"name":"VAE","type":"VAE","links":[],"slot_index":0}], title="VAE")
node(4, "CLIPTextEncode", [480,60], [620,360], [prompt],
     inputs=[{"name":"clip","type":"CLIP","link":None}],
     outputs=[{"name":"CONDITIONING","type":"CONDITIONING","links":[],"slot_index":0}],
     title="CHIMERA_PROMPT")
node(5, "CLIPTextEncode", [480,460], [620,90], [""],
     inputs=[{"name":"clip","type":"CLIP","link":None}],
     outputs=[{"name":"CONDITIONING","type":"CONDITIONING","links":[],"slot_index":0}],
     title="negative - LEAVE EMPTY (Flux ignores it)")
node(6, "FluxGuidance", [1140,60], [300,60], [3.0],
     inputs=[{"name":"conditioning","type":"CONDITIONING","link":None}],
     outputs=[{"name":"CONDITIONING","type":"CONDITIONING","links":[],"slot_index":0}],
     title="Guidance - 3.0 keeps it flat/graphic")
node(7, "EmptyLatentImage", [1140,170], [300,110], [W, H, 1],
     outputs=[{"name":"LATENT","type":"LATENT","links":[],"slot_index":0}],
     title=("Sheet %dx%d - two %dx%d panels" % (W,H,PANEL_W,PANEL_H)) if TWO
           else "Plate size - 1216x832 buildings / 832x1216 units")
node(8, "KSampler", [1490,60], [320,270], [0,"randomize",25,1.0,"euler","beta",1.0],
     inputs=[{"name":"model","type":"MODEL","link":None},
             {"name":"positive","type":"CONDITIONING","link":None},
             {"name":"negative","type":"CONDITIONING","link":None},
             {"name":"latent_image","type":"LATENT","link":None}],
     outputs=[{"name":"LATENT","type":"LATENT","links":[],"slot_index":0}])
node(9, "VAEDecode", [1860,60], [220,60], [],
     inputs=[{"name":"samples","type":"LATENT","link":None},
             {"name":"vae","type":"VAE","link":None}],
     outputs=[{"name":"IMAGE","type":"IMAGE","links":[],"slot_index":0}])

if TWO:
    node(10,"SaveImage",[1860,170],[460,330],["Chimera_Plate/%s_SHEET" % ASSET],
         inputs=[{"name":"images","type":"IMAGE","link":None}],
         title="full sheet - judge consistency here")
    node(11,"ImageCrop",[2360,170],[300,150],[PANEL_W,PANEL_H,0,0],
         inputs=[{"name":"image","type":"IMAGE","link":None}],
         outputs=[{"name":"IMAGE","type":"IMAGE","links":[],"slot_index":0}], title="LEFT = front")
    node(12,"SaveImage",[2700,170],[420,320],["Chimera_Plate/%s_front" % ASSET],
         inputs=[{"name":"images","type":"IMAGE","link":None}], title="-> Tripo front")
    node(13,"ImageCrop",[2360,540],[300,150],[PANEL_W,PANEL_H,PANEL_W,0],
         inputs=[{"name":"image","type":"IMAGE","link":None}],
         outputs=[{"name":"IMAGE","type":"IMAGE","links":[],"slot_index":0}], title="RIGHT = back")
    node(14,"SaveImage",[2700,540],[420,320],["Chimera_Plate/%s_back" % ASSET],
         inputs=[{"name":"images","type":"IMAGE","link":None}], title="-> Tripo back")
else:
    node(10,"SaveImage",[1860,170],[520,460],["Chimera_Plate/%s" % ASSET],
         inputs=[{"name":"images","type":"IMAGE","link":None}], title="Saves to output/Chimera_Plate/")

by = {n["id"]: n for n in N}
def wire(a, aslot, b, bslot, typ):
    _lid[0] += 1
    links.append([_lid[0], a, aslot, b, bslot, typ])
    by[a]["outputs"][aslot]["links"].append(_lid[0])
    by[b]["inputs"][bslot]["link"] = _lid[0]

wire(2,0,4,0,"CLIP");  wire(2,0,5,0,"CLIP");  wire(4,0,6,0,"CONDITIONING")
wire(1,0,8,0,"MODEL"); wire(6,0,8,1,"CONDITIONING"); wire(5,0,8,2,"CONDITIONING")
wire(7,0,8,3,"LATENT"); wire(8,0,9,0,"LATENT"); wire(3,0,9,1,"VAE")
wire(9,0,10,0,"IMAGE")
if TWO:
    wire(9,0,11,0,"IMAGE");  wire(11,0,12,0,"IMAGE")
    wire(9,0,13,0,"IMAGE");  wire(13,0,14,0,"IMAGE")

groups = [{"id":1,"title":"1. Models - leave alone","bounding":[20,10,440,420],"color":"#3f789e","font_size":24,"flags":{}},
          {"id":2,"title":"2. THE PROMPT","bounding":[460,10,660,560],"color":"#a1309b","font_size":24,"flags":{}},
          {"id":3,"title":"3. Dials","bounding":[1120,10,340,300],"color":"#b58b2a","font_size":24,"flags":{}},
          {"id":4,"title":"4. Generate","bounding":[1470,10,880,520],"color":"#3f789e","font_size":24,"flags":{}}]
if TWO:
    groups.append({"id":5,"title":"5. Auto-split -> feed BOTH to Tripo",
                   "bounding":[2340,110,800,790],"color":"#2a7a4b","font_size":24,"flags":{}})

name = "Chimera_Concept_Plate_2View" if TWO else "Chimera_Concept_Plate"
out = os.path.join(WFDIR, name + ".json")
io.open(out,"w",encoding="utf-8").write(json.dumps(dict(
    id=name.lower(), revision=0, last_node_id=max(by), last_link_id=_lid[0],
    nodes=N, links=links, groups=groups, config={},
    extra={"ds":{"scale":0.5,"offset":[60,40]}}, version=0.4), indent=2))
print("wrote", out)
print("asset:", ASSET, "| mode:", "TWO-VIEW %dx%d -> 2x %dx%d" % (W,H,PANEL_W,PANEL_H) if TWO else "single %dx%d" % (W,H))
print("prompt words:", len(prompt.split()))
