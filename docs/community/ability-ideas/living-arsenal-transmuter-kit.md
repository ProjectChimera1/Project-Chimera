# Ability Idea — The Living Arsenal (Transmutable Weapon / Material×Shape Hero)

*Captured from brainstorm session 2026-07-15 (Alec + Claude). Second entry in the community ideas pile.*
**Status: DESIGN CAPTURE ONLY — not scheduled, nothing to implement now.**

---

## The seed (Alec's pitch)

A hero built around **one-weapon mastery** who can also:
- shatter the weapon into tiny pieces and use them however they want,
- send the weapon / pieces flying (Yondu, *Guardians of the Galaxy*),
- reforge the pieces into a **different weapon type**.

Alec's own read: the bones are good but it lacked a spine — "creativity / freedom of choice in the weapon/object." That instinct is the whole design.

## The refinement (where it landed)

The "weapon" isn't an object — it's a **material**, and the material is a **creator-editable choice** (metal, rock, water, digitized, ethereal, … more). In-match the player uses an **ability wheel**:

1. Open the wheel → pick the **shape/form** the material takes.
2. That opens a **sub-wheel** of abilities specific to that shape.
3. Slot the abilities you want into your skill slots.
4. **Re-customize live** to fit each situation — loadout-crafting *during* the match.

## The core reframe — it's a **grid**, not a menu (Material × Shape)

"Make it anything" from a giant list = paralysis. The fix that also makes it buildable: a **grid**. The *shape* says what it is; the *material* rewrites what it does. Same "wall":

| Material | "Wall" becomes |
|---|---|
| Metal | hard block, stops everyone, conducts lightning |
| Water | your units flow through, enemies crawl |
| Rock | huge HP; breaks into a rubble hazard |
| Ethereal | bodies pass, but it eats spells & arrows |
| Digital | flickers on/off on a timer, or teleports a step when hit |

A few materials × a few shapes **feels infinite but stays balanceable and actually buildable.** "Anything" comes from the *multiplication* + emergent combos, not from menu size.

## What makes it deep / skill-worthy — the spine

It needs a **cost**, or it collapses into "always pick the best answer." Six stacked skill axes (most abilities have one or two):

1. **Knowledge** — memorize the grid: which Material×Shape answers which threat.
2. **Reading** — commit to a loadout *before* you know what's coming.
3. **Nerve / tempo** — dare to stop and re-tool while exposed (and, in an RTS, while *not* commanding your army).
4. **Economy** — manage finite material, and where you draw it from.
5. **Execution** — emergent combos: metal rod in a water pool → run lightning through it → chain-shock everyone standing in the water. Not scripted — it *emerged* from properties touching.
6. **Floor vs. ceiling** — saved presets for beginners, live improv for masters.

## Signature beats worth folding in (from the first pass — optional flavor)

These came from the earlier single-weapon framing. Keep as optional mechanics, not required:
- **Build-or-spend risk curve** — mastery builds only while the material is unified/whole; shattering *spends* accumulated mastery into the swarm. Patience = a bigger payoff you can still lose by dying first.
- **Harpoon recall (the real Yondu beat)** — shards stick in enemies; recall reels them home *and drags the enemies they're stuck in*. Skewer the backline, yank the team out of position.
- **Shards as orbiting armor** — each eats one hit before shattering off.
- **Transmute anywhere** — shape the material in the field, not just on your body (cage *around* an enemy).
- **The map is your arsenal** — draw material from the terrain you stand on (rock on stone, water by water). Positioning feeds the crafting; ties to Epic 6 regions/terrain.
- **Named loadouts** — save "siege kit / duel kit / escape kit" on quick-select (same pattern as Named Cameras, Story 6.6) = the accessible floor; improv = the ceiling.
- **Counterplay** — dispersed = fragile & disarmable; re-tool / reflow = a vulnerable beat. Catch him mid-craft and you've disarmed a god.

---

## Triage (what it takes to build)

### Buildable today — Epic 2 composition
- Any **single fixed** Material+Shape as a normal hand-authored ability (a metal wall, a water slow-field, a summon-swarm). No wheel.
- Named-loadout quick-select pattern already exists (6.6 Named Cameras).

### Trigger-floor — prototypable once Epic 7 lands
- A curated "pick 1 of N preset loadouts mid-match" could be faked with triggers + variables + a simple choice UI — enough to playtest the fantasy before building the real wheel.

