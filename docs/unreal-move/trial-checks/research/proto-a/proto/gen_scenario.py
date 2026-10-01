import json, sys
# Deterministic 500 v 500 scenario generator (prototype for the R2 report).
P1 = [("heavy_infantry",50),("infantry",150),("scout",30),("archer",120),("mage",60),("siege_engine",30),("griffin",40)]
P2 = [("bulwark",50),("footsoldier",150),("ironclad",30),("crossbowman",120),("rune_caster",60),("war_machine",30),("wyvern",40)]
W1, W2 = "worker", "forgehand"
def block(roster, sign):
    units=[]; seq=[]
    for name,n in roster: seq += [name]*n
    # 20 columns (x) by 25 rows (z), spacing 2.0; front column nearest the centre line.
    for i,name in enumerate(seq):
        col = i // 25; row = i % 25
        x = sign*(20 + 2*col); z = -24 + 2*row
        units.append({"unit_id":name,"slot":0 if sign<0 else 1,"x":x,"z":z})
    return units
units = block(P1,-1)+block(P2,+1)
for k in range(20):
    units.append({"unit_id":W1,"slot":0,"x":-94+ (k%5)*2,"z":-8+(k//5)*4})
    units.append({"unit_id":W2,"slot":1,"x": 94- (k%5)*2,"z":-8+(k//5)*4})
nodes=[]
for sx in (-1,1):
    for z in (-14,14):
        nodes.append({"x":sx*104,"z":z,"supply":600,"rate":5,"max_gatherers":4,"collection_model":"Gather","resource_type":"Ore","requires_structure_radius":15,"owner_slot":-1,"income_period_ticks":30})
d = {"id":"trial_1000","display_name":"Trial 1000","terrain_ref":"","map_bounds":120,"win_condition":"DestroyAllBuildings",
 "player_slots":[
  {"slot":0,"faction_json":"res://resources/data/factions/alpha_faction.json","start_ore":1000,"start_crystal":500,"base_x":-100,"base_z":0},
  {"slot":1,"faction_json":"res://resources/data/factions/beta_faction.json","start_ore":1000,"start_crystal":500,"base_x":100,"base_z":0}],
 "resource_nodes":nodes,
 "buildings":[{"type":"CommandCenter","slot":0,"x":-100,"z":0,"pre_built":True},{"type":"CommandCenter","slot":1,"x":100,"z":0,"pre_built":True}],
 "units":units,"triggers":[]}
json.dump(d, open(sys.argv[1],"w"), indent=1)
print(len(units),"units")
