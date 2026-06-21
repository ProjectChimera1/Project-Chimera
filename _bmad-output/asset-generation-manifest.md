# Project Chimera — Asset Generation Manifest

> **Theme:** *The Law of Equal Exchange* — a Fullmetal-Alchemist-VIBE universe (original, trademark-safe).
> Two factions: **The Crucible Covenant** (Rebel Alchemists, `alpha`) vs **The Sanguine Court**
> (Homunculus Legion, `beta`). World bible + faction design: `_bmad-output/fma-faction-design.md`.
>
> **Purpose:** the exact list of art/audio files the game needs, with locked generation prompts, for
> Alec's **local AI asset-generation pipeline** (tooling + setup: `_bmad-output/local-asset-pipeline-research.md`).
> **Current state: 0 committed GLBs, 0 audio, 0 textures** — the game runs entirely on procedural
> box/placeholder fallbacks, so this whole list is open work.
>
> **Engine-side context (already architected):** assets load data-driven via `UnitDefinition.mesh_path`
> (verified field name); a missing/invalid file → box placeholder (never a crash). Shipped/non-editor
> builds load `.glb` via **`GLTFDocument.AppendFromFile` → `GenerateScene`** (architecture addendum P2),
> and the **art-style consistency layer** (P1 — shared `StandardMaterial3D` preset library + one global
> cel-shading post-process) keeps them coherent. **Because the engine overrides per-asset PBR and applies
> cel-shading, the pipeline generates SHAPE-ONLY meshes with flat single-material albedo — no baked PBR.**

---

## Format & technical targets

- **3D:** glTF **binary (`.glb`), PLAIN / UNCOMPRESSED** — Y-up, origin at base/feet, facing +Z.
  **CRITICAL (verified empirically on Godot 4.6.2-stable mono):** the runtime loader **rejects** any GLB
  declaring `KHR_mesh_quantization`, `EXT_meshopt_compression`, or `KHR_draco_mesh_compression` (err 43,
  never reaches `GenerateScene`). So **never** Draco/quantize/meshopt-compress; `gltfpack`/`gltf-transform
  optimize` are disqualified (use discrete `weld`/`simplify` only). Single material per asset.
- **Reconciled tri-budget** (tighter GDD figure = target, looser ingest cap = hard ceiling):

  | Asset | LOD0 target (PASS) | WARN + auto-decimate | FAIL (re-roll) | LOD1 | LOD2 | Collision |
  |---|---|---|---|---|---|---|
  | **Units** | ≤ 3,000 tris | 3k–15k | > 15,000 | ~40% | ~15% | convex hull, <1% |
  | **Buildings** | ≤ 10,000 tris | 10k–30k | > 30,000 | ~40% | ~15% | convex hull, <1% |
  | **Props (future)** | 200–1,000 | 1k–3k | > 3,000 | — | — | hull |

  AI generators emit 100k+ tris (marching cubes) — the QA loop's first move is always **decimate-to-target**.
- **Textures (terrain):** tileable/seamless albedo, 1k–2k, assigned via the **Terrain3D Inspector**
  (procedural-via-ClassDB does not persist — documented gotcha).
- **Audio:** `.ogg` (Vorbis), **mono** SFX.
- **Target dirs:** GLBs → `godot/assets/models/factions/<faction>/`; audio → `godot/resources/audio/sfx/`;
  terrain textures → assigned in-editor. (`res://` = the `godot/` project root.)

> **WIRING NOTE — faction JSON update required.** The themed mesh filenames below are the *new* targets;
> the live JSONs still point at the old generic filenames (`worker.glb`, etc.). Landing the redesign means a
> one-time **data edit** to `alpha_faction.json` / `beta_faction.json`: set `mesh_path` to the themed
> filename, `display_name` to the themed name, and the revised stats from the faction-design doc. **Zero
> code change** (role ids are unchanged). Tracked as the "JSON data task".

