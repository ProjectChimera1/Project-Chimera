# -*- coding: utf-8 -*-
"""Build the structured asset manifest (chimera_assets.json) from compact data.
Source of truth for prompts = _bmad-output/asset-generation-manifest.md (themed).
NOTE: buildings use a 3/4-ISOMETRIC SOLID-MASSING concept prompt — flat/top-down/facade concepts
produce thin flat meshes in Hunyuan (observed in the first batch)."""
import json, os

OUT = r"D:\Projects\Project_Chimera\tools\asset-gen\config\chimera_assets.json"

UNIT_PREFIX = ("clean low-poly RTS unit, flat-shaded single-material albedo (engine applies a global "
    "cel-shade, do not add PBR/specular/roughness/normal-map detail), readable silhouette for a distant "
    "top-down camera, early-20th-century industrial/European-military FMA-inspired alchemy world, neutral "
    "relaxed idle pose facing +Z (A-pose, arms slightly away from the body, NOT a rigid T-pose), plain "
    "white background, origin centered at the feet/base on the +Z axis.")
# Buildings: force a SOLID 3D structure seen at a 3/4 high angle (NOT top-down / floor plan / facade).
STRUCTURE_PREFIX = ("clean low-poly RTS building game asset, a SINGLE three-dimensional structure shown from a "
    "3/4 high-angle isometric perspective with clear volumetric massing — visible roof, walls, and depth, like "
    "an Age of Empires / Warcraft building render, flat-shaded single-material albedo (engine applies a global "
    "cel-shade, no PBR/specular/normal-map detail), early-20th-century industrial/European-military FMA-inspired "
    "alchemy world, the whole building resting on the ground, plain white background.")
VEHICLE_PREFIX = UNIT_PREFIX.replace(
    "neutral relaxed idle pose facing +Z (A-pose, arms slightly away from the body, NOT a rigid T-pose)",
    "static neutral orientation, barrel/front facing +Z (no biped pose)")
AIR_PREFIX = UNIT_PREFIX.replace(
    "origin centered at the feet/base on the +Z axis",
    "origin centered under the body as if hovering low, no ground contact")

PREFIXES = {"unit": UNIT_PREFIX, "structure": STRUCTURE_PREFIX, "vehicle": VEHICLE_PREFIX, "air": AIR_PREFIX}

PALETTES = {
    "alpha": ("FACTION PALETTE: slate-blue greatcoats and leather, matte brass-colored automail accents "
        "(used sparingly), chalk-white transmutation sigils that flare cyan-white; flat color blocks, no implied metal gloss."),
    "beta": ("FACTION PALETTE: oxblood-crimson cloth, flat black-iron and brass blocks, faint crimson alchemic "
        "Core-glow as emissive accent, gothic-uncanny homunculus mood; flat color blocks, no implied metal gloss."),
}

NEGATIVE = ("high-poly, photorealistic, PBR, specular highlights, glossy or metallic reflections, normal-map or "
    "rivet/crack micro-detail, baked shadows, ground plane, base or pedestal, scenery, multiple characters, cropped "
    "or missing limbs, text, watermark, logo, signature, busy background, motion blur, depth of field, dramatic "
    "cinematic lighting, off-center framing, action or dynamic pose, character sheet, reference sheet, model sheet, "
    "turnaround, multiple views, multiple poses, item studies, equipment studies, weapon studies, panels, insets, "
    "grid layout, collage, design sheet, props, duplicate")
# Extra negatives for buildings — kill the flat/top-down/emblem failure modes.
BUILDING_NEG = (", top-down view, overhead map, floor plan, blueprint, schematic, flat facade, single wall, "
    "wall panel, elevation drawing, emblem, seal, medallion, circular design, mandala, 2D, orthographic, flat slab, "
    "tile, rug, coin")

CHAR_SUFFIX = ("(single full-body character, ONE subject only, isolated and centered on a plain seamless white "
    "background, full figure from head to feet, straight-on front view).")
STRUCT_SUFFIX = ("(a solid 3D building with real depth and a roof, seen at a 3/4 aerial isometric angle; NOT "
    "top-down, NOT a floor plan or map, NOT a flat facade or wall, NOT an emblem or seal or medallion, NOT a 2D "
    "drawing; ONE isolated building centered on plain white).")

