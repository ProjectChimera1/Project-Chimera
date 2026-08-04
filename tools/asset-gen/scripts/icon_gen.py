# -*- coding: utf-8 -*-
"""Icon pipeline — WC3-style portrait icons for every unit, building and item.

    python icon_gen.py --placeholders     # write representative SVG stand-ins (no GPU, runs anywhere)
    python icon_gen.py --prompts          # emit the locked icon prompts (feed to SDXL / review them)
    python icon_gen.py                    # generate real PNGs via ComfyUI (needs the venv python + GPU)

WHY THIS SHARES chimera_assets.json RATHER THAN DESCRIBING OBJECTS AGAIN
-----------------------------------------------------------------------
Every object already carries ONE appearance description — the `SUBJECT:` clause of its mesh prompt
("a slight young acolyte in a knee-length slate-blue work coat ... one simple brass automail forearm").
An icon that re-describes the object from scratch will drift from the model it is supposed to depict,
and the drift is invisible until you see them side by side in game. So the icon prompt is built as

    ICON_PREFIX + <the object's own faction PALETTE> + <the object's own SUBJECT clause, verbatim>

which makes "the icon matches the model" true BY CONSTRUCTION instead of by discipline. Adding a new
unit needs no icon authoring at all: it inherits one the moment its mesh entry exists.

PLACEHOLDERS
------------
Real icons need art. Until then `--placeholders` writes deterministic SVGs built from the SAME record:
faction palette for the plate, a category-coded silhouette (worker/soldier/scout/heavy/caster/hero/
vehicle/air/structure/item), and the object's initials. They are representative, not decorative — a
Bulwark Adept reads as a broad heavy shape in Covenant slate-blue, a Quicksilver Runner as a thin dart —
so the UI can be built and judged now, and a painted PNG later drops into the same slot with no code
change (the loader prefers .png and falls back to .svg).

STATE OF REAL GENERATION (measured 2026-08-04, four GPU passes on `worker`)
--------------------------------------------------------------------------
The rail WORKS end to end: ComfyUI 0.18.5 on an RTX 3060 with sd_xl_base_1.0 produced real 512px PNGs
through this script. FRAMING is solved by the prompt above (bust, face, no legs, no frame artifacts).

What prompt engineering alone did NOT solve is identity fidelity and the painted-icon STYLE. SDXL base
drifts to generic ornate fantasy armour and discards the specific identity: "a slight young acolyte in a
knee-length work coat with a chalk satchel" came back as an armoured knight with gold filigree, twice.
Pass 3 regressed outright. This is a MODEL-ASSET gap, not a prompt gap — D:/ai-models/{loras,ipadapter,
clip_vision} are all EMPTY (the IPAdapter *node* is installed, its models are not).

RECOMMENDED SEQUENCING (architectural, not a workaround)
An icon should be generated FROM the unit's concept image via img2img / IPAdapter, not from text alone.
That is the same artefact the mesh pipeline already produces, and conditioning on it makes "the icon
depicts the same character as the model" structurally true rather than a hope. The manifest records 0
committed GLBs, so the concept pass has not run yet — meaning text-only icons generated NOW would not
match the models made LATER. Icons therefore sequence after (or alongside) the mesh concept pass; the
SVG placeholders carry the UI until then.
"""
import argparse, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
CONFIG = os.path.join(HERE, "..", "config", "chimera_assets.json")
ICON_DIR = os.path.join(ROOT, "godot", "resources", "icons")

