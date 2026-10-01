#!/usr/bin/env python3
"""Generate the Unreal trial scenario (plan A section 3.3): alpha (slot 0) versus beta (slot 1), flat ground,
map_bounds 120, CommandCenters at x = -100 / +100, two ore nodes a side (four in total, at x = +-104, z = +-14; plan
section 3.3 and R2 say "four a side", the prototype that F19 pins has two), N/2 units a side (20 workers + the rest
combat). The layout is the one of research/proto-a/proto/gen_scenario.py: with the default --units 1000 the output is
byte-identical to that prototype's trial_1000.json (A4 acceptance compares them).

Usage: python gen_trial_1000.py <out.json> [--units 1000] [--id trial_1000]

Combat roster: the prototype's seven unit types per side in the prototype's proportions (480 per side at 1000 units),
scaled by largest-remainder rounding for other sizes; at most 20 columns, max(25, ceil(n/20)) rows, spacing 2.0, front column
nearest the centre line; rows are centred on z = 0 (z = -(rows-1) + 2*row, which is the prototype's -24 + 2*row for
25 rows). Output is LF-only, indent 1, no trailing newline (json.dump), so the file hash is the same on every OS.
"""
import argparse
import json
import sys

# (unit id, count at the 1000-unit scale)
P1 = [("heavy_infantry", 50), ("infantry", 150), ("scout", 30), ("archer", 120), ("mage", 60),
      ("siege_engine", 30), ("griffin", 40)]
P2 = [("bulwark", 50), ("footsoldier", 150), ("ironclad", 30), ("crossbowman", 120), ("rune_caster", 60),
      ("war_machine", 30), ("wyvern", 40)]
W1, W2 = "worker", "forgehand"
WORKERS_PER_SIDE = 20
COLUMNS = 20
MIN_ROWS = 25


def scaled_roster(roster, combat_total):
    """Scale the roster's counts to sum to combat_total (largest remainder; identity when the sum already matches)."""
    base = sum(n for _, n in roster)
    if base == combat_total:
        return list(roster)
    exact = [n * combat_total / base for _, n in roster]
    counts = [int(e) for e in exact]
    order = sorted(range(len(roster)), key=lambda i: (-(exact[i] - counts[i]), i))
    for i in order[: combat_total - sum(counts)]:
        counts[i] += 1
    return [(roster[i][0], counts[i]) for i in range(len(roster))]


def block(roster, sign):
    seq = []
    for name, n in roster:
        seq += [name] * n
    rows = max(MIN_ROWS, (len(seq) + COLUMNS - 1) // COLUMNS)
    units = []
    for i, name in enumerate(seq):
        col = i // rows
        row = i % rows
        # the prototype fills a column at a time, 25 rows deep (480 units = 19.2 columns): col = i // rows, row = i % rows
        x = sign * (20 + 2 * col)
        z = -(rows - 1) + 2 * row
        units.append({"unit_id": name, "slot": 0 if sign < 0 else 1, "x": x, "z": z})
    return units


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("out")
    ap.add_argument("--units", type=int, default=1000, help="total units, both sides (default 1000)")
    ap.add_argument("--id", default="trial_1000")
    a = ap.parse_args()
    per_side = a.units // 2
    if a.units % 2 or per_side <= WORKERS_PER_SIDE:
        sys.exit("--units must be even and above %d" % (2 * WORKERS_PER_SIDE))
    combat = per_side - WORKERS_PER_SIDE
    units = block(scaled_roster(P1, combat), -1) + block(scaled_roster(P2, combat), +1)
    for k in range(WORKERS_PER_SIDE):
        units.append({"unit_id": W1, "slot": 0, "x": -94 + (k % 5) * 2, "z": -8 + (k // 5) * 4})
        units.append({"unit_id": W2, "slot": 1, "x": 94 - (k % 5) * 2, "z": -8 + (k // 5) * 4})
    nodes = []
    for sx in (-1, 1):
        for z in (-14, 14):
            nodes.append({"x": sx * 104, "z": z, "supply": 600, "rate": 5, "max_gatherers": 4,
                          "collection_model": "Gather", "resource_type": "Ore", "requires_structure_radius": 15,
                          "owner_slot": -1, "income_period_ticks": 30})
    d = {"id": a.id, "display_name": "Trial " + str(a.units),
         "terrain_ref": "", "map_bounds": 120, "win_condition": "DestroyAllBuildings",
         "player_slots": [
             {"slot": 0, "faction_json": "res://resources/data/factions/alpha_faction.json", "start_ore": 1000,
              "start_crystal": 500, "base_x": -100, "base_z": 0},
             {"slot": 1, "faction_json": "res://resources/data/factions/beta_faction.json", "start_ore": 1000,
              "start_crystal": 500, "base_x": 100, "base_z": 0}],
         "resource_nodes": nodes,
         "buildings": [{"type": "CommandCenter", "slot": 0, "x": -100, "z": 0, "pre_built": True},
                       {"type": "CommandCenter", "slot": 1, "x": 100, "z": 0, "pre_built": True}],
         "units": units, "triggers": []}
    with open(a.out, "w", newline="\n") as f:
        json.dump(d, f, indent=1)
    print(len(units), "units")


if __name__ == "__main__":
    main()