---

## ONE SHARED STYLE PREFIX (paste verbatim; swap only the bracketed unit/structure pose clause)
**UNIT_PREFIX:** `clean low-poly RTS unit, flat-shaded single-material albedo (engine applies a global cel-shade, do not add PBR/specular/roughness/normal-map detail), readable silhouette for a distant top-down camera, early-20th-century industrial/European-military FMA-inspired alchemy world, neutral relaxed idle pose facing +Z (A-pose, arms slightly away from the body, NOT a rigid T-pose), plain white background, origin centered at the feet/base on the +Z axis.`

**STRUCTURE_PREFIX:** `clean low-poly RTS structure, flat-shaded single-material albedo (engine applies a global cel-shade, do not add PBR/specular/roughness/normal-map detail), readable silhouette for a distant top-down camera, early-20th-century industrial/European-military FMA-inspired alchemy world, sitting squarely on the ground plane facing +Z, plain white background, origin centered at the base footprint.`

**VEHICLE_PREFIX:** as UNIT_PREFIX but replace the pose clause with `static neutral orientation, barrel/front facing +Z (no biped pose).`

**AIR_PREFIX:** as UNIT_PREFIX but replace the origin clause with `origin centered under the body as if hovering low, no ground contact.`

**Per-faction PALETTE token (append AFTER the shared prefix):**
- COVENANT_PALETTE: `FACTION PALETTE: slate-blue greatcoats and leather, matte brass-colored automail accents (used sparingly), chalk-white transmutation sigils that flare cyan-white; flat color blocks, no implied metal gloss.`
- COURT_PALETTE: `FACTION PALETTE: oxblood-crimson cloth, flat black-iron and brass blocks, faint crimson alchemic Core-glow as emissive accent, gothic-uncanny homunculus mood; flat color blocks, no implied metal gloss.`

## ONE SHARED NEGATIVE PROMPT (apply to every gen)
`high-poly, photorealistic, PBR, specular highlights, glossy or metallic reflections, normal-map or rivet/crack micro-detail, baked shadows, ground plane, base or pedestal, scenery, multiple characters, cropped or missing limbs, text, watermark, logo, signature, busy background, motion blur, depth of field, dramatic cinematic lighting, off-center framing, action or dynamic pose.`

---

# THE CRUCIBLE COVENANT (alpha)

## Units
**worker — Acolyte** | mesh `acolyte_alchemist.glb` | mesh_scale 0.95
Lore: A novice who reads, breaks, and rebuilds raw ore into the army's lifeblood, chalk still on their fingers.
Prompt: `UNIT_PREFIX + COVENANT_PALETTE` SUBJECT: a slight young acolyte in a knee-length slate-blue work coat with rolled sleeves, one simple brass automail forearm, chalk satchel and a small pick at the hip; compact unarmed silhouette clearly the smallest humanoid; a faint chalk-white circle glows on the open palm.

**infantry — Covenant Transmuter** *(baseline Barracks unit, list FIRST)* | mesh `covenant_transmuter.glb` | mesh_scale 1.0
Lore: The Covenant's backbone battle-alchemist, reshaping the ground into spears mid-charge.
Prompt: `UNIT_PREFIX + COVENANT_PALETTE` SUBJECT: a standing soldier in a belted slate-blue greatcoat over a leather cuirass, etched chalk bracers (not a full metal arm), a short straight sword at the hip, hands meeting over a small glowing chalk-white circle; **bulk the shoulders/pauldrons so this is the WIDEST top-down outline of the three Covenant humanoids**; baseline soldier read.

**scout — Quicksilver Runner** | mesh `quicksilver_runner.glb` | mesh_scale 0.9
Lore: A courier-duelist who etches speed-sigils into the road and strikes where the line is thin.
Prompt: `UNIT_PREFIX + COVENANT_PALETTE` SUBJECT: a lean runner in a cropped slate-blue jacket and tight leggings, a long scarf swept hard to ONE side and a light dagger; **low crouched stance with NO shoulder bulk so the top-down outline is a thin dart shape, distinct from the wide Transmuter**; chalk-white speed-streak sigils glow along the boots.