# ── The locked icon style, mirroring the manifest's ONE-SHARED-PREFIX discipline ────────────────────
# Square, bust-framed and high-contrast because the icon is read at 64px in a command card, where a
# full-body mesh pose is illegible. The dark vignette + rim light is the WC3 icon read: subject pops
# off a recessed background at a glance.
ICON_PREFIX = (
    "game UI portrait icon, square 1:1 crop, "
)
# Framing is asserted AFTER the subject, not before. The SUBJECT clauses were authored for MESH
# reference — they describe whole bodies ("knee-length coat", "a pick at the hip", A-pose on a plain
# white background) — and SDXL follows those concrete body cues over an opening framing instruction.
# The first attempt produced exactly that: a full-body figure on white, boots included, no face. So the
# override lands last, where recency gives it weight, and the negative names the failure explicitly.
ICON_FRAMING = (
    " HARD FRAMING OVERRIDE: extreme close-up HEAD AND SHOULDERS portrait bust, "
    "face clearly visible and centered, cropped tightly at the upper chest, shoulders filling the width, "
    "NO legs, NO feet, NO full figure, subject fills 90% of the square, "
    "flat dark charcoal backdrop behind the head, strong warm rim light along the silhouette edge, "
    "bold high-contrast shapes readable at 64 pixels, painted game-icon art with clean edges"
)
ICON_NEG = (
    # Every entry is a failure OBSERVED while iterating on `worker`, not a guess:
    #   pass 1  full-body figure on white, no face      -> body/white-bg terms
    #   pass 2  correct bust + face, but a film-strip border and pale backdrop
    #           ("vignette" in the POSITIVE was inducing the frame; removed there)
    #   pass 3  "waist-up" was too weak and reverted to full body; "signature tool or weapon"
    #           handed the WORKER a sword -> both phrases dropped, weapon terms negated for non-combatants
    "full body, full figure, legs, feet, boots, knees, standing, A-pose, T-pose, sticker, die-cut outline, "
    "film strip, filmstrip, sprocket holes, picture frame, matte border, framed painting, inset panel, "
    "border, ui frame, rounded corners, drop shadow, "
    "white background, light background, pale background, beige background, grey background, sky, "
    "tiny subject, wide shot, zoomed out, distant, multiple characters, character sheet, turnaround, "
    "text, watermark, letters, signature, busy background, scenery, landscape, "
    "low contrast, washed out, blurry, back view, faceless, hood covering the face"
)
ICON_SIZE = 512  # generate large, downsample in-engine; 512 keeps detail for a 64px read

# ── Placeholder art ─────────────────────────────────────────────────────────────────────────────────
# Faction palette: (deep plate, lit face/mid tone, metal accent, rim light).
PALETTES = {
    "alpha": ("#1b2a3d", "#3d5a80", "#c8a24a", "#a8d0f0"),   # Covenant: slate-blue, brass, cyan-white sigil
    "beta":  ("#2a0f14", "#5c1f28", "#b08d57", "#e0707c"),   # Court: oxblood, dull brass, crimson core-glow
    "item":  ("#16211c", "#2f3a34", "#c8a24a", "#93c9a5"),
}

# Role comes from the FACTION JSON's own `category` field (Worker/Melee/Ranged/Siege/Air) — authoritative
# data, not prose guessing. An earlier pass inferred roles from the prompt text and got mage->soldier and
# crossbowman->heavy wrong, because substring matching also fires on unrelated words ("flowing" contains
# "wing", "flaring" contains "ring").
ROLE_BY_CATEGORY = {
    "Worker": "worker", "Melee": "melee", "Ranged": "ranged",
    "Siege": "siege", "Air": "air",
}

# Bust compositions — head + shoulders, matching the icon convention rather than a full body. Each role
# reads differently in OUTLINE alone (the thing that survives being shrunk to 64px): the worker is small
# and round-shouldered, melee is broad and square, ranged is narrow and tall, siege is a machine block.
BUSTS = {
    "worker": {"head": "M48 26 a11 11 0 1 1 -0.1 0 z",
               "body": "M27 78 Q27 52 40 47 L56 47 Q69 52 69 78 Z"},
    "melee":  {"head": "M48 24 a12 12 0 1 1 -0.1 0 z",
               "body": "M18 78 Q18 48 34 42 L62 42 Q78 48 78 78 Z"},
    "ranged": {"head": "M48 25 a10.5 10.5 0 1 1 -0.1 0 z",
               "body": "M31 78 Q31 50 41 45 L55 45 Q65 50 65 78 Z"},
    "siege":  {"head": "M36 34 L60 34 L64 44 L32 44 Z",
               "body": "M16 78 L16 52 L28 52 L34 44 L62 44 L68 52 L80 52 L80 78 Z"},
    "air":    {"head": "M48 32 a9 9 0 1 1 -0.1 0 z",
               "body": "M30 74 Q30 50 40 44 L56 44 Q66 50 66 74 Z"},
    "structure": {"head": "", "body": "M20 80 L20 46 L48 28 L76 46 L76 80 Z"},
    "item":   {"head": "", "body": "M41 28 L55 28 L55 38 L63 54 Q63 76 48 76 Q33 76 33 54 L41 38 Z"},
}