# (id, faction, prefix, tri_kind, mesh_file, mesh_scale, subject)
ASSETS = [
    # ---- Crucible Covenant (alpha) ----
    ("worker", "alpha", "unit", "unit", "acolyte_alchemist.glb", 0.95,
     "a slight young acolyte in a knee-length slate-blue work coat with rolled sleeves, one simple brass automail forearm, chalk satchel and a small pick at the hip; compact unarmed silhouette clearly the smallest humanoid; a faint chalk-white circle glows on the open palm."),
    ("infantry", "alpha", "unit", "unit", "covenant_transmuter.glb", 1.0,
     "a standing soldier in a belted slate-blue greatcoat over a leather cuirass, etched chalk bracers, a short straight sword at the hip, hands meeting over a small glowing chalk-white circle; bulk the shoulders and pauldrons so this is the widest of the Covenant humanoids; baseline soldier read."),
    ("scout", "alpha", "unit", "unit", "quicksilver_runner.glb", 0.9,
     "a lean runner in a cropped slate-blue jacket and tight leggings, a long scarf swept hard to one side and a light dagger; low crouched stance with no shoulder bulk so the outline is a thin dart shape; chalk-white speed-streak sigils glow along the boots."),
    ("heavy_infantry", "alpha", "unit", "unit", "bulwark_adept.glb", 1.2,
     "a broad heavily-built soldier in matte brass-and-iron plate over a slate-blue underlayer, large slab pauldron, heavy two-handed maul held head-down; bulky wide top-heavy silhouette, clearly the tankiest human; one chalk-white circle glowing at a shoulder seam."),
    ("archer", "alpha", "unit", "unit", "pierce_marksman.glb", 0.95,
     "a poised marksman in a long slate-blue duster and leather bandolier, peaked cap and goggles, a long brass-fitted bolt-action rifle held horizontally across the body; tall slim vertical figure that reads as ranged; chalk-white sigil glows at the rifle breech."),
    ("mage", "alpha", "unit", "unit", "circle_savant.glb", 1.0,
     "a robed scholar in a flowing hooded slate-blue coat with wide split sleeves, no automail, a large glowing chalk-white transmutation circle held symmetrically between two outstretched hands; distinctive floating-circle silhouette and trailing hem; thin and unarmored."),
    ("siege_engine", "alpha", "vehicle", "unit", "crucible_mortar.glb", 1.8,
     "a two-wheeled brass-and-iron mortar carriage with a stubby upward-angled barrel feeding from a glowing crucible furnace at the rear, slate-blue plating, hand-crank; low wide wheeled machine silhouette, clearly mechanical and much bigger than infantry; chalk-white sigils ring the barrel mouth."),
    ("griffin", "alpha", "air", "unit", "greycrest_bonded.glb", 1.4,
     "a noble eagle-lion chimera with broad smooth rounded feathered wings, a lion's hindquarters, one brass automail foreleg-talon, a slim slate-blue saddle-harness with a chalk-white brand on the shoulder; instantly-readable rounded winged silhouette; alert and loyal, not monstrous."),
    ("command_center", "alpha", "structure", "building", "covenant_sanctum.glb", 3.0,
     "a stout fortified chapel-workshop of slate-blue stone with a steep slate roof, a tall central clocktower-spire, brass-pipe chimneys and telegraph wires, arched doors with a glowing chalk-white sigil banner over the entrance; clearly the largest, tallest friendly structure, civic and protective."),
    ("barracks", "alpha", "structure", "building", "crucible_hall.glb", 2.5,
     "a long low slate-blue barracks hall with a pitched roof and an arched drilling-yard gateway, a banner of crossed chalk and sword over the door, brick chimney; squat rectangular military building, distinctly lower than the spired command center."),
    ("archery_range", "alpha", "structure", "building", "sigil_foundry.glb", 2.5,
     "an open-fronted slate-blue range building with a tall slatted firing canopy roof, hanging brass lanterns, a stacked row of target butts along one side; horizontal open-air building with a distinctive overhanging slatted roof."),
    ("siege_workshop", "alpha", "structure", "building", "transmutation_forge.glb", 2.8,
     "a heavy industrial forge-hall of slate-blue brick with a tall brick smokestack and a large arched vehicle bay, glowing orange crucible light spilling from the bay, brass gauges on the walls; bulky workshop building with the tall chimney as its read-at-distance marker."),
    # ---- Sanguine Court (beta) ----
    ("forgehand", "beta", "unit", "unit", "cinderhand_thrall.glb", 1.0,
     "a stooped hollow-eyed laborer in a soot-stained leather apron, one crude brass prosthetic ending in a digging claw, a dim red Core-ember glowing through a chest vent; hunched, the smallest and simplest silhouette so it reads as a non-combatant worker."),
    ("footsoldier", "beta", "unit", "unit", "maul_fused_wretch.glb", 0.95,
     "a gaunt fused thrall-beast in a tattered oxblood coat, an iron half-mask and one over-grafted brute arm ending in a fused cleaver-maul, faint red Core-light bleeding from coat seams; lean asymmetric humanoid-beast silhouette, rank-and-file, slightly inhuman in outline."),
    ("bulwark", "beta", "unit", "unit", "slag_bulwark.glb", 1.0,
     "a broad squat humanoid whose left side is a massive fused slab of dark iron forming a built-in tower shield, the other arm a stubby crushing maul; a single broad glowing crimson seam across the slab; wide low blocky silhouette that reads as a wall on legs."),
    ("ironclad", "beta", "unit", "unit", "pride_colossus.glb", 1.3,
     "a towering armored homunculus in flat black-iron plate over a crimson tabard, an uncanny too-perfect masked face, a heavy gauntleted fist, regal stance, a bright red Core glowing through a breastplate gap; the largest, most imposing humanoid silhouette, an elite head-and-shoulders above the rest."),
    ("crossbowman", "beta", "unit", "unit", "bolt_penitent.glb", 0.95,
     "a hooded thrall whose forearms are bolted into a bulky black-iron repeating crossbow held at the hip, heavy shoulder plates, dim red Core-glow at the throat; distinct wide horizontal weapon silhouette separating it from the bare-handed melee bodies."),
    ("rune_caster", "beta", "unit", "unit", "cinder_cantor.glb", 1.0,
     "a tall robed thrall in a hooded oxblood greatcoat with one bare arm raised, a single glowing red transmutation disc held off to that one raised hand (asymmetric, one-sided), an uncanny serene masked face, a bright Core at the sternum; slender silhouette defined by the off-side floating disc."),
    ("war_machine", "beta", "vehicle", "unit", "render_crawler.glb", 1.8,
     "a massive low many-legged crawling siege furnace, a fat upward-angled mortar barrel over a glowing red boiler-belly, flat black-iron plating and brass gauges, vents leaking crimson light; the single largest and lowest silhouette, clearly a vehicle not a humanoid."),
    ("wyvern", "beta", "air", "unit", "envy_wraithwing.glb", 1.4,
     "a stitched flying chimera with wide ragged asymmetric bat-leather wings with trailing tatters, an elongated fanged maw, faint extra human-like faces fused into a crimson-glowing torso; broad torn-wing silhouette that reads instantly as airborne."),
    ("command_center", "beta", "structure", "building", "sanguine_furnace.glb", 3.0,
     "a tall gothic foundry-cathedral of black iron and brass with a great central furnace-chimney pouring red light, ringed by smaller smokestacks, tall arched doors with a crimson sigil over them; the largest, most vertical and ornate building."),
    ("barracks", "beta", "structure", "building", "thrall_yards.glb", 2.5,
     "a long low fortified hall of dark iron and stone with a pitched roof, barred pens along one side and a wide arched mustering gate stained crimson; broad horizontal building, lower than the central furnace cathedral."),
    ("archery_range", "beta", "structure", "building", "bolt_sanctum.glb", 2.5,
     "a narrow tall vaulted chapel-armory of black iron with a steep roof and tall slit firing-windows, racks of crimson-glowing quarrels along the front wall; upright slender building distinct from the squat barracks."),
    ("siege_workshop", "beta", "structure", "building", "render_works.glb", 2.8,
     "a massive heavy-industrial foundry building of black iron with an overhead gantry crane, oversized brass boiler tanks and a wide vehicle-bay door glowing red inside; the bulkiest, most mechanical building with a clear vehicle exit."),
]