**heavy_infantry — Bulwark Adept** | mesh `bulwark_adept.glb` | mesh_scale 1.2
Lore: A veteran who transmutes their own automail into a wall of plate, bought with the alchemist's own strength.
Prompt: `UNIT_PREFIX + COVENANT_PALETTE` SUBJECT: a broad heavily-built soldier in matte brass-and-iron plate over a slate-blue underlayer, large slab pauldron, heavy two-handed maul head-down; **bulky wide top-heavy silhouette, clearly the tankiest human**; flat color blocks, one chalk-white circle glowing at a shoulder seam (no rivet detail).

**archer — Pierce Marksman** *(baseline Archery Range unit, list FIRST among Ranged)* | mesh `pierce_marksman.glb` | mesh_scale 0.95
Lore: A sharpshooter whose rifle transmutes ordinary rounds into armor-splitting alchemic slugs.
Prompt: `UNIT_PREFIX + COVENANT_PALETTE` SUBJECT: a poised marksman in a long slate-blue duster and leather bandolier, peaked cap and goggles, **a long brass-fitted bolt-action rifle held HORIZONTALLY across the body so the barrel breaks the round head-shape from above**; tall slim vertical figure that reads as ranged; chalk-white sigil glows at the rifle breech.

**mage — Circle Savant** | mesh `circle_savant.glb` | mesh_scale 1.0
Lore: A master of inscribed circles who hurls condensed transmutation-energy; the Covenant's most fragile.
Prompt: `UNIT_PREFIX + COVENANT_PALETTE` SUBJECT: a robed scholar in a flowing hooded slate-blue coat with wide split sleeves, no automail, **a large glowing chalk-white transmutation circle held SYMMETRICALLY BETWEEN two outstretched hands (centered disc)**; distinctive floating-circle silhouette and trailing hem mark it as the caster; thin and unarmored.

**siege_engine — Crucible Mortar** | mesh `crucible_mortar.glb` | mesh_scale 1.8
Lore: A wheeled alchemic furnace that lobs transmuted molten payloads.
Prompt: `VEHICLE_PREFIX + COVENANT_PALETTE` SUBJECT: a two-wheeled brass-and-iron mortar carriage with a stubby upward-angled barrel feeding from a glowing crucible furnace at the rear, slate-blue plating, hand-crank; low wide wheeled machine silhouette, clearly mechanical and much bigger than infantry; chalk-white sigils ring the barrel mouth.

**griffin — Greycrest, the Bonded** | mesh `greycrest_bonded.glb` | mesh_scale 1.4
Lore: A rescued beast-fusion that still remembers being a falconer's friend; the Covenant's loyal sky chimera. (Air: no dedicated air-production building exists in the engine today — pre-existing gap, not a new building.)
Prompt: `AIR_PREFIX + COVENANT_PALETTE` SUBJECT: a noble eagle-lion chimera with **broad SMOOTH ROUNDED feathered wings (clean wing outline)**, a lion's hindquarters, one brass automail foreleg-talon, a slim slate-blue saddle-harness with a chalk-white brand on the shoulder; instantly-readable rounded winged silhouette wider than any ground unit; alert and loyal, not monstrous.

## Buildings
**command_center — Covenant Sanctum** | mesh `covenant_sanctum.glb` | mesh_scale 3.0
Lore: A repurposed chapel-workshop where Acolytes train and the Covenant's stolen knowledge is kept.
Prompt: `STRUCTURE_PREFIX + COVENANT_PALETTE` SUBJECT: a stout fortified chapel-workshop of slate-blue stone, steep slate roof, tall central clocktower-spire, brass-pipe chimneys, telegraph wires, a huge glowing chalk-white transmutation circle inlaid in the courtyard; clearly the largest friendly structure, civic and protective.