# Motifs are matched on the SUBJECT clause with WORD BOUNDARIES (see the substring bug above) and drawn
# as a small accent so two units sharing a role still differ at a glance.
MOTIFS = {
    "automail":  "M64 58 L74 58 L74 70 L64 70 Z M66 60 L72 60 M66 64 L72 64",   # brass forearm plate
    "claw":      "M68 56 L74 68 M72 55 L76 66 M64 58 L68 70",
    "maul":      "M62 34 L78 34 L78 44 L62 44 Z M69 44 L69 66",
    "sword":     "M70 30 L74 34 L58 62 L54 58 Z",
    "crossbow":  "M58 50 L80 50 M69 42 L69 60 M62 44 Q69 50 76 44",
    "bolt":      "M60 44 L80 56 M74 52 L80 56 L74 60",
    "hood":      "M36 22 Q48 8 60 22 Q56 30 48 30 Q40 30 36 22 Z",
    "mask":      "M38 30 L58 30 L58 40 Q48 46 38 40 Z",
    "scarf":     "M36 46 Q24 54 22 70 L32 70 Q34 56 42 50 Z",
    "wings":     "M18 44 Q34 34 44 44 M78 44 Q62 34 52 44",
    "barrel":    "M60 40 L84 46 L84 54 L60 50 Z",
    "chimney":   "M62 20 L70 20 L70 46 L62 46 Z",
    "arch":      "M40 80 L40 62 Q48 52 56 62 L56 80 Z",
    "core":      "M48 60 a7 7 0 1 1 -0.1 0 z",
    "vent":      "M40 56 L56 56 M40 62 L56 62 M40 68 L56 68",
    "potion":    "M43 34 L53 34 L53 40 L59 54 Q59 72 48 72 Q37 72 37 54 L43 40 Z",
    "ring":      "M48 52 a13 13 0 1 1 -0.1 0 z M48 60 a5 5 0 1 0 0.1 0 z",
}
MOTIF_WORDS = {           # word(s) to look for -> motif key
    "automail": "automail", "prosthetic": "claw", "claw": "claw",
    "maul": "maul", "sword": "sword", "crossbow": "crossbow", "bow": "crossbow",
    "bolt": "bolt", "hood": "hood", "hooded": "hood", "mask": "mask", "masked": "mask",
    "scarf": "scarf", "wings": "wings", "wingspan": "wings",
    "barrel": "barrel", "mortar": "barrel", "cannon": "barrel",
    "smokestack": "chimney", "chimney": "chimney", "arched": "arch", "arch": "arch",
    "core": "core", "vent": "vent", "vents": "vent", "potion": "potion", "ring": "ring",
}

def subject_of(prompt: str) -> str:
    """The object's own appearance clause — everything after SUBJECT:, which is the ONLY part that
    describes what the thing actually looks like (the rest is shared style/pose boilerplate)."""
    i = prompt.find("SUBJECT:")
    return prompt[i + len("SUBJECT:"):].strip() if i >= 0 else prompt.strip()


def palette_of(prompt: str) -> str:
    """Reuse the object's own FACTION PALETTE sentence so icon and mesh share one colour contract."""
    m = re.search(r"FACTION PALETTE:.*?(?=SUBJECT:|$)", prompt, re.S)
    return m.group(0).strip() if m else ""


_CATEGORY_CACHE = {}


def faction_categories():
    """(faction, unit id) -> the faction JSON's own `category`. The authoritative role source."""
    if _CATEGORY_CACHE:
        return _CATEGORY_CACHE
    for fac in ("alpha", "beta"):
        fp = os.path.join(ROOT, "godot", "resources", "data", "factions", f"{fac}_faction.json")
        if not os.path.exists(fp):
            continue
        with open(fp, encoding="utf-8") as f:
            d = json.load(f)
        for u in d.get("units", []):
            _CATEGORY_CACHE[(fac, u["id"])] = u.get("category", "Melee")
    return _CATEGORY_CACHE


def category_of(asset: dict) -> str:
    """Icon role. Structures/vehicles/air come straight off the mesh prefix; humanoids use the faction
    JSON's declared category so the role can never disagree with the game data."""
    if asset["prefix"] == "structure":
        return "structure"
    cat = faction_categories().get((asset["faction"], asset["id"]))
    if cat:
        return ROLE_BY_CATEGORY.get(cat, "melee")
    return {"vehicle": "siege", "air": "air"}.get(asset["prefix"], "melee")


def motifs_of(subject: str, limit: int = 2) -> list:
    """Up to `limit` motif keys named by the SUBJECT, matched on WORD boundaries. Order follows the
    description so the most prominent feature wins."""
    low = subject.lower()
    found, seen = [], set()
    for m in re.finditer(r"[a-z]+", low):
        key = MOTIF_WORDS.get(m.group(0))
        if key and key not in seen:
            seen.add(key)
            found.append(key)
            if len(found) >= limit:
                break
    return found


