# Project Chimera — The Final Product

### Every editor: how it's made, what you can do inside it, how far it goes, and where the AI fits

*Written 2026-07-13. Sources: the GDD, the epics backlog (post-renumber 2026-07-13), and the as-built record through Epic 5. This describes the **1.0 product** — each section carries a status tag so you always know what exists today vs. what's still on the backlog.*

---

## 1. What Project Chimera is

Project Chimera is **not an RTS game — it's the machine that makes RTS games.** It ships looking like one: a polished strategy game where the Crucible Covenant (fast, fragile alchemists who pay for power with their own vitality) fights the Sanguine Court (slow, immortal legions that grow stronger as things die around them). But those two factions aren't hand-coded specials. They were built with the same in-game editors that ship to every player. They're the demo of the machine.

The platform serves three kinds of people:

- **Commanders** — pure players. They browse community scenarios, click Play, and never see an editor. (Most players — roughly 99.8% by Fortnite Creative's numbers.)
- **Architects** — builders. They sculpt maps, invent units, wire game logic, and publish.
- **Tinkerers** — both. They play a lot, then push the tools to their limits.

The north-star test, straight from history: the Warcraft III World Editor birthed DotA and Tower Defense as genres. Chimera's goal is to be the modern, AI-accelerated version of that editor — approachable enough that your first playable scenario takes under 15 minutes, deep enough that the next DotA could come out of it.

Everything below answers three questions the project filters every feature through: **does it make an RTS easier to create, easier to share, or more exciting to discover?**

---

## 2. The machinery under every editor (read this once — it explains all of them)

Every editor in Chimera is built the same way. Understand this section and each editor becomes "the same trick, pointed at different data."

### Everything is a recipe card

A unit, an ability, a building, a faction, a trigger, a map — each one is a small **text file of plain data** (JSON). Think of it as a recipe card: "Footsoldier: 120 health, sword damage, moves at speed 5, costs 50 ore, trained at the Barracks." The game engine is the kitchen — it cooks *whatever the cards say*. It has no idea what a "Footsoldier" is; it just reads cards and runs them.

**Why this matters to you:** the editors are just friendly forms that write recipe cards. Nothing a creator makes is "code." That's what makes the whole platform possible for non-programmers — and it's also the security model (a recipe card can't contain a virus; a script could).

### The proofreader at the door (validation)

Every card, no matter who wrote it — you, another player, or the AI — passes through a strict **validator** before the game will touch it. It's fail-closed: if anything is wrong, the card is rejected *entirely*, and the editor points at the **exact field with the exact problem** ("`attack_cooldown` on line 3: must be greater than zero"), not a vague "something's wrong." You physically cannot save a broken unit, launch a broken faction, or load a corrupted scenario into a match.

**Why it matters:** this is the reason AI generation and stranger-made downloads are safe. AI output and human output walk through the same door, past the same guard.

### The sacred rule: perfect sync (determinism)

Chimera's multiplayer works like chess-by-mail: computers don't send the game state to each other, only the *orders* ("move these units there"). Every player's machine then simulates the identical match independently. For that to work, every machine must compute **exactly** the same result, down to the last decimal — every time, on every CPU.

That one requirement shapes everything:

- The simulation uses **fixed-point math** (whole-number arithmetic that never rounds differently on different machines) instead of normal decimals.
- Content vocabulary is **closed** — a fixed menu of known effects, events, and actions the validator can check, rather than open-ended scripts that could run forever or differently per machine.
- Anything cosmetic (particles, sounds, camera shake, UI) is walled off in a separate "presentation" layer that **provably cannot touch** the simulation. Your explosion effect can be as wild as you want; it can't desync a match.
- Every piece of match state is folded into a running **checksum** — a fingerprint computed every couple of seconds. If two machines ever disagree, the game knows instantly and exactly when. There are 23+ recorded "golden" matches that are replayed byte-for-byte by automated tests on every code change, on both Windows and Linux.

**Why it matters to you as a creator:** it's why the editors give you a rich *menu* instead of a blank *scripting box*. That's a deliberate trade — WC3-class expressiveness, zero ability to crash or desync someone else's match.

### The ladder of depth (layered complexity)

Every editor exposes the same four rungs, and you can hop between them freely:

1. **Presets** — pick "targeted heal" from a dropdown, tune two numbers, done.
2. **Forms** — sliders, dropdowns, and fields for every stat, each with a tooltip.
3. **Graphs / advanced composition** — visually snap building blocks together for multi-part logic.
4. **Raw JSON escape hatch** — flip the recipe card over and edit the text directly. The same validator still guards the save, and the friendly view updates to match.

The AI (section 12) is an accelerator that can write drafts at any rung — never a fifth rung of its own.

### One consistent body (the shared kit)

All editors are built from one **design-system component kit** — the same styled buttons, sliders, cards, tabs, confirm-dialogs, tooltips, and red "this field is wrong" badges everywhere. Every control shows a tooltip on hover or keyboard focus (a lesson taken from Dreams). Learn one editor and you've learned the muscle memory for all of them.

### The instant playtest loop

Press **F5** at any moment: your scenario — the map you just painted, the unit you just retuned — launches straight into a real match. No export, no restart, no loading project files. Press F5 again and you're back in the editor, state reset cleanly. Mario Maker proved this loop is the single most important feature of any creation tool, and Chimera treats it as sacred. (Built — Story 3.10.)

### Why adding new capabilities stays cheap

Because everything is data + validator + form, extending the platform follows one pattern: **add one new brick, and every surface gets it.** A new effect type or trigger action is added once to the closed registry, and the rules require it to appear simultaneously in: the validator, the runtime, the sentence-builder UI, the visual graph palette, *and* the AI's generation schema. It's a checklist, not an engineering project. That's the honest answer to "how easy is it to add more imaginative capabilities" — for the developer: days, not months, per brick; for creators: every brick ever added multiplies what all four rungs of the ladder can express.

---

## 3. The Unit Card Editor — *(BUILT — Epic 3)*

**What it is:** the single place where a unit — or a hero — is born and tuned. One card shows *everything* about one unit: stats, combat profile, model, abilities, tags, hero progression. This is a direct lesson from history: WC3 put all unit data in one panel and was beloved; StarCraft 2 scattered it across five cross-referencing editors and it was "the single biggest reason people gave up." Chimera follows WC3: **one entity, one view.**

**How it's made, in plain words:** the card is a form bound to one recipe card (a `UnitDefinition` in the faction's file). The left side renders a live, slowly-rotating 3D preview of the unit's actual model in a miniature viewport. The right side is grouped stat controls from the shared kit. When you hit Save, a surgical file-writer changes *only the values you touched* in the faction file — your comments, formatting, and everything else in the file survive byte-for-byte. Every save passes the validator first.

**What you can do inside it, live:**

- **Create, edit, duplicate, and delete units** with a toolbar; deletes ask for confirmation; **Ctrl+Z / Ctrl+Y** undo and redo edits.
- **Tune every stat** with sliders and fields: health, attack, attack range, attack speed (cooldown — the burst-vs-sustain knob), armor value, move speed, costs (in any resources the scenario defines), build time, supply cost, vision range, collision size, push/yield priority.
- **Pick the combat identity:** a damage type and armor type from the 5×6 counter matrix (spear-vs-armor rock-paper-scissors, itself data you can retune per scenario), splash radius for area damage, and **hitscan vs. projectile** delivery with projectile speed (projectiles can miss moving targets — micro potential).
- **Compose, don't program:** pick exactly one of six archetypes — Worker, Melee, Ranged, Siege, Air, Structure — then attach any abilities and behaviors on top. A healer isn't a "healer class"; it's *Ranged + a heal ability + support behavior*. A scout is *Melee + fast + big vision*. Invalid combinations are rejected with a badge at the offending field.
- **Tag units** (organic / mechanical / magical) so abilities and counters can target categories ("deals double damage to mechanical").
- **Assign the look:** browse for a 3D model (GLB file) and watch the preview swap instantly on a turntable; or explicitly choose the box placeholder and dress it later. A broken model file falls back to the box with a warning badge — never a crash.
- **Flip the Promote-to-Hero switch:** hero-only fields unfold — a leveling curve, XP-gain rules, and signature + ultimate ability slots. Flip it off and the unit is cleanly ordinary again. Heroes then actually *run* at match time: kill-credit XP, level-ups with stat growth, death and revival.
- **Author what persists:** a per-scenario **persistence manifest** — checkboxes for which attributes (hero level, XP, items, currency) carry forward into the next game. This is WC3's beloved save/load-code system, minus the codes: players get a visual **hero picker** with slot cards (portrait, level, signature ability) and Deploy / Overwrite / Delete. Offline it's a local save; online it's stored server-side so it can't be tampered with.
- **Press F5** and fight with the unit immediately. If the card has an error, the editor blocks the playtest and shows you where.

**How far can you go?** Any unit that can be described as *stats + archetype + abilities* — which covers essentially the entire RTS/AoS/TD unit canon: vampiric melee bruisers, fragile glass-cannon artillery, flying transports, aura-carrying banner units, self-damaging berserkers, hero-killers, walking buildings. The ceiling is the ability vocabulary (next section) — a unit can't do a *verb* that no ability primitive expresses. New stats are cheap to add (one field + one validator rule + one control); the archetype set is deliberately fixed at six, because every archetype multiplies the test surface.

**The AI's part:** describe a unit in plain language — "a slow, heavily armored flame turret that's weak to air" — and get a complete draft card: stats, name, lore, suggested composition. It lands as ordinary editable data in this same editor, walks through the same validator, and is yours to retune or discard. The AI can also **balance-check** your roster (section 12).

---

## 4. The Ability Editor — *(BUILT — Epic 2)*

**What it is:** the forge for everything units *do* beyond auto-attacking — fireballs, heals, auras, poisons, buffs, self-sacrifice mechanics. This is the editor that decides how much personality your game can have, so it got the deepest possibility space of the entity editors.

**How it's made, in plain words:** inside the engine there's a small, closed vocabulary of **effect building blocks** — think LEGO bricks. Bricks like: *change health directly* (damage or heal), *apply a modifier* (a timed stat change, damage-over-time, heal-over-time, slow, stun-class effects), *search an area* (find everyone in a radius, filtered by ally/enemy/tag), *run a sequence* (do A, then B, then C), *persist* (repeat an effect every N ticks for a duration), plus cosmetic bricks (play effect / sound / shake camera) that can't touch the simulation. An ability is a small tree of these bricks plus a price tag (energy cost, health cost, cooldown) and a targeting mode. The executor walks the tree with hard depth and size caps, in deterministic order. The editor is a friendly front-end that assembles those trees.

**What you can do inside it, live:**

- **Simple mode:** pick a preset — *targeted damage*, *targeted heal*, *buff* — set two or three numbers (amount, range, cooldown, cost), save. It's attachable to a unit and castable in a match immediately, without ever seeing what's underneath.
- **Advanced mode:** compose multi-effect graphs. The showcase example: *pay 30 of my own health → search 8 units around me → heal all allies found.* That's three bricks snapped together, and it's the Covenant's signature "Equal Exchange" mechanic — authored, not coded.
- **Passive mode:** abilities with no cast button. Three families, each picked from a closed trigger list:
  - **Auras** — "allies within 10 get +2 armor while I'm alive" (re-granted each period, removed when they leave or you die).
  - **On-hit riders** — "when my attack lands, also apply a 3-second burn."
  - **Permanent self-modifiers** — "this unit regenerates 2 health per second, forever." (That's the Court's "Sanguine Furnace" trickle — again: authored content, not engine code.)
- **Set the price:** energy/mana costs, cooldowns, and — unusually — **health self-costs**, because the showcase faction demanded it, so every creator gets it.
- **Attach the juice:** every ability (and unit) carries a **combat feedback profile** — hit particles, impact sound, screen shake, hit-freeze, death effect. A tuned "feels good" default ships; override any of it per ability. Feedback provably never touches the simulation — a hit-freeze pauses the *picture*, never the match.
- **Raw JSON hatch** with round-trip guarantee: edit the text, re-parse, identical graph.
- Save is **blocked on invalid configurations** with the error located to the exact brick and field.

**How far can your imagination go — honestly?** The possibility space is *combinatorial*: every brick multiplies against every other. Drain-life (damage + self-heal in sequence), area slows, poison clouds (persistent area damage), rage mechanics (raise own damage at a health price), on-hit chain riders, stacking auras, periodic self-regeneration — all reachable today by composition. Two genuinely asymmetric factions were built on it as proof.

The honest ceiling: **you can't invent a brand-new brick from inside the editor.** If a fantasy needs a verb the vocabulary lacks (say, "teleport target across the map" or "mind-control"), that brick must be added to the engine first. That's the determinism trade, and it's also where extension is cheapest: one new primitive added to the registry (validator + executor + editor UI + AI schema — the checklist from section 2) instantly becomes a new LEGO piece for *every* creator and *every* rung of the ladder. Scenario-level logic ("when any unit dies anywhere, do X for the whole army") lives one floor up, in the Trigger system — abilities are per-unit; triggers are per-world. Death-triggered designs (martyrdom: die → heal everyone around you) land there too, because the on-death event belongs to the trigger layer's vocabulary (Epic 7). The two floors share the same effect bricks, so learning transfers.

**The AI's part:** ability drafts from plain language, same as units — with a special design intent on record for this editor: the AI shows its **translation work in front of you**. You type "a healing prayer that's stronger the more wounded the target is," and it rephrases into the real fields — showing which of your words became which knobs — then offers confirm / reroll / variants. The AI is a translator into the brick vocabulary, never a bypass around it.

---

## 5. The Item Card Editor — *(BUILT — Epic 3)*

**What it is:** the consolidated card editor for items — the potions, blades, and artifacts that make hero-centric maps (the DotA/RPG family) work — plus the shop and inventory systems that deliver them.

**How it's made, in plain words:** an item is the smallest recipe card in the game: name, icon, cost, charges, a bundle of stat modifiers, and optionally a reference to an ability effect (a health potion is "charges: 3, effect: heal 150"). The item editor is the Unit Card pattern shrunk to fit. Shops aren't special buildings — any building can be flagged `sells_items` with a stock list, which keeps shops fully creator-definable.

**What you can do inside it, live:**

- **Create / edit / duplicate / delete items** on a card: name, icon, cost, charges, stat deltas (+damage, +armor, +speed...), and an effect reference for usable items. Dangling references (an effect or icon that doesn't exist) are rejected with a located message.
- **Flag any building as a shop** and author its stock list; set the shop radius in data.
- **Watch it work in-match:** select a hero near the shop and the shop panel lists items with cost and stock. Purchases ride the real command pipeline with ownership and affordability checks — meaning item-buying is multiplayer-safe and cheat-checked by construction, exactly like ordering a unit around.
- **Player-side inventory UI:** a grid on the hero panel with charge counts, tooltips, and use-hotkeys.
- **Persist items across games** by ticking inventory in the persistence manifest — the hero picker then shows the carried items on the hero's slot card.

**How far can you go?** Consumables, stat-stick equipment, charge-limited scrolls, shop-economy maps, quest-reward artifacts carried across a campaign of custom games. The ceiling matches the ability editor's (an item's active effect is an ability-vocabulary tree). Recipes/item-combining and equip-slot restrictions aren't in the 1.0 card — they're the kind of brick that gets added to the schema later, cheaply.

**The AI's part:** item drafts ride the same entity-draft framework (name/lore/stats from a prompt, validated, editable), and balance analysis can flag cost-effectiveness outliers ("this +10 damage item costs less than your +5 one").

---

## 6. The Building Editor — *(BUILT — Epic 4; one map-side gap closes in Epic 6)*

**What it is:** the authoring panel for everything static — town halls, barracks, supply structures, shops, and any production or tech building your faction needs.

**How it's made, in plain words:** same pattern as the Unit Card — a form over a `BuildingDefinition` recipe card — presented as a **right-dock inspector**: click a building anywhere it appears (list, or a node in the tech-tree graph) and its properties open in a dock on the right. Saves go through the same canonical file-writer and validator. The panel only ever writes definition data; it can't poke the live simulation.

**What you can do inside it, live:**

- **Create and edit buildings:** health, armor, footprint, construction cost — as a **cost map over whatever resources the scenario defines** (not hardcoded to Ore/Crystal), construction time, supply bonus (how much army cap it grants), and which unit category it produces.
- **Make it a producer:** buildings gate and train units; production queues, rally points, and train buttons on the in-match command card all follow from data.
- **Make it a shop** (`sells_items`, from the item editor) or a **research host** (list which upgrades it offers — section 7).
- **Inline validation:** negative costs, blank or duplicate IDs, dangling references — rejected at the field, never written to disk.
- **Raw-JSON hatch** with the simple cards reflecting your text edits on reload.
- **Place it and fight:** buildings authored here place in the map editor and in-match via worker construction (ghost preview, ore deduction, construction timer, growing-mesh animation).

**One honest today-gap:** stock building types place everywhere already; placing *arbitrary brand-new* building definitions on maps still runs through a legacy fixed-list gate that Epic 6 (Story 6.8) retires, keyed to stable authored IDs. Authoring is done; universal placement is the next epic. Also queued behind this: WC3-style **neutral buildings** you can use/claim on the map (mercenary camps, neutral shops) — the decision is on record, the mechanic follows the building editor.

**How far can you go?** Any economy/production skeleton: multi-tier bases, defensive structures (a tower is a Structure archetype with an attack), supply chains, faction-unique tech buildings, shop networks. Together with per-resource collection models (next section) you can express StarCraft-, Age-of-Empires-, or Total-Annihilation-shaped economies. The ceiling: buildings are static in 1.0 (no flying/walking buildings), and footprints stay axis-aligned.

**The AI's part:** building drafts arrive inside AI faction drafts (a generated faction proposes its building set), and balance analysis covers costs and tech pacing.

---

## 7. The Tech Tree Editor — *(BUILT — Epic 4)*

**What it is:** the visual map of your faction's progression — what unlocks what — drawn as a graph instead of typed as lists.

**How it's made, in plain words:** every building (and research) already carries a `prerequisites` list on its recipe card. The tech-tree editor renders those cards as **nodes in a tier-laned graph** (tier 1 lane, tier 2 lane...) and turns editing the lists into a physical act: **drag a wire from one node's out-port onto another node** and the prerequisite is written; delete the wire and it's gone. The same right-dock inspector from the building editor opens when you click any node, so stats and progression are edited in one place. It's the same trick as everything else — a friendlier pen for the same recipe cards.

**What you can do inside it, live:**

- **Wire prerequisites by dragging.** The runtime then enforces *exactly what you drew*: a unit or building stays unbuildable (its train button dimmed, with the reason shown) until its prerequisite stands complete.
- **Get instant sanity checks:** wiring a loop (A requires B requires A) is rejected on the spot, at drop, with the same rule the file-loader uses — you can't even draw an impossible tree.
- **Author research/upgrades in the same graph:** research nodes drag into the dependency chains exactly like buildings. Each research has per-level costs and times, **repeatable levels** ("Forged Blades 1/2/3"), and per-level stat deltas.
- **See research run in-match:** command-card research buttons show cost/time/level and dim with the reason when unaffordable or prereq-missing; in-progress research shows a progress bar; completion fires a chime and toast. Upgrades apply as permanent faction-wide modifiers to every current *and future* unit — a soldier trained ten minutes later is born already upgraded, deterministically. Cancelling refunds a configurable fraction.
- **Round-trip guarantee:** save, reload, and the graph redraws with the same nodes, lanes, and wires; the raw-JSON hatch shows the matching arrays.

**How far can you go?** Any DAG-shaped progression: wide flat trees (everything early, AoE-style), deep narrow ladders (SC-style tiers), diamond-shaped choice trees, research-gated superweapons, repeatable incremental upgrades. Ceiling: prerequisites are "building exists / research completed" conditions — richer gating ("requires 2 of X," "requires hero level 5") belongs to the trigger layer or a future brick.

**The AI's part:** faction drafts propose a full tree; balance analysis reads it ("your tier-3 unlocks 2 minutes before your opponent's — intended?").

---

## 8. The Faction Definer — *(BUILT — Epic 5)*

**What it is:** the capstone wizard that assembles everything you've authored — units, heroes, abilities, buildings, tech — into a complete, playable faction. Also the proof of the whole thesis: both shipped factions are valid Definer outputs.

**How it's made, in plain words:** a five-step guided flow, each step a form writing one section of a `FactionDefinition` recipe card, with the full faction validator run at the end. It can't produce a broken faction — the finish button is physically gated on a clean validation pass.

**What you can do inside it, live:**

1. **Name & color** — team-color swatches are colorblind-safe by design (the Okabe-Ito palette) and each faction gets a distinguishing glyph, so "red vs blue" never excludes anyone.
2. **Unit roster** — fill role slots (worker, fighters, ranged, and optional extras) from your authored unit library.
3. **Buildings & tech** — attach the building set and the tech tree you drew.
4. **Starting conditions** — starting resources and starting unit list.
5. **AI preset** — pick how the computer plays this faction (aggressive / balanced / defensive / passive presets), so *any* faction is instantly playable against and by the AI. A faction can't save without one.

Plus: **hero & persistence configuration** surfaced in the flow, an **advanced raw-JSON mode** guarded by the same validator, and located errors that point at the exact step and field when something's dangling.

- **The ≤12-minute promise:** the full simple-mode flow, first faction, no JSON, is designed and acceptance-tested to finish in twelve minutes or less.
- **Instant payoff:** the moment you save, your faction **appears in the playtest and skirmish pickers — no restart, no file copying.** Assign it to any of up to 4 player slots and fight it, with it, or watch two AIs pilot it. A broken faction file found on disk shows as non-selectable with the reason, so a bad card can never launch a match.
- **Invisible to players:** none of this surfaces to a Commander who just wants to play — creation UI is opt-in, never in the way of the game.

**How far can you go?** The showcase sets the bar deliberately high: two factions that don't just have different stats but different *shapes of play* (blitz-and-burst vs. attrition-and-regeneration), built from shared parts. Asymmetry, mono-unit meme factions, hero-only rosters, tower-defense "factions" that are mostly buildings — all expressible. Ceiling: 1.0 ships and validates 2 showcase factions + your authored ones at up to 4 players per match (8-player is the first post-1.0 bump).

**The AI's part:** full **faction drafts** — prompt "a swarm faction of cheap, fast, fragile insects that wins by numbers" and receive a complete editable proposal (roster, stats, names, lore) through the same wizard-and-validator path. Balance analysis then stress-reads the whole faction. The **AI preset** itself is the other AI: the deterministic opponent brain (section 12).

---

## 9. The Map & Terrain Editor — *(PARTIALLY BUILT — sculpt/paint/placement work today; Epic 6, next up, hardens it and adds the World-Editor parity floor)*

**What it is:** the world-builder — terrain, textures, resources, start positions, regions, decorations, cameras, and win conditions. This is where a *scenario* physically lives.

**How it's made, in plain words:** the ground is a professional 3D terrain system (Terrain3D — GPU-driven, the same class of tech commercial games use) that Chimera drives at runtime with brush tools. Everything placed *on* the terrain goes through a palette-and-ghost system: pick a thing, a translucent preview follows your cursor, click to stamp it into the scenario's recipe card. The map, like everything else, is data — heightmap + texture maps + a list of placed things — bundled into the scenario package.

**What you can do inside it today, live:**

- **Sculpt with brushes:** raise, lower, smooth, flatten — adjustable size and strength, painting directly on the world in real time.
- **Paint biomes:** four texture layers (grass/dirt/rock/snow class), swappable per map.
- **Place everything with ghost previews:** units for any player, buildings, resource nodes (with per-node supply and gather-rate spinners), and start-position flags (with per-slot starting ore). Grid-snap toggles with G.
- **Set the win condition** from a panel; **delete** anything with full undo; **Ctrl+Z/Y across every placement action.**
- **F5 into a real match** on the map at any moment.

**What Epic 6 adds (the very next work), live:**

- **The headline fix — persistence:** sculpted height and painted textures currently reset on reload (they live only in memory); 6.2 writes real terrain data into the map package so a saved map reopens *identical*, survives packaging, and produces the identical walkable surface every load.
- **Terrain stroke undo/redo** unified with placement undo.
- **High ground that matters:** the simulation gains per-unit elevation sampled from your heightmap, plus an optional per-map toggle that grants elevated units bonus vision — deterministic, checksum-covered high-ground play.
- **Regions:** draw and name rectangular areas — the backbone primitive of WC3-style custom maps — with a toggleable overlay. Triggers bind to them ("when a unit enters *NorthPass*...") and King-of-the-Hill binds to a drawn zone.
- **Impassable terrain:** paint unwalkable cells (with the classic pathability overlay to inspect them), optional automatic blocking on steep slopes — real chokepoints, walls, and cliffs instead of open fields.
- **Doodads & props:** a starter decoration library, placed/rotated/scaled with variation, rendered dirt-cheap (instanced), optionally flagged to block pathing.
- **Editor power tools:** marquee multi-select with shift-add, group move/delete/duplicate, **copy-paste** with relative offsets preserved, and step-rotation of placements.
- **Named cameras** (position/target/zoom presets) for cinematic triggers, and **water volumes** (visual plane + auto-blocked cells).
- **A real New-Map flow:** name, author, description, suggested players, map size from a supported set; **2–4 start positions**; properties editable later; an **auto-generated minimap preview** that follows the map into the skirmish screen, lobby, and content browser.
- **Custom buildings placeable** (the 6.8 gate-retirement from section 6).

**How far can you go?** Any map the RTS camera can love: symmetric 1v1 ladders, 4-player FFAs, chokepoint sieges, high-ground king maps, decoration-heavy story sets with named cameras for cinematics, TD lanes built from impassable paint, water-bounded islands. Ceiling at 1.0: map sizes come from a supported set (grid systems underneath are dimension-coupled — sizes are validated combinations, not a free slider); regions are rectangles; rotation is cosmetic; 2–4 start positions (8 post-1.0).

**The AI's part:** **AI map generation is already live** in a first form — describe a map and get a generated, validated layout (it can't load unless it passes the same scenario validation as a hand-built map). Epic 8 re-points it at your chosen AI provider and removes the RTS-only training wheels (unit-count/slot-count clamps become scenario-type parameters), so generated non-RTS scenarios stop being wrongly rejected. The GDD's full vision is generate → rule-based feature placement (resources, chokes, symmetry) → optional AI refinement pass suggesting balance/flow improvements.