**barracks — Crucible Hall** | mesh `crucible_hall.glb` | mesh_scale 2.5
Lore: A drill-hall where Transmuters and Bulwark Adepts learn to reshape matter mid-fight.
Prompt: `STRUCTURE_PREFIX + COVENANT_PALETTE` SUBJECT: a long low slate-blue barracks hall with an arched drilling-yard entrance, a banner of crossed chalk and sword, training dummies, a chalk-white circle glowing above the doorway; squat rectangular military silhouette, distinct from the tall command center.

**archery_range — Sigil Foundry** | mesh `sigil_foundry.glb` | mesh_scale 2.5
Lore: A ventilated shooting-gallery and rune-lab where Marksmen and Circle Savants etch firing sigils.
Prompt: `STRUCTURE_PREFIX + COVENANT_PALETTE` SUBJECT: an open-fronted slate-blue range with a tall slatted firing canopy, hanging brass lanterns, stacked target butts down one side, a chalk-white aiming sigil on the back wall; horizontal open-air silhouette with a distinctive overhanging roof.

**siege_workshop — Transmutation Forge** | mesh `transmutation_forge.glb` | mesh_scale 2.8
Lore: A roaring forge where Acolytes pour alchemic payloads and assemble Crucible Mortars.
Prompt: `STRUCTURE_PREFIX + COVENANT_PALETTE` SUBJECT: a heavy industrial forge-hall of slate-blue brick with a tall brick smokestack, a large arched vehicle bay with a half-built mortar inside, glowing orange crucible light spilling out, brass gauges; bulky workshop silhouette with the tall chimney as its read-at-distance marker.

---

# THE SANGUINE COURT (beta)

## Units
**forgehand — Cinderhand Thrall** | mesh `cinderhand_thrall.glb` | mesh_scale 1.0
Lore: A hollowed laborer whose grafted brass hand never tires; the Court's stone-fed shovel.
Prompt: `UNIT_PREFIX + COURT_PALETTE` SUBJECT: a stooped hollow-eyed laborer in a soot-stained leather apron, one crude brass prosthetic ending in a digging claw, a dim red Core-ember glowing through a chest vent; hunched, the smallest and simplest silhouette so it reads as a non-combatant worker.