def icon_prompt(asset: dict) -> str:
    # Order matters: identity first (palette + the object's own SUBJECT), framing override LAST.
    return (f"{ICON_PREFIX}\n{palette_of(asset.get('prompt',''))}\n"
            f"SUBJECT: {subject_of(asset.get('prompt',''))}\n{ICON_FRAMING}")


def load_assets():
    with open(CONFIG, encoding="utf-8") as f:
        cfg = json.load(f)
    rows = []
    for a in cfg["assets"]:
        rows.append({
            "id": a["id"], "faction": a["faction"], "kind": "buildings" if a["prefix"] == "structure" else "units",
            "category": category_of(a), "prompt": icon_prompt(a), "mesh": a.get("mesh_file", ""),
            "motifs": motifs_of(subject_of(a.get("prompt", ""))),
        })
    # Items live in the game data, not the mesh manifest (they have no model) — describe them here so
    # they ride the same rail as everything else rather than being a special case.
    items_dir = os.path.join(ROOT, "godot", "resources", "data", "items")
    for fn in sorted(os.listdir(items_dir)) if os.path.isdir(items_dir) else []:
        if not fn.endswith(".json"):
            continue
        with open(os.path.join(items_dir, fn), encoding="utf-8") as f:
            it = json.load(f)
        rows.append({
            "id": it["id"], "faction": "item", "kind": "items", "category": "item", "mesh": "",
            "motifs": motifs_of(it.get("display_name", it["id"]) + " " + it["id"]),
            "prompt": f"{ICON_PREFIX}\nSUBJECT: a single {it.get('display_name', it['id'])} "
                      f"as an alchemical apothecary object on a dark recessed background",
        })
    return rows


def svg_for(row: dict) -> str:
    """A composed placeholder: recessed faction plate, rim-lit bust in the object's ROLE outline, and up
    to two motifs the description actually names. Deliberately readable at 64px — the outline and the
    faction hue do the work, exactly as a real icon must."""
    deep, mid, metal, rim = PALETTES.get(row["faction"], PALETTES["item"])
    bust = BUSTS.get(row["category"], BUSTS["melee"])
    ms = row.get("motifs", [])
    initials = "".join(p[0] for p in re.split(r"[_\-\s]+", row["id"])[:2]).upper()
    motif_svg = "".join(
        f'<path d="{MOTIFS[m]}" fill="none" stroke="{metal}" stroke-width="2.4" '
        f'stroke-linecap="round" opacity="0.95"/>' for m in ms if m in MOTIFS)
    head = f'<path d="{bust["head"]}" fill="{mid}"/>' if bust["head"] else ""
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 96 96" width="96" height="96">
  <!-- PLACEHOLDER: {row['id']} ({row['faction']}/{row['category']}) motifs={ms or 'none'}
       Generated by tools/asset-gen/scripts/icon_gen.py --placeholders. Representative, not final.
       Role comes from the faction JSON's category; motifs from the object's own SUBJECT clause.
       Drop a painted {row['id']}.png beside this file to replace it (the loader prefers .png).
       Faction-scoped path: BuildingType ids are shared across factions. -->
  <defs>
    <radialGradient id="p{initials}{row['faction']}" cx="50%" cy="32%" r="78%">
      <stop offset="0%" stop-color="{mid}"/><stop offset="70%" stop-color="{deep}"/><stop offset="100%" stop-color="#080a0d"/>
    </radialGradient>
    <linearGradient id="r{initials}{row['faction']}" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0%" stop-color="{rim}" stop-opacity="0.95"/><stop offset="55%" stop-color="{rim}" stop-opacity="0"/>
    </linearGradient>
  </defs>
  <rect width="96" height="96" rx="9" fill="url(#p{initials}{row['faction']})"/>
  <path d="{bust['body']}" fill="{deep}" stroke="#05070a" stroke-width="1.5"/>
  {head}
  <path d="{bust['body']}" fill="url(#r{initials}{row['faction']})" opacity="0.55"/>
  {motif_svg}
  <rect x="2.5" y="2.5" width="91" height="91" rx="7.5" fill="none" stroke="{metal}" stroke-width="1.8" opacity="0.8"/>
  <text x="48" y="91" font-family="sans-serif" font-size="10" font-weight="700"
        text-anchor="middle" fill="{metal}" opacity="0.85">{initials}</text>