def concept_size(prefix):
    return [832, 1216] if prefix in ("unit", "air") else [1024, 1024]

assets = []
for (aid, fac, prefix, tri_kind, mesh, scale, subject) in ASSETS:
    suffix = STRUCT_SUFFIX if prefix == "structure" else CHAR_SUFFIX
    neg = NEGATIVE + (BUILDING_NEG if prefix == "structure" else "")
    full_prompt = f"{PREFIXES[prefix]} {PALETTES[fac]} SUBJECT: {subject} {suffix}"
    assets.append({
        "id": aid, "faction": fac, "prefix": prefix, "tri_kind": tri_kind,
        "mesh_file": mesh, "mesh_scale": scale,
        "dest": f"godot/assets/models/factions/{fac}/{mesh}",
        "concept_size": concept_size(prefix),
        "prompt": full_prompt, "negative": neg,
    })

doc = {
    "project_root": "D:/Projects/Project_Chimera",
    "comfy_root": r"C:\Vid-Pic Gen Dump from C Drive\AI Video Generation\ComfyUI_windows_portable_nvidia\ComfyUI_windows_portable\ComfyUI",
    "tri_target": {"unit": 6000, "building": 10000},
    "concept_steps": 30, "concept_cfg": 7.0,
    "hunyuan_seed_base": 42, "max_rerolls": 4,
    "assets": assets,
}
os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w", encoding="utf-8") as f:
    json.dump(doc, f, indent=2, ensure_ascii=False)
print(f"WROTE {OUT}: {len(assets)} assets ({sum(1 for a in assets if a['prefix']=='structure')} buildings with 3/4-isometric prompt)")
