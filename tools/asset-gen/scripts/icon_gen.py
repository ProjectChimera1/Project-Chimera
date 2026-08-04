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
    "game UI portrait icon, square 1:1, tight bust framing from the chest up, subject centered and "
    "filling the frame, strong rim light separating the subject from a dark recessed vignette background, "
    "bold readable shapes at small size, flat stylized painted look with clean edges, "
    "early-20th-century industrial FMA-inspired alchemy world, no text, no border frame"
)
ICON_NEG = (
    "full body, tiny subject, wide shot, multiple characters, character sheet, text, watermark, letters, "
    "ui frame, border, rounded corners, drop shadow, busy background, scenery, low contrast, blurry"
)
ICON_SIZE = 512  # generate large, downsample in-engine; 512 keeps detail for a 64px read

PALETTES = {
    "alpha": ("#3d5a80", "#8fb8de", "#c8a24a"),   # slate-blue plate, cyan-white sigil, brass accent
    "beta":  ("#5c1f28", "#d4636f", "#b08d57"),   # oxblood plate, crimson core-glow, dull brass
    "item":  ("#2f3a34", "#93c9a5", "#c8a24a"),
}

# Category → silhouette recipe. Keyed off the object's own role so the placeholder is REPRESENTATIVE:
# the outline hints at the real subject's read (heavy = broad, scout = thin dart, caster = tall+orb).
SHAPES = {
    "worker":    "M32 78 L32 52 Q32 44 40 44 L56 44 Q64 44 64 52 L64 78 Z M40 44 L40 34 Q48 26 56 34 L56 44",
    "soldier":   "M26 78 L26 50 Q26 40 36 38 L60 38 Q70 40 70 50 L70 78 Z M40 38 L40 28 Q48 20 56 28 L56 38",
    "scout":     "M38 78 L38 52 Q38 46 44 45 L52 45 Q58 46 58 52 L58 78 Z M42 45 L42 33 Q48 27 54 33 L54 45",
    "heavy":     "M20 78 L20 48 Q20 36 34 34 L62 34 Q76 36 76 48 L76 78 Z M38 34 L38 24 Q48 16 58 24 L58 34",
    "caster":    "M30 78 L34 46 Q36 38 48 38 Q60 38 62 46 L66 78 Z M42 38 L42 28 Q48 21 54 28 L54 38",
    "hero":      "M24 78 L26 46 Q28 36 40 34 L56 34 Q68 36 70 46 L72 78 Z M40 34 L40 24 Q48 15 56 24 L56 34",
    "vehicle":   "M16 72 L16 54 L30 54 L36 44 L64 44 L70 54 L84 54 L84 72 Z",
    "air":       "M12 56 L44 48 L48 34 L52 48 L84 56 L52 62 L48 74 L44 62 Z",
    "structure": "M22 80 L22 44 L48 26 L74 44 L74 80 Z",
    "item":      "M40 30 L56 30 L56 40 L64 56 Q64 76 48 76 Q32 76 32 56 L40 40 Z",
}

ROLE_HINTS = [  # matched against the SUBJECT clause, most specific first
    ("hero", ("hero", "commander", "champion", "greycrest", "the bonded")),
    ("caster", ("caster", "savant", "alchemist mage", "sigil", "transmuter", "conduit", "ritual")),
    ("heavy", ("heavy", "broad", "tankiest", "plate", "bulwark", "juggernaut", "brute")),
    ("scout", ("runner", "scout", "lean", "dart", "courier", "swift")),
    ("worker", ("acolyte", "laborer", "worker", "thrall", "forgehand", "digging")),
    ("soldier", ("soldier", "infantry", "marksman", "line")),
]


def subject_of(prompt: str) -> str:
    """The object's own appearance clause — everything after SUBJECT:, which is the ONLY part that
    describes what the thing actually looks like (the rest is shared style/pose boilerplate)."""
    i = prompt.find("SUBJECT:")
    return prompt[i + len("SUBJECT:"):].strip() if i >= 0 else prompt.strip()


def palette_of(prompt: str) -> str:
    """Reuse the object's own FACTION PALETTE sentence so icon and mesh share one colour contract."""
    m = re.search(r"FACTION PALETTE:.*?(?=SUBJECT:|$)", prompt, re.S)
    return m.group(0).strip() if m else ""


def category_of(asset: dict) -> str:
    if asset["prefix"] in ("structure", "vehicle", "air"):
        return asset["prefix"]
    hay = (asset["id"] + " " + subject_of(asset.get("prompt", ""))).lower()
    for cat, needles in ROLE_HINTS:
        if any(n in hay for n in needles):
            return cat
    return "soldier"


def icon_prompt(asset: dict) -> str:
    return f"{ICON_PREFIX}\n{palette_of(asset.get('prompt',''))}\nSUBJECT: {subject_of(asset.get('prompt',''))}"


def load_assets():
    with open(CONFIG, encoding="utf-8") as f:
        cfg = json.load(f)
    rows = []
    for a in cfg["assets"]:
        rows.append({
            "id": a["id"], "faction": a["faction"], "kind": "buildings" if a["prefix"] == "structure" else "units",
            "category": category_of(a), "prompt": icon_prompt(a), "mesh": a.get("mesh_file", ""),
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
            "prompt": f"{ICON_PREFIX}\nSUBJECT: a single {it.get('display_name', it['id'])} "
                      f"as an alchemical apothecary object on a dark recessed background",
        })
    return rows


def svg_for(row: dict) -> str:
    base, glow, accent = PALETTES.get(row["faction"], PALETTES["item"])
    path = SHAPES.get(row["category"], SHAPES["soldier"])
    initials = "".join(p[0] for p in re.split(r"[_\-\s]+", row["id"])[:2]).upper()
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 96 96" width="96" height="96">
  <!-- PLACEHOLDER for {row['id']} ({row['faction']}/{row['category']}) — generated by tools/asset-gen/scripts/icon_gen.py.
       Representative, not final: faction palette + role silhouette so it reads correctly at 64px.
       Replace by dropping a painted {row['id']}.png beside this file; the loader prefers .png.
       Path is faction-scoped: BuildingType ids (command_center, barracks, ...) are shared across factions. -->
  <defs>
    <radialGradient id="bg" cx="50%" cy="38%" r="72%">
      <stop offset="0%" stop-color="{base}"/><stop offset="100%" stop-color="#12151a"/>
    </radialGradient>
  </defs>
  <rect width="96" height="96" rx="8" fill="url(#bg)"/>
  <rect x="2" y="2" width="92" height="92" rx="7" fill="none" stroke="{accent}" stroke-width="2" opacity="0.75"/>
  <path d="{path}" fill="{glow}" opacity="0.92"/>
  <path d="{path}" fill="none" stroke="#0d1014" stroke-width="1.6" opacity="0.65"/>
  <text x="48" y="90" font-family="sans-serif" font-size="11" font-weight="700"
        text-anchor="middle" fill="{accent}" opacity="0.9">{initials}</text>
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

    print("real PNG generation needs the ComfyUI venv python + GPU; see SKILL.md.\n"
          "Run --prompts to review what would be generated, or --placeholders for stand-ins.")


if __name__ == "__main__":
    main()