### New-brick requests (engine-first — the roadmap fuel)
1. **In-match radial ability-crafting wheel** — net-new UI system.
2. **Material as a property-profile primitive** — new data type (behavior / damage-type / interaction profile), creator-editable.
3. **Material × Shape resolver** — executor combines two data axes into the final effect.
4. **Hot-swap granted abilities at runtime** — slot/unslot live (abilities are near-static on a unit today).
5. **Mass / budget resource** — governs how many forms can be out at once; material sets the economy.
6. **Material interaction / emergence** — conduction, dousing, phasing, etc. *Hardest to build and balance.*
7. **Terrain-boosted material regen** — link a material's regen rate to the terrain/region the hero stands in (leans on Epic 6 regions).
8. **Drawn-trajectory input** — hand-draw a flight path per fragment, launch-as-you-finish, with earlier pieces already in flight while you draw the next (the Swarm control scheme). Net-new input mode + path/spline resolution.
9. **Preset path-shape library ("alchemist shapes")** — autodraw templates that generate all fragment paths at once; creator-authorable.
10. **Determinism note** — it all composes existing effect-bricks and path data resolves to fixed-point, so it's lockstep-safe; the cost is UI + input + hot-swap plumbing, not sim math.

## Recommended build path (when it's someday scheduled)
- **Hand-author a curated grid first** — ~4 materials × 5 shapes with tuned combos that feel incredible → prove it's fun → *then* open the wheel to creators. Do not build the infinite version first.
- **Strategic value:** this hero is "the Ability Editor in the player's hands, live" — a playable demo of the whole platform thesis. Nail one, and it's a walking advertisement for the game.

---

## Design decisions (locked 2026-07-15)

1. **Cost spine — Blend: mass budget + re-tool risk.** You can only hold so much material out at once (budget), AND re-tooling the wheel leaves you exposed for a beat. Strategic layer (what's my budget committed to?) + tactical nerve layer (dare I re-tool right now?).
2. **Material source — Hybrid, terrain-boosted regen.** You carry a capped reserve of *each* material at all times, so you're never helpless. Standing in terrain saturated with a material sharply **increases regeneration of that material** (on rock → rock refills fast; in water → water refills fast). Positioning doesn't gate access — it accelerates the *matching* resource, so you fight where your intended kit refuels. Ties into Epic 6 regions/terrain.
3. **Starting shapes — all four: Wall, Blade, Swarm, Cage.** (More can be added later; the roster is data.)
4. **Material palette — 7: metal, rock, water, digitized, ethereal, + Blood (Sanguine-themed life-cost/lifesteal) + Plant/organic (entangle/regrow).**

### Swarm control — hand-draw paths + autodraw (the headline mechanic)
The Yondu form is NOT fire-and-forget. On cast you enter a **drawing mode**:
- **Hand-draw a path per fragment.** Draw fragment 1's trajectory → it launches the instant you finish → you immediately start drawing fragment 2's path *while #1 is already in flight* → and so on. You're conducting a live swarm piece by piece, curving shots around walls and threading targets. Precision = mastery.
- **Autodraw for speed.** Instead of drawing each path by hand, slam a preset — the wheel **draws from a library of "alchemist shapes"** (transmutation-circle path templates: spiral, starburst, pincer, cross…) that auto-generate all fragment paths at once. Trade precision for tempo when you can't afford to stand and draw.
- **This mechanic *is* the floor/ceiling axis** — autodraw presets = accessible day one; bespoke hand-drawing = the skill ceiling nobody sees coming. It's the earlier "named loadouts" idea reborn as path templates.
- **Synergy with the cost spine (#1):** every second spent hand-drawing means more fragments already loose *and you standing still, exposed* — which is exactly the re-tool risk you chose. Drawing skill is literally paid for in vulnerability; the two choices reinforce each other.
- **Determinism:** a drawn path is just data (waypoints / a spline) sent as an order and resolved to fixed-point → lockstep-safe. Hand-drawn and autodraw resolve through the same path executor.

*Idea to consider (not locked): close a drawn path into a full loop and it completes a "transmutation circle" on the ground — the enclosed area detonates or becomes a hazard field. Rewards drawing skill with an alchemy-flavored payoff, and gives autodraw's circle-shapes a reason to be circles.*

## Name candidates
"The Living Arsenal" · "One Blade, Every Shape" · "The Transmuter". FMA-transmutation theme fits the existing Crucible Covenant / Sanguine Court factions.

*Add future community submissions as sibling files in this folder.*