</svg>
"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--placeholders", action="store_true", help="write representative SVG stand-ins")
    ap.add_argument("--prompts", action="store_true", help="print/emit the locked icon prompts")
    ap.add_argument("--only", help="substring filter on id")
    args = ap.parse_args()

    rows = load_assets()
    if args.only:
        rows = [r for r in rows if args.only in r["id"]]

    if args.prompts:
        # Keyed faction/id, never id alone: BuildingType tokens are SHARED across factions, so
        # command_center is both the Covenant Sanctum and the Sanguine Furnace. Keying by id silently
        # collapses four buildings into two and hands two different structures the same icon.
        out = {r["faction"] + "/" + r["id"]: {"kind": r["kind"], "faction": r["faction"], "category": r["category"],
                         "size": ICON_SIZE, "negative": ICON_NEG, "prompt": r["prompt"]} for r in rows}
        dest = os.path.join(HERE, "..", "config", "icon_prompts.json")
        with open(dest, "w", encoding="utf-8") as f:
            json.dump(out, f, indent=1, ensure_ascii=False)
        print(f"wrote {len(out)} icon prompts -> {os.path.relpath(dest, ROOT)}")
        return

    if args.placeholders:
        n = 0
        for r in rows:
            # Faction-scoped for the same reason the prompt keys are (shared BuildingType tokens).
            d = os.path.join(ICON_DIR, r["kind"]) if r["kind"] == "items"                 else os.path.join(ICON_DIR, r["kind"], r["faction"])
            os.makedirs(d, exist_ok=True)
            with open(os.path.join(d, r["id"] + ".svg"), "w", encoding="utf-8") as f:
                f.write(svg_for(r))
            n += 1
        print(f"wrote {n} placeholder SVGs -> {os.path.relpath(ICON_DIR, ROOT)}")
        return

    # ── Real generation via the running ComfyUI server ───────────────────────────────────────────
    sys.path.insert(0, HERE)
    from backends.comfy_client import ComfyClient
    from backends import workflows as W

    with open(CONFIG, encoding="utf-8") as f:
        cfg = json.load(f)
    client = ComfyClient(comfy_root=cfg["comfy_root"])
    if not client.ping():
        print("ComfyUI is not answering on 127.0.0.1:8188 — start it first "
              "(D:\\tools\\ComfyUI_windows_portable\\run_nvidia_gpu.bat).")
        return 1

    ok, failed = 0, []
    for i, r in enumerate(rows):
        dest_dir = os.path.join(ICON_DIR, r["kind"]) if r["kind"] == "items" \
            else os.path.join(ICON_DIR, r["kind"], r["faction"])
        # Deterministic per-object seed: same object → same icon on a re-run, so a regenerated set is
        # reproducible and a single re-roll is opt-in via --seed-offset rather than reshuffling everything.
        seed = (cfg.get("hunyuan_seed_base", 1000) + i * 7919) % (2**31)
        wf = W.sdxl_concept(
            prompt=r["prompt"], negative=ICON_NEG, seed=seed,
            steps=cfg.get("concept_steps", 30), cfg=cfg.get("concept_cfg", 7.0),
            width=ICON_SIZE, height=ICON_SIZE,          # SQUARE: an icon is 1:1, unlike the tall mesh concepts
            out_prefix=f"icons/{r['faction']}_{r['id']}",
        )
        try:
            pid = client.queue(wf)
            hist = client.wait(pid, timeout=600)
            files = client.output_files(hist)
            copied = client.copy_outputs(files, dest_dir)
            if not copied:
                failed.append((r["id"], "no output file"))
                continue
            final = os.path.join(dest_dir, r["id"] + ".png")
            if os.path.exists(final):
                os.remove(final)
            os.rename(copied[0], final)
            ok += 1
            print(f"  [{ok}/{len(rows)}] {r['faction']}/{r['id']}.png  (seed {seed})")
        except Exception as e:                                     # noqa: BLE001 — report, keep batching
            failed.append((r["id"], str(e)[:120]))
            print(f"  FAILED {r['id']}: {str(e)[:120]}")

    print(f"\ngenerated {ok}/{len(rows)} icons -> {os.path.relpath(ICON_DIR, ROOT)}")
    if failed:
        print("failures:")
        for k, v in failed:
            print(f"  {k}: {v}")
    return 0 if ok else 1


if __name__ == "__main__":
    main()