**footsoldier — Maul-Fused Wretch** *(baseline Barracks unit, list FIRST; the Court's disposable ground chimera)* | mesh `maul_fused_wretch.glb` | mesh_scale 0.95
Lore: A mass-fused stone-fed thrall-beast marched forward in waves; each one that falls only stokes the furnace.
Prompt: `UNIT_PREFIX + COURT_PALETTE` SUBJECT: a gaunt fused thrall-beast in a tattered oxblood coat, an iron half-mask and one over-grafted brute arm ending in a fused cleaver-maul, faint red Core-light bleeding from coat seams; lean asymmetric humanoid-beast silhouette, clearly rank-and-file — taller than the worker, lighter than the heavy bodies, slightly inhuman in outline.

**bulwark — Slag Bulwark** | mesh `slag_bulwark.glb` | mesh_scale 1.0
Lore: A thrall fused to a slab of transmuted iron; a walking wall the Court plants in front of everything it values.
Prompt: `UNIT_PREFIX + COURT_PALETTE` SUBJECT: a broad squat humanoid whose left side is a massive fused slab of dark iron forming a built-in tower shield, the other arm a stubby crushing maul; **a single BROAD glowing crimson seam across the slab (no fine cracks)**; wide low blocky silhouette that reads as a wall on legs, shorter and bulkier than the Wretch.

**ironclad — Pride Colossus** | mesh `pride_colossus.glb` | mesh_scale 1.3
Lore: A sin-born immortal in transmuted armor; it does not retreat because it does not believe it can lose.
Prompt: `UNIT_PREFIX + COURT_PALETTE` SUBJECT: a towering armored homunculus in flat black-iron plate over a crimson tabard, an uncanny too-perfect masked face, a heavy gauntleted fist, regal stance, a bright red Core glowing through a breastplate gap; the largest, most imposing humanoid silhouette, clearly an elite head-and-shoulders above the Bulwark (shape-level only, no rivet/texture detail).

**crossbowman — Bolt Penitent** *(baseline Archery Range unit, list FIRST among Ranged)* | mesh `bolt_penitent.glb` | mesh_scale 0.95
Lore: A stone-fed marksman bolted into a heavy repeating arbalest, firing crimson-tipped quarrels until its arms give out, then mending them.
Prompt: `UNIT_PREFIX + COURT_PALETTE` SUBJECT: a hooded thrall whose forearms are bolted into a bulky black-iron repeating crossbow held at the hip, heavy shoulder plates, dim red Core-glow at the throat; **distinct wide horizontal weapon silhouette (the arbalest reads from above)** separating it from the bare-handed melee bodies.

**rune_caster — Cinder Cantor** | mesh `cinder_cantor.glb` | mesh_scale 1.0
Lore: A stone-fed caster who scrawls burning transmutation circles in the air, turning stolen souls into searing crimson fire.
Prompt: `UNIT_PREFIX + COURT_PALETTE` SUBJECT: a tall robed thrall in a hooded oxblood greatcoat with ONE bare arm raised, **a single glowing red transmutation disc held OFF to that one raised hand (asymmetric, one-sided — distinct from the Covenant Savant's centered two-handed disc)**, an uncanny serene masked face, a bright Core at the sternum; slender silhouette defined by the off-side floating disc.

**war_machine — Render Crawler** | mesh `render_crawler.glb` | mesh_scale 1.8
Lore: A vast crawling furnace-cannon that lobs barrels of compressed Sin-Glass, devouring ground and the dead to feed its boilers.
Prompt: `VEHICLE_PREFIX + COURT_PALETTE` SUBJECT: a massive low many-legged crawling siege furnace, a fat upward-angled mortar barrel over a glowing red boiler-belly, flat black-iron plating and brass gauges, vents leaking crimson light; the single largest and lowest silhouette, clearly a vehicle not a humanoid, unmistakable as siege from above.

**wyvern — Envy Wraithwing** | mesh `envy_wraithwing.glb` | mesh_scale 1.4
Lore: A mass-fused sky-chimera of leather, iron and stolen faces; the Court's manufactured horror that should never have been given wings. (Air: no producer exists — unbuildable until the Air-building epic; not introduced here.)
Prompt: `AIR_PREFIX + COURT_PALETTE` SUBJECT: a stitched flying chimera with **wide RAGGED ASYMMETRIC bat/leather wings with trailing tatters (jagged torn wing outline — deliberately contrasted with the Covenant griffin's clean rounded wings)**, an elongated fanged maw, faint extra human-like faces fused into a crimson-glowing torso; broad torn-wing silhouette that reads instantly as the only airborne Court unit.

## Buildings
**command_center — The Sanguine Furnace** | mesh `sanguine_furnace.glb` | mesh_scale 3.0
Lore: The beating crimson heart of the Court; a cathedral-foundry where stolen souls are rendered into thralls and Sin-Glass fuel. (Lore hook: this IS the soul-rendering / Core-manufacturing structure of the setting.)
Prompt: `STRUCTURE_PREFIX + COURT_PALETTE` SUBJECT: a tall gothic foundry-cathedral of black iron and brass with a great central furnace-chimney pouring red light, ringed by smaller smokestacks and a glowing transmutation circle on the forecourt; the largest, most vertical and ornate structure — clearly the main base.

**barracks — The Thrall Yards** | mesh `thrall_yards.glb` | mesh_scale 2.5
Lore: Stone-fed pens and stitching-tables where conscripts, bulwarks and the sin-born melee line are assembled.
Prompt: `STRUCTURE_PREFIX + COURT_PALETTE` SUBJECT: a long low fortified hall of dark iron and stone with barred pens along one side, a wide arched mustering gate stained crimson, chained gibbets; broad horizontal silhouette, lower than the central furnace.

**archery_range — The Bolt Sanctum** | mesh `bolt_sanctum.glb` | mesh_scale 2.5
Lore: A vaulted armory-chapel that bolts arbalests onto penitents and brands sigils into the Court's casters.
Prompt: `STRUCTURE_PREFIX + COURT_PALETTE` SUBJECT: a narrow tall vaulted chapel-armory of black iron with tall slit firing-windows, racks of crimson-glowing quarrels along the front, a small floating sigil-disc over the entrance; upright slender silhouette distinct from the squat barracks.

**siege_workshop — The Render Works** | mesh `render_works.glb` | mesh_scale 2.8
Lore: A heavy crawling-crane foundry where bodies and ore alike are rendered into Render Crawlers and barrels of Sin-Glass.
Prompt: `STRUCTURE_PREFIX + COURT_PALETTE` SUBJECT: a massive heavy-industrial foundry of black iron with an overhead gantry crane, a glowing red rendering-vat, oversized brass boiler tanks, a wide vehicle-bay door; the bulkiest, most mechanical structure with a clear vehicle exit.


---

### D. The 7 SFX generation prompts — via the reused Stable Audio 3 setup

> **Generator (reused, already proven on this exact rig):** Stable Audio 3 **`medium-base`** at
> `D:\Projects\TabletopMagic\TabletopMagic\tools\bird-audio-gen\stable-audio-3\.venv\Scripts\python.exe`
> (Py 3.12, torch 2.7.1+cu126; HF gated repos already accepted; runs on the RTX 3060 12GB without flash_attn).
> License: Stability AI Community (commercial OK < $1M).
> **PROVEN RECIPE — do not change:** `model.generate(prompt=…, duration=N, steps=50, cfg_scale=7.0,
> rescale_cfg=False, seed=42)` with **NO negative_prompt** (at cfg>1 it causes electrical buzzing — the old
> "Stable Audio Open + negative + num_waveforms" plan is REPLACED). Output runs hot (peak ~1.0) →
> **normalize/limit in post**, then `ffmpeg -ac 1 -c:a libvorbis -q:a 4` → mono `.ogg`. For clean sustained
> tonal cues (`ui_click`, `training_complete`) drop **cfg to 4–5** if cfg 7 distorts. Prompt rules: concrete
> sound-words + "dry, close-mic'd, single sound, quiet background"; avoid instrument-metaphor words (taken
> literally). `audio_end_in_s` below = the `duration` arg.

| File | Prompt | duration_s |
|---|---|---|
| `melee_hit.ogg` | "a single sharp metallic sword-on-shield clash, short, dry, percussive, high quality" | 1.0 |
| `ranged_hit.ogg` | "a single arrow thudding into wood and flesh, short whoosh then impact, dry" | 1.0 |
| `explosion.ogg` | "a single punchy mid-sized explosion, debris burst, tight low-end thump, no long tail" | 2.0 |
| `unit_killed.ogg` | "a short armored body collapse, metal clatter and grunt, dry, one-shot" | 1.5 |
| `building_placed.ogg` | "a heavy structure thud settling into ground with a brief stone-and-wood creak, confirming" | 1.5 |
| `training_complete.ogg` | "a short bright positive confirmation chime, two ascending metallic notes, clean game UI cue" | 1.2 |
| `ui_click.ogg` | "a single crisp short UI button click, tight tick, dry, no reverb" | 0.5 |
> UI cues (`training_complete`, `ui_click`) are abstract — expect more re-rolls; try cfg 4–5. No cloud/ElevenLabs fallback needed: the local Stable Audio 3 `medium-base` setup is reused in place.

### E. The 4 terrain-texture prompts (SDXL + seamless-tiling, 1k-2k, +negative: `"seams, visible tile edges, repeating motif, objects, shadows, baked lighting, text, photoreal high detail"`)

| Biome | Prompt |
|---|---|
| Grass | "seamless tileable top-down stylized grass terrain texture, flat even green tones, subtle blade variation, low-contrast, game ground albedo" |
| Dirt | "seamless tileable top-down stylized dry dirt terrain texture, flat earthy brown, subtle pebbles and cracks, low-contrast, game ground albedo" |
| Rock | "seamless tileable top-down stylized grey rock terrain texture, flat slate tones, subtle fracture pattern, low-contrast, game ground albedo" |
| Snow | "seamless tileable top-down stylized white snow terrain texture, flat soft off-white, gentle drift undulation, low-contrast, game ground albedo" |

### F. Per-asset-type QA acceptance criteria (add as a manifest table)

**Units (.glb):** PASS tris ≤ 3,000 (warn+decimate 3k-15k, FAIL >15k); materials = 1 (FAIL >2); watertight OR no non-manifold edges; bounds min-Z ∈ [-0.02, 0.05] (origin at feet), X/Y centered ±2% of extents; facing +Z; NO KHR_mesh_quantization/EXT_meshopt_compression/Draco in extensionsUsed (hard FAIL); loads via AppendFromFile->GenerateScene (no box fallback); silhouette legible top-down per agent vision.

**Buildings (.glb):** same as units except PASS tris ≤ 10,000 (warn 10k-30k, FAIL >30k); footprint flat on ground (min-Z=0); wider stable base silhouette.

**Terrain textures (.png):** dimensions 1024-2048 sq, power-of-two preferred; seam delta (np.roll half-tile, edge-region mean abs diff) below threshold (FAIL on visible seam); flat/low-contrast (no baked shadows); albedo only.

**SFX (.ogg):** mono (1 channel, FAIL if stereo); duration within target ±25%; peak in [-3, -0.5] dBFS (FAIL if silent/clipping); no >0.2s leading/trailing dead air after trim; Vorbis -q:a 3-5.

---

## Counts, priority & build status

- **24 GLBs total** (Covenant 12 + Court 12) · **7 SFX** · **4 terrain textures**.
- **Asset-generation priority:** (1) Court 8 units (P0.3 headline) → (2) Court 4 buildings → (3) Covenant
  12 → (4) SFX → (5) terrain. All 24 GLBs are genuinely open (both factions are placeholders today).
- **Validation gate:** every generated asset passes the 4-layer self-QA loop (trimesh → Blender ortho
  render → agent vision → in-engine `GLTFDocument` ingest) and the per-asset-type criteria above before
  landing. A rejected asset falls back to the box placeholder.

### ⚠️ Engine reality — buildable-today vs needs-code (from the full-redesign critique)

Asset generation is independent of this (the **meshes are needed regardless**), but the *full ability
redesign* depends on engine systems not yet built. Today the production system trains only the **first**
unit per category, so per faction only **1 Melee + 1 Ranged + Siege + Worker** are reachable in normal
play; **Air has no producer**; and **no signature abilities/faction mechanics run yet** (both armies are
pure stat sheets). The 12 supporting epics live in `_bmad-output/fma-faction-design.md` and feed the GDS
epics/stories planning step. **Recommendation: generate all 24 assets now; build the engine systems via the
GDS roadmap in parallel.**

## 7. Optional / deferred
- **Unit/hero portraits** (`portraits/*.png`) — command-card / hero UI (the Seven-Sin hero pantheon +
  Covenant heroes are great portrait candidates).
- **Music tracks** — menu + in-match ambience.
- **UI sprites** — currently programmatic; theme art is a UX-polish item.
- **Vermillion Core map-objective economy** — the setting's headline theme; a future gameplay epic.