---

## 10. The Trigger Editor — the Scenario Logic System — *(CORE EXISTS, THE BIG BUILD IS EPIC 7 — this is the "build any game" epic)*

**What it is:** the brain of every scenario — the system that turns a map with units into a *game* with rules, story beats, waves, objectives, and custom win conditions. It's the spiritual successor to the WC3 Trigger Editor, deliberately stopped short of StarCraft 2's fatal complexity. Today a basic version runs (events → conditions → actions, evaluated deterministically every tick, already driving win conditions and AI-generated triggers); Epic 7 rebuilds it into the full four-tier language below.

**How it's made, in plain words:** a trigger is a sentence written from menus: **"WHEN** [something happens] **IF** [these things are true] **THEN** [do these things]." Under the hood, all your logic — every tier — compiles into **one shared representation**: a typed graph of known node types (the same closed-vocabulary trick as abilities, one floor up). Because there's exactly one representation, the four ways of authoring are just four *views* of the same thing, and switching views never converts or loses anything. The whole graph is statically checkable: the validator proves your logic can't loop forever, can't reference things that don't exist, can't exceed its budget — *before* the match starts. There is deliberately no script box anywhere.

**The four tiers, from zero effort to full depth:**

- **Tier 1 — Presets (zero-code):** pick a complete objective from a dropdown and fill in blanks. Six ship: Annihilation, Eliminate-All-Units, **King of the Hill** (hold a drawn region for N seconds — contested = no progress), **Timed Survival**, **Assassination** (protect/kill a leader), **Landmark Destruction**. Each is itself built from the public trigger vocabulary — presets are shortcuts, not secret engine magic. Bad parameters (an Assassination with no leader picked) are caught at load with a pointed error.
- **Tier 2 — the ECA sentence list (low-code):** the WC3-style dropdown sentence builder. The GDD's bet, from editor history: this tier handles ~90% of what custom scenarios need.
- **Tier 3 — the visual node graph (medium-code):** the same logic as draggable nodes and typed wires (wire color = data type), with validation errors drawn *on the offending node*. Node positions are cosmetic and never affect the logic's fingerprint. Round-trip T2→T3→T2 is lossless by construction.
- **Tier 4 — natural language (AI):** type "when the player captures the northern outpost, spawn 20 soldiers from the eastern forest and play a war horn" → a draft trigger appears **in the editor, for your review**, referencing your actual region/unit names — never activated without your confirmation. Section 12 covers the guardrails.

