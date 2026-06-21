# -*- coding: utf-8 -*-
"""
Land the FMA faction redesign (The Crucible Covenant / The Sanguine Court) into the
live faction JSONs. DATA-ONLY: role ids unchanged → zero code change.
Applies: faction display_name, per-unit themed display_name + mesh_path + revised stats,
and reorders the Covenant units so the baseline Barracks/Range units sit FIRST in their
category (infantry=Transmuter before scout). Court order already correct.
Source of truth: _bmad-output/fma-faction-design.md + asset-generation-manifest.md.
"""
import json, io, sys
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

ROOT = r"D:\Projects\Project_Chimera\godot\resources\data\factions"

def mp(faction, fn):
    return f"res://assets/models/factions/{faction}/{fn}"

# (faction, unit_id) -> {field: value}.  Only changed fields listed; unchanged kept.
ALPHA_FACTION_NAME = "The Crucible Covenant"
BETA_FACTION_NAME = "The Sanguine Court"

ALPHA_ORDER = ["worker", "infantry", "scout", "heavy_infantry", "archer", "mage", "siege_engine", "griffin"]

ALPHA_UNITS = {
    "worker":        {"display_name": "Acolyte",            "mesh": "acolyte_alchemist.glb", "hp": 55,  "speed": 4.0, "mesh_scale": 0.95},
    "infantry":      {"display_name": "Covenant Transmuter","mesh": "covenant_transmuter.glb","hp": 145, "speed": 4.5, "attack_speed": 0.95},
    "scout":         {"display_name": "Quicksilver Runner", "mesh": "quicksilver_runner.glb","hp": 70,  "speed": 6.5, "attack_damage": 7, "attack_speed": 1.3, "vision_range": 12.0},
    "heavy_infantry":{"display_name": "Bulwark Adept",      "mesh": "bulwark_adept.glb",    "hp": 270, "speed": 3.0, "attack_speed": 1.4, "cost_crystal": 0},
    "archer":        {"display_name": "Pierce Marksman",    "mesh": "pierce_marksman.glb",  "hp": 85,  "speed": 4.0, "attack_range": 6.5, "attack_speed": 0.85},
    "mage":          {"display_name": "Circle Savant",      "mesh": "circle_savant.glb",    "hp": 65,  "speed": 3.5, "attack_speed": 1.9, "cost_crystal": 0, "splash_radius": 1.5},
    "siege_engine":  {"display_name": "Crucible Mortar",    "mesh": "crucible_mortar.glb",  "hp": 330, "speed": 2.2, "attack_speed": 3.8},
    "griffin":       {"display_name": "Greycrest, the Bonded","mesh": "greycrest_bonded.glb","hp":190, "speed": 6.5, "attack_speed": 1.1, "cost_crystal": 0, "vision_range": 15.0},
}
ALPHA_BLDG = {
    "command_center": {"display_name": "Covenant Sanctum",     "mesh": "covenant_sanctum.glb"},
    "barracks":       {"display_name": "Crucible Hall",        "mesh": "crucible_hall.glb"},
    "archery_range":  {"display_name": "Sigil Foundry",        "mesh": "sigil_foundry.glb"},
    "siege_workshop": {"display_name": "Transmutation Forge",  "mesh": "transmutation_forge.glb"},
}

BETA_UNITS = {
    "forgehand":   {"display_name": "Cinderhand Thrall",  "mesh": "cinderhand_thrall.glb", "hp": 80},
    "footsoldier": {"display_name": "Maul-Fused Wretch",  "mesh": "maul_fused_wretch.glb", "hp": 130, "speed": 3.4, "cost_ore": 70, "mesh_scale": 0.95},
    "bulwark":     {"display_name": "Slag Bulwark",       "mesh": "slag_bulwark.glb",      "hp": 240, "speed": 2.8, "supply": 2},
    "ironclad":    {"display_name": "Pride Colossus",     "mesh": "pride_colossus.glb",    "hp": 340, "armor_type": "Heavy", "supply": 3, "cost_ore": 250, "mesh_scale": 1.3},
    "crossbowman": {"display_name": "Bolt Penitent",      "mesh": "bolt_penitent.glb",     "hp": 120, "speed": 2.8},
    "rune_caster": {"display_name": "Cinder Cantor",      "mesh": "cinder_cantor.glb",     "hp": 110, "splash_radius": 1.5},
    "war_machine": {"display_name": "Render Crawler",     "mesh": "render_crawler.glb",    "hp": 480, "splash_radius": 4.0},
    "wyvern":      {"display_name": "Envy Wraithwing",    "mesh": "envy_wraithwing.glb",   "hp": 300, "speed": 4.8},
}
BETA_BLDG = {
    "command_center": {"display_name": "The Sanguine Furnace", "mesh": "sanguine_furnace.glb"},
    "barracks":       {"display_name": "The Thrall Yards",     "mesh": "thrall_yards.glb"},
    "archery_range":  {"display_name": "The Bolt Sanctum",     "mesh": "bolt_sanctum.glb"},
    "siege_workshop": {"display_name": "The Render Works",     "mesh": "render_works.glb"},
}

def apply(faction, entry, patches):
    p = patches.get(entry["id"])
    if not p:
        return []
    changes = []
    for k, v in p.items():
        if k == "mesh":
            old = entry.get("mesh_path"); new = mp(faction, v)
            if old != new: changes.append(f"mesh_path: {old.split('/')[-1]} -> {v}")
            entry["mesh_path"] = new
        else:
            old = entry.get(k, "(new)")
            if old != v: changes.append(f"{k}: {old} -> {v}")
            entry[k] = v
    return changes

def patch_file(path, faction, fac_name, units, bldg, order=None):
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    print(f"\n=== {path} ===")
    old_fac = data.get("display_name")
    data["display_name"] = fac_name
    print(f"faction display_name: {old_fac} -> {fac_name}")
    for u in data["units"]:
        for c in apply(faction, u, units):
            print(f"  [{u['id']}] {c}")
    for b in data["buildings"]:
        for c in apply(faction, b, bldg):
            print(f"  [{b['id']}] {c}")
    if order:
        before = [u["id"] for u in data["units"]]
        data["units"].sort(key=lambda u: order.index(u["id"]) if u["id"] in order else 99)
        after = [u["id"] for u in data["units"]]
        if before != after:
            print(f"  REORDER units: {before} -> {after}")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"  WROTE OK ({len(data['units'])} units, {len(data['buildings'])} buildings)")

patch_file(ROOT + r"\alpha_faction.json", "alpha", ALPHA_FACTION_NAME, ALPHA_UNITS, ALPHA_BLDG, ALPHA_ORDER)
patch_file(ROOT + r"\beta_faction.json", "beta", BETA_FACTION_NAME, BETA_UNITS, BETA_BLDG)
print("\nDONE.")