**The vocabulary you get at 1.0** (each item authorable in all tiers):

- **Events:** unit dies (with killer credit), unit damaged, unit trained, ability cast, hero levels up, building completed, timer expires, region entered/left, resource threshold, player chat commands ("-give 100"), match start, and **custom events you define yourself**.
- **State & data:** typed variables (numbers, fixed-point decimals, booleans, unit references, faction references, points, timers, arrays) with three scopes — global, per-player, trigger-local. Full arithmetic and boolean logic (`+ − × ÷`, comparisons, AND/OR/NOT with grouping), built-ins like count/distance/min/max, and **live state reads**: an entity's health/position/owner, a player's unit count (filterable by tag), resource amounts, units-in-region counts.
- **Actions:** spawn units, order units around (move/attack-move/patrol a selection chosen by region+owner+filter), set variables, display messages, victory/defeat per player, create timers, play sound/effect, **move the camera** to named cameras with cinematic letterboxing, show/complete/fail **objectives**, enable/disable/run other triggers, weighted **random choice** (seeded — replays stay identical), and loops (**ForEach** over a snapshot of a group, batched variants for big sets).
- **Custom events** deserve a highlight: define your own named events with typed payloads, raise them from one trigger, subscribe handlers in others — real modular architecture for big maps (and the mechanism behind faction mechanics like the Court's on-death feast). The system proves your event web is loop-free at load, and a bounded next-tick queue exists for legitimate state machines.
- **Objectives & briefings:** an authored objective list drives an in-match quest log with toasts, and every match opens on a briefing surface (map name, objectives, faction blurb). Scenarios with only a preset auto-emit a default objective — a player is never left guessing the goal.
- **Debugging, live in playtest:** toggle an overlay showing live variable values, a tick-stamped log of exactly which triggers fired, per-trigger fire counters, and enabled states — then click a log entry to jump to that trigger in the editor. No more guess-and-replay.

**Why the guardrails exist (the honest ceiling):** no unbounded `while` loops, no recursion, no arbitrary scripts — every construct is provably bounded so a downloaded scenario can never hang, crash, or desync a multiplayer match, and the server can validate any content before hosting it. Budgets are generous and validated against real content, and a fuel meter backs the whole thing as a deterministic seatbelt. Within those walls, the vocabulary is expressly designed to cover the WC3-class custom-map canon — and the 3-mission campaign that ships with the game is **built in this editor** as the dogfood proof: if the trigger system can't express the campaign, the trigger system isn't done.

**Extending it:** one new event/condition/action node = one registry entry, and the definition of done *requires* it to appear in the sentence list, the graph palette, and the AI schema simultaneously. Vocabulary grows in lockstep across all tiers forever.

**The AI's part:** Tier 4 above — plus the AI's system prompt is generated from the live schema, so every new vocabulary brick is automatically something the AI can write tomorrow.

---

## 11. The Custom Runtime UI Builder — *(EPIC 7, alongside triggers)*

**What it is:** the tool that lets a scenario ship its **own screen furniture** — a tower-defense wave counter, an RPG dialog box, a shop panel, a scoreboard, a voting prompt — so custom games stop looking like "an RTS with extra steps."

**How it's made, in plain words:** a drag-and-drop canvas (locked to a 16:9 safe area so it works on any monitor) with a closed palette of widgets: Panel, Label, Counter, Progress Bar, Timer, Leaderboard, Floating Text, Item List, **Button**. You pin widgets with 9-point anchoring, and — the key move — **bind them to your trigger variables** with `{curly-brace}` references. The widget tree is data in the scenario card, checked by the same validator (bindings must resolve, caps on widget count/nesting).

**What you can do, live:**

- **Read rail:** a Counter bound to `{wave_number}` updates the instant your trigger increments it; a Leaderboard sorts itself off per-player variables; a Timer renders a deterministic countdown as mm:ss. Formatting happens entirely on the display side — the simulation never touches a string, which is why custom UI *cannot* desync a match.
- **Write rail:** **Buttons raise your custom events back into the game** — and they ride the same networked command path as unit orders, with sim-side authorization of who may press what. That's what makes "everyone votes on the next wave," "buy from this shop panel," and "press to start the boss" work identically in single-player, multiplayer, and replays.
- **Local-only buttons** (open/close a panel) come from a whitelist that's *proven* unable to touch game state — cosmetic UI stays free.
- **Trigger-gated visibility:** show the boss health bar only during the boss fight.

**How far can you go?** Any HUD a widget vocabulary can compose — which covers the TD/AoS/RPG staples the platform targets. Ceiling: widgets are the fixed palette (no embedded web pages, no free drawing); new widget types are — say it with me — one new brick in the registry.

**The AI's part:** indirect at 1.0 — the AI writes the trigger logic and variables your UI binds to. AI-drafted UI layouts are a natural post-1.0 extension of the entity-draft framework.

---

## 12. The AI, everywhere — the full picture *(EPIC 8 + the deterministic opponent)*

Two completely different things called "AI" live in Chimera. Keeping them straight explains every design decision.

### A. The creation AI (a large language model — your co-author)

**Your key, your choice, your machine if you want.** AI features run through one provider abstraction with three adapters: **Anthropic (Claude)**, **OpenRouter** (one key, many models), or **local Ollama** — a model running free and offline on your own PC. You pick provider and model in settings (curated lists plus a free-text override); keys live in a git-ignored local secret store that provably never ships inside a build; a test-connection button and four clear status states (no provider / no key / unreachable / output-failed-validation) mean you always know why something isn't working. **The entire suite is fully usable with AI off** — every AI affordance degrades to the manual editor beside it.

**The iron rule — drafts, not dictats.** Everything the AI produces is ordinary recipe-card data that:
1. walks through the **same fail-closed validator** as your hand-typed work (AI authoring is *no more dangerous* than human authoring, by construction),
2. appears **for your review and editing before it's applied** — nothing is ever auto-applied,
3. is normal, reopenable, editable data forever after — never a locked black box,
4. gets its decimals quantized into the deterministic number format before saving, so an AI-touched scenario replays and syncs exactly like any other.

**What the AI does in each editor:**

| Editor | The AI's role |
|---|---|
| **Trigger Editor** | Tier 4: plain English → draft trigger, referencing your actual entities/regions (fuzzy-matched with "did you mean" fixes), previewed in the editor for confirm/edit. The prompt schema is generated from the live vocabulary, so new constructs are automatically writable. |
| **Map Editor** | Generate whole map layouts from a description; validated before load; scenario-type-aware limits (non-RTS map shapes stop being clamped to RTS rules). |
| **Unit Card** | Full unit drafts — stats, name, lore — expressed as archetype + ability composition, never bespoke hacks. |
| **Ability Editor** | Ability drafts, with the show-your-work translation UX: your words → the real fields, visibly mapped, confirm/reroll/variants. |
| **Hero / Faction Definer** | Complete hero and faction drafts (roster, stats, names, lore) into the same wizard + validator path. |
| **Balance (any content)** | On request: analysis of a faction or scenario returning **field-level suggestions with rationale** ("Bulwark: cost 90→110 — its effective HP per ore is 38% above roster average") that you apply, edit, or discard one by one. Nothing mutates behind your back. |
| **Names & lore, everywhere** | Constrained JSON generation for flavor text at any field. |

**What the AI is structurally forbidden from doing:** bypassing validation, writing directly to disk, touching a running match. LLMs are slow and non-repeatable — two players' machines could never agree on one — so **no LLM output ever executes inside the deterministic simulation.** That single wall is why Chimera can be AI-soaked at authoring time and rock-solid at play time.

### B. The opponent AI (not an LLM — a deterministic brain)

The thing you fight is a **utility AI**: every action it could take (expand, build supply, add production, launch an attack) gets a score from the live game state each tick, highest score wins. It's pure arithmetic — which means it's fast, tunable, and **runs identically on every machine**, so AI players are legal in lockstep multiplayer (the float→fixed-point conversion that makes this fully true is scheduled work, Story 10.11). Difficulty levels adjust its thresholds and reaction delays; the release-readiness epic adds **pattern-tracking adaptation** (it counts your rushes and turtles and weights counter-strategies), a debug overlay showing its decision scores in real time, and AI-fill for any open skirmish slot. Creators touch it through the **AI preset** step of the Faction Definer — pick aggressive/balanced/defensive/passive per faction, and any authored faction is instantly playable by the computer.

### C. The asset AI (the developer-side pipeline)

The showcase art itself is AI-generated under human curation: 3D models from Hunyuan3D/Tripo-class tools (local, ~30s per model), 2D portraits/icons from Stable-Diffusion-class tools with a trained style adapter, all funneled through one style prefix, a shared material library, and a unifying cel-shade pass so hundreds of generated assets read as one game. At 1.0 this is the developer's pipeline (and the reason a solo dev can art a whole game); exposing generation directly to end users in-app is an explicit post-1.0 stretch goal. What creators *do* get at 1.0 is the **Import Manager** (next section) — bring any model/image/sound you made anywhere, including with these same tools.

---

## 13. The Import Manager — custom art, sound, and the sharing loop *(EPIC 12 + EPIC 9)*

**What it is:** WC3 Import Manager parity — the door through which your own `.glb` models, `.png` images, and `.ogg` sounds enter a scenario, plus the machinery that makes shared content "just work" for everyone who downloads it.

**What you can do, live:**

- **Import** models/images/audio with a browsable preview list (model turntable, image view, audio play). Files are checked against sanity caps (triangle counts, texture sizes, audio length, package size) with actionable rejections. A one-time **"I have the rights to distribute this"** attestation per package keeps the IP posture honest.
- **Assign anywhere stock assets go:** unit and building models (with team tint), command-card icons, portraits, hero-picker art, combat sounds, projectile visuals. A failed model ingest falls back to the placeholder with a warning — never a crash.
- **Package:** everything bundles into one `.chimera.zip` — terrain, factions, entities, tech, triggers, custom UI, assets, thumbnails — with a content hash covering every byte.
- **Publish to mod.io** (the cross-platform mod backend) from inside the game, gated by two things: a **quality gate** (thumbnail, description, screenshot) and the Mario-Maker-inspired **proof-of-play gate — you must beat your own scenario before you may publish it.** You own your content, full stop; the platform takes only the right to host and distribute it (the explicit anti-Reforged clause).
- **Discover in-game:** a content browser with search, tags, sorting, ratings, one-click subscribe — and in multiplayer lobbies, version mismatches resolve with a one-click **"Update Required"** download that hash-verifies before the match can start. Players never manually manage mod files.

---

## 14. How far can imagination go? — the whole-platform answer

**The recipe box at 1.0.** Compose from: any economy (N resources; gather / flat-income / stand-and-stream collection; structure-gated extraction; optional upkeep) + any roster (6 archetypes × abilities × tags × heroes × items) + any progression (tech DAGs, repeatable research) + any world (sculpted terrain, high ground, chokepoints, regions, props, cameras) + any rules (four-tier triggers, custom events, custom UI, six win-condition presets or fully custom victory) + persistence across games. That box demonstrably holds:

- **Tower defense** — income economy, wave loops on timers, lane chokepoints from pathability paint, a custom wave-counter HUD, Timed Survival victory.
- **Hero arenas / the DotA shape** — heroes, XP, items, shops, custom events, teams, per-player elimination.
- **RPG / quest maps** — objectives and quest log, dialog via messages and custom UI panels, persistent heroes carried across a hand-built campaign of scenarios (the WC3 save-code tradition, minus the codes).
- **Survival / horde, King-of-the-Hill, regicide, escort, cinematic story missions** — all preset- or vocabulary-native.
- **Asymmetric-economy skirmish variants** — one faction gathers, the other streams; the showcase factions already prove asymmetric *identity*.
- **Autochess-style experiments** — bounded array pools and batched loops exist precisely for this class of design.

**The wall, stated honestly.** You cannot write arbitrary code, and that is a feature: it's the exact property that makes every downloaded scenario safe to run, every AI draft safe to accept, and every multiplayer match desync-proof. The practical ceiling is the vocabulary — and the vocabulary is the part designed to grow forever at low cost (**one brick → every editor tier + the AI schema, by rule**). 1.0's other honest edges: 4 verified players (8 is the first fast-follow), rectangular regions, cosmetic rotation, fixed map-size set, no anti-maphack (lockstep shows the map to a determined cheater, exactly as in WC3/SC — server-side command validation *is* in), and imported models render static (no custom animations at 1.0).

**When something's impossible today,** the extension path is always the same short walk: new stat → new field + validator rule + control; new verb → new registry brick; new widget → new palette entry; new genre lever (a resource model, a persistence attribute) → data + one system change. The platform was shaped so that "more imagination" is a content-team-sized task on a one-person team — because the one person has the other AI (the one writing this document) building it with him.

---

## 15. Where it stands today (2026-07-13)

| Piece | Status |
|---|---|
| Deterministic engine, lockstep MP foundation, CI determinism gates | ✅ Built (Epic 1) |
| Effect engine, modifiers, **Ability Editor** (active + passive), combat feedback | ✅ Built (Epic 2) |
| **Unit Card Editor**, heroes (XP/levels/death/revival), items + shops + inventory, persistence manifest + hero picker, F5 Edit↔Play, design system | ✅ Built (Epic 3) |
| **Building Editor**, **Tech-Tree Editor**, research, N-resource economy + collection models | ✅ Built (Epic 4) |
| **Faction Definer wizard**, showcase factions landed + playtest-validated asymmetric, "Your First Scenario" onboarding | ✅ Built (Epic 5) |
| **Map & Terrain Editor** to ship bar (persistence fix, regions, pathability, props, high ground, New-Map flow) | 🔜 Epic 6 — next up (8 stories; 4 small Epic-14 remediation stories recommended first) |
| **Trigger DSL four tiers + Custom Runtime UI + debugging + objectives** | 🔜 Epic 7 (15 stories) |
| **AI-assisted creation** (provider stack, drafts, balance analysis) | 🔜 Epic 8 (5 stories) |
| Match shell (skirmish setup, pause, save/load, alerts, score screens) | 🔜 Epic 11 — sequenced before Epic 9 |
| Share & discover (mod.io publish/browse), MP at scale (≤4 verified, teams, replays, matchmaking, online hero rail) | 🔜 Epic 9 (16 stories) |
| Release readiness (real art + audio, performance to 500–2,000 units, balance to 45–55%, accessibility, Linux, Steam + DRM-free) | 🔜 Epic 10 |
| **Import Manager** + MP content sync | 🔜 Epic 12 |
| 3-mission prologue campaign, built in the creation suite (dogfood) | 🔜 Epic 13 |

75 stories remain across Epics 6–14. Five epics and ~65 stories are already behind us — including, notably, **every entity editor**. The product from here is: the world tools, the logic tools, the AI layer, the sharing loop, and the polish to ship.

---

*Companion documents: `Project_Chimera_GDD.md` (full design spec), `_bmad-output/planning-artifacts/epics.md` (every story + acceptance criteria), `_bmad-output/fma-faction-design.md` (showcase faction design).*
