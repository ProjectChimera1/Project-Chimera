# R8b: Scatter assets and licences (grass, flowers, shrubs, trees for the ChimeraTerrain meadow)

Research only, 2026-10-01. No Unreal process was started. Nothing was downloaded except Poly Haven metadata (JSON) and preview images, kept in
`D:/Projects/Chimera-Unreal/ChimeraTerrain/Out/refs/scatter/` (git-ignored: `.gitignore:28` `ChimeraTerrain/Out/`). Tags: **MEASURED** = read from a file or API
response in this session; **UNVERIFIED** = inferred or not yet run; the verification that would settle it is named.

## 0. Verdict

Enough legal material exists to build the scatter system now, but not yet enough at Manor Lords quality for bushes and broadleaf trees.

1. **Stand-in, zero download, already on this machine:** Epic's `PCGBiomeSample` meshes (9 meshes, 6 to 2,588 Nanite-input triangles each, all Nanite, covered by the
   Unreal Engine EULA). They are stylised flat-green low-poly (see the LookTest shot), so they are for bring-up and perf tests, not the final look.
2. **Final look, CC0, no login:** Poly Haven models. Grass, flowers, ferns and weed sprigs are small (0.8 to 3 MB each at 1k, 5 to 21 variant meshes per id, mesh
   data identical at every resolution) and fit a temperate meadow well. Trees are scan-grade (2 M to 17 M triangles, 100 MB to 1 GB each) and need an offline
   decimation step (Blender 4.5.10 and 5.2.1 are installed at `D:/tools/blender`). **Poly Haven has no usable temperate bush** (its "shrubs" are weed sprigs and dry
   scrub) and only one broadleaf tree (`tree_small_02`, small). That gap is the real risk (section 6).
3. **Do not use:** Fab/Quixel (login), Epic's `ImpostorBaker` (Blueprint-driven, violates the text-first rule), the PVE sample trees as the primary route
   (experimental Nanite-assembly skeletal meshes with the DynamicWind plugin; spike only).
4. At the `rts80` camera one grass clump is about 2 px. Density, colour and shadow matter, not detail: author clumps at 2 to 3 times natural size and keep them
   under about 300 triangles (section 5).

## 1. What exists on this machine

### 1a. Epic PCGBiomeSample meshes (the LookTest scatter)

Source files: `D:/Epic Games/UE_5.8/Engine/Plugins/Experimental/PCGBiomeSample/Content/Meshes/*.uasset` (plugin `.uplugin`: "CreatedBy Epic Games, Inc.",
`IsExperimentalVersion: true`, `EnabledByDefault: false`). The packages carry the engine tag `++Fortnite+Main` (strings of `PCG_Tree_01.uasset`): they are Epic-authored.
LookTest places 450 trees, 400 undergrowth and 40 boulders from these (`ProjectChimera/LookTest/PLAN.md:336-340`, `tools/lt_common.py:42-47`; the map
`LT_A_noLumen.umap` references `/PCGBiomeSample/Meshes/PCG_*`).

Triangle counts are **MEASURED** from the Nanite build log of the LookTest editor session
(`ProjectChimera/LookTest/logs/editor_3.stdout.log:7690-7850`, "ConstrainClusters Input" and "Fallback num tris"). Pairing of interleaved lines to meshes is by
build order (Tree_02 and Spruce lines interleave); treat the Tree_02/Spruce rows as 90% sure.

| mesh (`/PCGBiomeSample/Meshes/`) | Nanite input tris | fallback tris | Nanite GPU size | materials | uasset size | note |
|---|---|---|---|---|---|---|
| `PCG_Tree_01` | 312 | 186 | 8.6 KB | 2 | 21.7 KB | broadleaf, flat-shaded |
| `PCG_Tree_02` | 672 | 282 | 20.1 KB | 2 | 27.0 KB | broadleaf |
| `PCG_Spruce_01` | 2,588 | 1,124 | 92.9 KB | 2 | 56.5 KB | conifer, the densest |
| `PCG_Pine_01` | 318 | 192 | 9.0 KB | 2 | 24.2 KB | conifer |
| `PCG_Sapling_01` | 36 | 36 | 1.2 KB | 1 | 13.8 KB | |
| `PCG_Sapling_02` | 320 | 194 | 8.8 KB | 2 | 22.3 KB | |
| `PCG_Seedling_01` | 6 | 6 | 0.3 KB | 1 | 12.3 KB | cards; used by Epic as the "grass" of the broadleaf GPU ground scatter |
| `PCG_Seedling_02` | not logged | not logged | n/a | n/a | 17.2 KB | UNVERIFIED (probably under 100 tris by file size); never rebuilt in the log |
| `PCG_Boulder_01` | 128 | 128 | 4.1 KB | 1 | 19.2 KB | |

- **Nanite flag:** MEASURED yes for the 8 meshes with a Nanite build in the log (cluster output lines); LookTest recorded the flag with
  `lt_import.py` (`nanite_settings.enabled`). **LODs:** none authored; Nanite builds clusters plus a fallback mesh only.
- **Material complexity:** `Materials/PCG_Foliage_01.uasset` is the master (23 KB), `PCG_Foliage_01_Inst*.uasset` instances (4 to 9 KB), `PCG_Trunk_01`, `PCG_Rocks`.
  Instruction counts are UNVERIFIED (need `stat` or the material stats panel in an editor session). `Meshes/GPURuntime/PCG_Foliage_0x_GPUInst.uasset` are
  material instances of `PCG_Foliage_01`, not meshes (strings show `MaterialInstanceConstant`).
- **Look:** `Out/refs/scatter/looktest_A_noLumen_gameplay_1280.jpg` (from `LookTest/out/game_A_noLumen_r1.png`): saturated teal-green flat trees.
- **Engine copy:** `Plugins/PCG/Content/SampleContent/SimpleForest/Meshes/` holds a second set (`PCG_Tree_01..03`, `PCG_Boulder_02`, `PCG_Seedling_01`, 16 to 41 KB), triangle counts UNVERIFIED.
- **Not enabled in ChimeraTerrain:** `ChimeraTerrain.uproject` lists only RealtimeMeshComponent, PythonScriptPlugin, EditorScriptingUtilities. `ProjectChimera` enables PCGBiomeSample
  (`research/r3-ue-host-and-instancing.md:44`). To use these meshes in ChimeraTerrain, copy them to `/Game/Scatter/` with a commandlet (UNVERIFIED) or enable the plugin.
- **Measured cost context:** LookTest A_noLumen (58 units, 10 buildings, about 900 Nanite scatter meshes, 1080p, `-game` uncooked): median 76.5 fps, GPU 12.7 ms,
  render-thread bound at 13.1 ms, peak VRAM 3,082 MiB (`LookTest/out/fps_summary.json`, tag `game_A_noLumen_r1`; r2 76.0 fps). That scene has no terrain mesh cost.

### 1b. Epic Procedural Vegetation Editor sample assets (experimental)

`D:/Epic Games/UE_5.8/Engine/Plugins/Experimental/ProceduralVegetationEditor/Content/SampleAssets/` (plugin description: "Node Graph based Editor that allows users to create
Nanite Foliage ready vegetation directly in the engine"; modules `ProceduralVegetation` Runtime and `ProceduralVegetationEditor` Editor; deps Dataflow, GeometryScripting, PCG, DynamicWind).

- Ready-made outputs (MEASURED from asset strings): `StarterContent/DeciduousTree_01/PVE_Deciduous_Tree_01` (6.9 MB), `Deciduous_Shrub_01/PVE_Deciduous_Shrub_01` (3.5 MB),
  `ConiferTree_01/PVE_Conifer_01` (4.4 MB), `Plant_01/PVE_Plant_01` (0.3 MB). Each is a `SkeletalMesh` with `NaniteAssemblyData` and `DynamicWindSkeletalData`
  (strings `ENaniteAssemblyNodeTransformSpace`, `/Script/DynamicWind`). Triangle counts UNVERIFIED (not logged).
- Textures are large: tree bark C 35.2 MB and NAH 37.9 MB, foliage CA 11.9 MB and NT 24.0 MB (DeciduousTree_01); conifer bark 26.1 and 35.0 MB. About 250 MB of `.uasset` for the two trees.
- Species parameter sets as JSON: `Tree_European_Beech_01` (4 variants), `Tree_Common_Hazel_01` (5), `Tree_European_QuakingAspen_01` (4), `Tree_Norway_Maple_01` (4); 1.3 to 20 MB each.
  These are temperate species, the only temperate broadleaf and hazel data found. Presets: `StarterContent/Presets/PVE_Preset_*` (Leaf_Tree_01..04, Pine_01, Sapling_01, Shrub_01, Spruce_01..02).
- Generator export is `UPROPERTY(EditAnywhere)` in `Source/ProceduralVegetation/Public/Params/PVExportParams.h:60-64` (`bCreateNaniteFoliage = true`); no commandlet or
  BlueprintCallable found (grep of `Source/`). So regenerating species is an editor-UI action: UNVERIFIED whether it can be scripted. Using the shipped outputs as-is needs no generator.
- Risks: experimental plugin; DynamicWind is described "Extremely experimental dynamic wind support for Nanite foliage" (`DynamicWind.uplugin`); instancing skeletal Nanite assemblies
  at scale and packaging them in Shipping is UNVERIFIED. Treat as a one-session spike (section 6), not the baseline.

### 1c. Everything else on this machine: nothing usable

| location | finding |
|---|---|
| `Engine/Content/StarterContent` | textures only (`T_ground_Moss_D`); no meshes. `Engine/Content/EngineMeshes`, `BasicShapes`: primitives. `OpenWorldTemplate`, `MapTemplates`: materials only |
| `Engine/Plugins/MegascansPlugin`, `Bridge/Content/MSPresets` | material presets (`M_MS_Foliage_Material`) only; Megascans meshes need Fab login (constraint 5) |
| `Engine/Plugins/Experimental/Landmass/.../PreviewContent` | material layers `Grass`, `Rock` and layer infos; no meshes |
| `Engine/Plugins/PCG/Content/GraphTemplates/Meshes` | 4 fence meshes |
| `Engine/Plugins/Runtime/NetworkPredictionExtras/.../SM_TreeStump_01` | one stump |
| `Engine/Plugins/Experimental/ImpostorBaker` | `Content/BP`, `Content/Maps`, no `Source/`: Blueprint-driven, rejected by constraint 4 |
| `D:/tripo-out` | 24 roster units and buildings (`*.glb`), weapon concept PNGs in `Need 3d Model/`. No vegetation |
| `D:/Projects/Project_Chimera/godot/assets` (the "R/godot/assets" path does not exist) | `models/factions/*` units and buildings, `textures/terrain/grass.png`; no vegetation |
| `ProjectChimera/Content/LookTest` | `Ground/T_Grass,T_Dirt`, 4 maps, ground materials, the unit/building roster; the vegetation is referenced from PCGBiomeSample, not stored there |
| `ChimeraTerrain/Content/Terrain` | ground material and 4-layer textures only |

### 1d. Licence of the Epic content (PCGBiomeSample, PVE samples)

- No licence file ships inside the engine install (searched `Engine/Documentation`, `Extras`, `Build`, and the plugin folders; only third-party licences exist).
- The governing text is the Unreal Engine EULA. The current web page returned HTTP 403 to automated fetches, so this review read the published
  **"Unreal Engine End User License Agreement for Publishing", v15 PDF** (`https://cdn2.unrealengine.com/Unreal+Engine%2Ffaq%2FUnrealEngineEULA_for_Publishing_v15-b9955ec2954f0e09fade11b2d9acecfc65cb6ab1.pdf`,
  147 KB, saved in the session scratchpad as `dl_db47f6.txt`). Relevant text, MEASURED:
  - Definition: "Content" means any code, artwork, or other content that Epic makes available to you for use with the Engine Code (other than Unreal Tournament and Robo Recall content); "Licensed Technology" = Engine Code and Content (Section 25).
  - Section 1: Epic grants a licence "to use, reproduce, display, perform, and modify the Licensed Technology for any lawful purpose".
  - Section 1(A)(a): you may Distribute Licensed Technology "incorporated in object code format only as an inseparable part of a Product" to end users under an EULA that disclaims warranties. A cooked package is that form.
  - "UE-Only Content" (content designated usable only with the Engine Code) may only be used in a Product that requires the Engine Code. Scatter inside a UE game satisfies this.
- Conclusion: PCGBiomeSample and PVE sample assets installed with the engine are usable in a cooked, packaged Chimera build. Caveats: (1) this is the Publishing-licence PDF v15, not
  today's web text; confirm the current EULA before shipping (open question 1); (2) never ship the uncooked `.uasset` files in a repo or download; (3) the Content stays git-ignored anyway.
- Fab content is out of scope: Fab licences are CC-BY or Standard (`https://dev.epicgames.com/documentation/en-us/fab/licenses-and-pricing-in-fab`, MEASURED) and download needs an Epic login.

## 2. CC0 sources reachable without a login

### 2a. Poly Haven (primary)

- **Licence, MEASURED** (`https://polyhaven.com/license`): "You can use our assets for any purpose, including commercial work. You do not need to give credit or attribution when using them...
  You can redistribute them... or even in a product you sell." All assets CC0.
- **API terms, MEASURED** (`https://raw.githubusercontent.com/Poly-Haven/Public-API/master/ToS.md`, section 2): free for any purpose; calls must carry a unique Referer or User-Agent naming
  the software (2.4); the "Powered by Poly Haven" credit applies only when the live API surfaces content inside your product, not to downloaded CC0 assets (2.5). Our fetch script is a build tool, so
  use `User-Agent: ChimeraScatterFetch/1.0` and no in-game credit is required. `Tools/fetch_textures.py` already follows this pattern for ground textures (`Textures/manifest.json`, "CC0 1.0 (Poly Haven, ambientCG)").
- **Resolution check, MEASURED 2026-10-01:** `GET https://api.polyhaven.com/assets?t=models` returned 521 models; `info/{id}` and `files/{id}` returned HTTP 200 for all 30 ids queried (25 vegetation ids in the tables plus 5 rock ids); the ids listed only as rejected were taken from the model list, not queried individually.
  Model URL pattern: `https://dl.polyhaven.org/file/ph-assets/Models/gltf/{1k|2k|4k|8k}/{id}/{id}_{res}.gltf` (+ `.bin` and three textures: `diff`, `arm`, `nor_gl`). Asset page: `https://polyhaven.com/a/{id}`.
- **Poly counts differ by source.** "API" is `polycount` from `/info`; "glTF" is the triangle sum I counted from the 1k `.gltf` accessors (the `.bin` is byte-identical at every resolution, e.g. `grass_medium_01.bin` 1,006,632 bytes at 1k, 2k, 4k, 8k). For grass the API
  figure is 20 to 65 times the glTF figure, so the API number is not what Unreal would import. Heights are accessor bounds in glTF Y-up, no node scale or matrix present (checked on 8 ids).

Candidate table (vegetation). "Meshes" = separate variant meshes in the glTF. Texture sets at 1k are diff + arm + nor_gl JPGs.

| id | category | meshes | glTF tris (per mesh) | API polycount | height of variants | alpha mode | 1k total | fit |
|---|---|---|---|---|---|---|---|---|
| `grass_medium_01` | Grass Tufts | 17 | 28 to 6,422 (clumps: 290, 310, 340, 653, 833, 1,257; small tufts 28 to 79) | 1,606,633 | 14 to 32 cm (clumps) | BLEND | 3.0 MB | **good**: green meadow tussocks, many variants |
| `grass_medium_02` | Grass Tufts | 5 | 714; 1,215; 1,664; 1,760; 2,489 | 1,043,926 | 16 to 40 cm | BLEND | 0.8 MB | good for dry or autumn tussocks (thumbnail is brown-grey) |
| `grass_bermuda_01` | Grass Tufts | 21 | 8 to 30 (most), 128 to 268 (4 meshes) | 223,596 | 2.5 to 15 cm | none (opaque cards, double sided) | 1.4 MB | good: cheap short tufts for dense cover; tiny |
| `dandelion_01` | Wildflowers | 5 | 2,080; 2,604; 3,180; 23,358; 23,542 | 64,099 | 4.7 to 16 cm | MASK | 3.1 MB | **good** yellow flower (use the three 2 to 3 k meshes) |
| `celandine_01` | Wildflowers | 5 | 442; 599; 622; 3,002; 4,301 | 1,746,764 | 12 to 19 cm | BLEND | 1.5 MB | good: yellow flowering ground cover |
| `periwinkle_plant` | Wildflowers | 6 | 1,478 to 11,252 | 67,388 | 16 to 41 cm | BLEND | 2.9 MB | pink flowers on stems; second colour for the meadow |
| `fern_02` | Ferns | 4 | 784; 816; 2,248; 2,384 | 6,232 | 21 to 43 cm (57 to 99 cm wide) | MASK | 1.1 MB | good understory near trees |
| `weed_plant_02` | Low Groundcover | 5 | 1,766 to 2,967 | 17,062 | 4 to 7 cm (12 to 29 cm wide) | MASK | not measured | filler leaf rosettes |
| `nettle_plant` | Shrubs & Bushes | 6 | 1,088 to 8,080 | 46,195 | 2 to 22 cm | MASK | not measured | tall weed, filler |
| `shrub_02` | Shrubs & Bushes | 4 | 5,188; 5,242; 7,590; 9,234 | 52,317 | 117 to 171 cm | MASK | 2.0 MB | **only shrub-sized mesh**, but thumbnail is dry brown twigs: dry scrub, not a leafy bush |
| `shrub_03` | Shrubs & Bushes | 4 | 1,790 to 2,385 | 16,955 | 26 to 40 cm | MASK | 1.6 MB | meadow reed sprigs |
| `shrub_04` | Shrubs & Bushes | 1 | 27,327 | 47,813 | 22 cm (58 cm wide) | MASK | 1.9 MB | leafy sprig, too big a mesh for its size |
| `shrub_01` | Shrubs & Bushes | 1 | 156,012 | 282,224 | 39 cm (259 cm long) | MASK | 6.9 MB | tall thin stems; not useful |
| `moss_01` | Moss | n/a | n/a | 246,170 | n/a | n/a | 0.9 MB | thumbnail is a green ball; skip |

Trees (all scan-grade; none is usable without decimation; `.bin` sizes are what a download costs, textures extra):

| id | species look | meshes | glTF tris | API polycount | height (accessor) | `.bin` | 1k total | fit |
|---|---|---|---|---|---|---|---|---|
| `tree_small_02` | small broadleaf | 1 (3 materials) | 2,062,487 | 4,652,585 | 2.8 m (4.1 m wide, API says 4.3 x 4.7 m) | 95.1 MB | 101 MB | **only temperate broadleaf**; scale up 3 to 4 times and decimate |
| `fir_tree_01` | fir / spruce | 3 variants | 505,494; 2,300,624; 4,176,819 | 7,853,731 | 9.3 to 12.3 m | 478.5 MB | 487 MB | good conifer, best download-to-value among full-size trees |
| `pine_tree_01` | pine | 3 | 4.2 M to 7.0 M (17.2 M total) | 17,427,094 | 9.5 to 13.9 m | not measured | 958 MB | skip (size) |
| `fir_sapling` | young fir | 3 | 124,743; 150,876; 157,402 | 433,021 | 0.6 to 1.1 m | not measured | 61 MB at 4k | undergrowth |
| `pine_sapling_small` | young pine | 3 | 122,235; 133,403; 142,506 | 398,144 | 0.9 to 1.2 m | not measured | 63 MB at 4k | undergrowth |
| `jacaranda_tree`, `island_tree_01..03`, `quiver_tree_*`, `pine_sapling_medium` (9.8 M), `fir_sapling_medium` (2.3 M) | tropical, coastal, desert or oversized | not measured | | | | | | rejected: wrong biome or size |

Rocks and logs (not requested, listed because Manor Lords meadows carry boulders): `rock_09` (23,280), `rock_07` (27,830), `stone_01` (71,916), `boulder_01` (123,976), `rock_moss_set_01` (63,127),
`tree_stump_01` (41,046), `tree_stump_02` (62,345); all id resolutions MEASURED OK. Epic `PCG_Boulder_01` (128 tris) is already on disk.

Previews: `Out/refs/scatter/ph_<id>.png` (512 px thumbnails of 20 ids) and `Out/refs/scatter/contact_sheet_polyhaven.jpg`. Note that the ball-shaped thumbnails (grass_*, celandine, moss) are
Poly Haven's scatter-preview spheres, not the meshes; the mesh variants are the 15 to 40 cm clumps in the table.

### 2b. Other CC0 packs (stylised, secondary)

All verified reachable 2026-10-01. None is photoreal; they are alternatives for bushes and as a style fallback.

| pack | licence (source) | contents | download without login |
|---|---|---|---|
| Quaternius **Stylized Nature MegaKit** `https://quaternius.com/packs/stylizednaturemegakit.html` | CC0 ("Free to use in personal, educational and commercial projects. (CC0 License)", page text and itch page) | 116 models: 40 trees, 35 plants and flowers, 27 rocks, grass, bushes; FBX/OBJ/glTF; Ghibli style; triangle counts not stated | itch.io `quaternius.itch.io/stylized-nature-megakit`, `actual_price 0, min_price 0`; "Standard" zip 99 MB; the browser flow works, a scripted download needs the itch `download_url` POST (brittle): treat as one manual fetch with a recorded sha256 |
| Quaternius Ultimate Nature Pack `.../packs/ultimatenature.html` | CC0 (page links creativecommons.org/publicdomain/zero/1.0) | 150 models, FBX/OBJ/Blend, untextured low-poly | public Google Drive folder `drive.google.com/drive/folders/1-Kl0L_Jg8awbh0S5T-z3zxh4mVlnxTpa` (found in page HTML); no login but brittle for scripts |
| Kenney **Nature Kit** `https://kenney.nl/assets/nature-kit` | CC0 1.0 (page text) | 330 models (trees, rocks, plants, fences, tiles); very simple low-poly | direct `https://kenney.nl/media/pages/assets/nature-kit/37ac38a37b-1677698939/kenney_nature-kit.zip`, HTTP 200, 10.5 MB (HEAD checked) |
| Others from search (itch.io: yoogameart 3D Trees Pack, aaddom Low Poly Nature Pack with 3 bushes, rikogamez, voxelbytebot) | each states CC0 on its page (search summary only, UNVERIFIED) | small low-poly packs | itch.io |
| Poly Pizza | licence filter exists; per-model licences vary (not all CC0) | | per-model check needed |

Preview: `Out/refs/scatter/contact_sheet_cc0_packs.jpg` (Stylized Nature MegaKit, Ultimate Nature Pack, Kenney sheet). All three read as toy or cartoon next to Manor Lords.

Excluded: Fab, Quixel Megascans, BlenderKit, Sketchfab (login), ambientCG (textures and decals only, no vegetation meshes found; already used for the `Grass004` ground layer).

## 3. Recommended minimum set

Principle: one manifest entry per scatter species (`id`, `source`, `url`, `licence`, `licence_url`, `sha256`, the picked variant mesh names, target height, tri budget), generated into `/Game/Scatter` by a script, the same
way C7 builds `Content/Terrain` from `Textures/manifest.json`. Swapping the stand-in for the final mesh changes only the manifest row.

| slot | pick | why | tri budget per instance (after prep) |
|---|---|---|---|
| Grass clumps (3) | `grass_medium_01` meshes `Plane.042` (310), `Plane.037` (833), `Plane.039` (1,257); dry variant `grass_medium_02` `Plane.001` (714); short tuft `grass_bermuda_01` `Plane.026` (147) | green tussock, one dry, one low tuft; all alpha cards, 17 + 5 + 21 variants to rotate through | at most 300 for any clump at 2 to 3 times scale; use the 310 and 147 meshes for the bulk, keep the 833 and 1,257 for near shots |
| Wildflowers (2) | `dandelion_01` `Plane.041` (3,180), `Plane.066`, `Plane.113` (about 2.1 to 2.6 k); `celandine_01` `Plane.006` (622) and `Plane.007` (599) | two yellows with different silhouettes; add `periwinkle_plant` `tree.001` (1,478) as the pink accent if a third colour is wanted | 600 to 3,200 (flowers are sparse, so cost is low) |
| Understory (1 to 2) | `fern_02` `Plane.006` (784), `Plane.059` (816); `weed_plant_02` | beneath trees, on rock edges | under 1,000 |
| Low shrubs / bushes (2) | **gap.** Stop-gap: `shrub_02` (dry scrub) and `PCG_Sapling_02` (194 tris, stylised). Real fix in section 6 | no CC0 leafy temperate bush found at realistic quality | 5 to 10 k for shrub_02 variants after reduction |
| Trees (3) | broadleaf `tree_small_02` (decimated, scaled to 10 to 14 m); conifer `fir_tree_01` (variants 505 k, 2.3 M, 4.2 M tris; decimate, 12 m native); optional third from the PVE spike (`PVE_Deciduous_Tree_01`) or Poly Haven `fir_sapling` as understory trees | the only realistic trees reachable without login | 8 to 30 k per tree after reduction (Nanite makes this a build-time cost, not a runtime one) |
| Boulders (optional) | `rock_09`, `rock_07` (23 to 28 k API polys; reduce) and Epic `PCG_Boulder_01` (128 tris) | cheap and already half-done | under 2 k |

Total download for everything above except the trees: about 16 MB at 1k (sum of the "1k total" column for the 8 picked ids), plus trees: `tree_small_02` 101 MB and `fir_tree_01` 487 MB at 1k. Texture resolution
1k is enough: a 12 m tree is about 105 px tall at `rts80` (calculation below).

## 4. LOD and impostor strategy at RTS distance

Camera numbers from plan C: `rts80` is `(0,-5142,6128) cm`, pitch -50, vFOV 75 (`plan-c-runtime-terrain.md:223`; `RtsCameraPawn.cpp:13-51`). That is about 80 m slant. At 1080p the visible height at 80 m is
2 x 80 x tan(37.5 degrees) = 123 m, so about **8.8 px per metre**. The `oblique` pose (25 m up, 13 degrees down) gives about 28 px per metre. These are my calculations from those constants (UNVERIFIED against a real frame).

| object | size | px at `rts80` | px at `oblique` | consequence |
|---|---|---|---|---|
| grass clump 25 cm | 0.25 m | about 2 | about 7 | invisible individually at `rts80`; the look comes from density and colour. Scale clumps 2 to 3 times (50 to 75 cm, 4 to 7 px) |
| flower 12 cm | 0.12 m | about 1 | about 3 | a coloured dot; use it for colour variation only, keep to a sparse density |
| shrub 1.5 m | | about 13 | about 42 | silhouette plus shadow; 1 to 2 k triangles read the same as 10 k |
| tree 12 m | | about 105 | about 340 | the main scatter read; shadow pool matters as much as the mesh |

Strategy (no Blueprints, nothing hand-edited):

1. **Trees and shrubs: Nanite static meshes** (opaque trunk, masked leaf material). Nanite supplies the LOD chain, so no hand-made LODs. Evidence: LookTest ran about 900 Nanite scatter meshes (186 to 2,588 tris each) in A_noLumen at 76 fps, 12.7 ms GPU
   with units, buildings and no terrain mesh. Whether a masked-leaf mesh of 10 to 30 k triangles keeps that cost is UNVERIFIED (open question 3).
2. **Grass and flowers: not Nanite**, standard masked cards (the PH BLEND alpha becomes Masked in the generated material), 150 to 800 triangles, per-instance cull distance near 60 m at `rts80`, full density only inside the near-RTS band. Reason: sub-5 px cards, overdraw and Nanite masked cost.
   A further reduction is cheap: drop the 833 and 1,257 triangle clumps beyond 25 m.
3. **Impostors: not needed for Nanite trees.** If a non-Nanite fallback is required, bake octahedral impostor atlases with our own Blender script; do **not** use Epic's `ImpostorBaker` (`Content/BP`, no `Source/`: Blueprint workflow).
   The SpeedTree billboard materials under `Plugins/Editor/SpeedTreeImporter/Content/SpeedTree9/` are for SpeedTree assets only.
4. **Shadows:** the Manor Lords tree-shadow pools come from Virtual Shadow Maps (`Config/DefaultEngine.ini:8 r.Shadow.Virtual.Enable=1`) on the tree meshes; grass and flowers should not cast shadows (cost for no read at 2 to 7 px).
   Lumen stays off: `DefaultEngine.ini:12-14` sets the GI and reflection methods to 2, which I read as Screen Space (UNVERIFIED against the engine enum); the LookTest recipe is `gi_method SCREEN_SPACE` (`manifest_A_noLumen.json`).
5. **Offline prep (Blender 4.5.10 headless, `D:/tools/blender/blender-4.5.10-windows-x64/blender.exe --background --python`):** pick the variant meshes, strip unused variants, decimate trees (collapse + planar), keep UVs and alpha, write one glTF per species;
   the Unreal side imports through a Python commandlet like `lt_import.py`. Prep is deterministic and text-first; the outputs go under git-ignored `Content/Scatter` and `Out/`.

## 5. Placement rules the assets imply (for the other designers)

- Native sizes are small; instance scale is a design parameter: grass x2 to x3, flowers x1.5 to x2, trees x3 to x4 (`tree_small_02`) or x1 (`fir_tree_01`).
- Four splat layers: the grass layer carries the vegetation; dirt/path, rock and snow carry none or a thin fringe (a design rule for the placement agents, not a property of the assets).
- Use per-instance random yaw and a tiny pitch from the world-position hash; the PH meshes are single clumps, so the variation comes from rotation, scale and the 5 to 21 variant meshes.

## 6. Open questions and spikes

1. **Current EULA text.** Confirm Section 1(A) and the "Content" definition against today's `unreal.com/eula` (HTTP 403 to automation, so the v15 Publishing PDF was read). Alec or a browser session can do it in minutes.
2. **Bush and broadleaf gap.** Decide among: (a) a one-session PVE spike: place `PVE_Deciduous_Shrub_01` and `PVE_Deciduous_Tree_01` (already generated, Epic content under the EULA) in a packaged ISM scene and check Shipping packaging, Nanite assembly instancing and DynamicWind cost;
   (b) author-time composite bush from `nettle_plant` + `fern_02` + `weed_plant_02` + `shrub_04` in Blender; (c) the Quaternius MegaKit bushes as stylised stand-ins. I recommend (a) first because it also supplies a beech/hazel-class tree, and (b) as the fallback.
3. **Nanite masked leaf cost** for 10 to 30 k triangle trees at 450 to 900 instances on the RTX 3060 (`stat gpu`, `NaniteStats`), and grass card overdraw at `oblique`. Needs an editor window (lock rule). Pass criterion proposal: scatter adds at most 3 ms GPU to the existing terrain scene, reported (not gated, constraint 7).
4. **Material complexity** of `PCG_Foliage_01` (instruction counts) and whether its WPO wind is usable; read in the material editor.
5. **Seedling_02 and SimpleForest triangle counts**: read once with `StaticMesh.get_num_triangles` in the first editor session.
6. **Copying engine content:** confirm a Python `duplicate_asset` or migrate of `/PCGBiomeSample/Meshes/*` into `/Game/Scatter` keeps materials and Nanite settings; otherwise enable the plugin in `ChimeraTerrain.uproject`.
7. **Poly Haven polycount mismatch:** API `polycount` differs from the glTF (20 to 65 times for grass). Confirm in Unreal that the imported triangle count equals the glTF count, not the API one (a one-line check after import).
8. **Tree download size:** 600 MB for two trees is acceptable once and git-ignored, but check disk before the fetch (needs about 1.2 GB with extracted textures and Blender intermediates).

## 7. Citations

- Poly Haven licence: `https://polyhaven.com/license`; API README `https://raw.githubusercontent.com/Poly-Haven/Public-API/master/README.md`; API ToS `.../master/ToS.md`.
- Poly Haven API data (MEASURED 2026-10-01): `https://api.polyhaven.com/assets?t=models` (521 models), `https://api.polyhaven.com/info/{id}`, `https://api.polyhaven.com/files/{id}` for the 30 ids; 1k glTF JSON read per id.
- UE EULA: `https://cdn2.unrealengine.com/Unreal+Engine%2Ffaq%2FUnrealEngineEULA_for_Publishing_v15-b9955ec2954f0e09fade11b2d9acecfc65cb6ab1.pdf` (Sections 1, 1(A)(a), 25); Fab licences `https://dev.epicgames.com/documentation/en-us/fab/licenses-and-pricing-in-fab`.
- Quaternius: `https://quaternius.com/packs/stylizednaturemegakit.html`, `https://quaternius.itch.io/stylized-nature-megakit`, `https://quaternius.com/packs/ultimatenature.html`. Kenney: `https://kenney.nl/assets/nature-kit`.
- Local: `D:/Epic Games/UE_5.8/Engine/Plugins/Experimental/{PCGBiomeSample,ProceduralVegetationEditor,ImpostorBaker,DynamicWind}`; `D:/Projects/Chimera-Unreal/ProjectChimera/LookTest/{PLAN.md,logs/editor_3.stdout.log,out/fps_summary.json,tools/lt_common.py}`;
  `D:/Projects/Chimera-Unreal/ChimeraTerrain/{ChimeraTerrain.uproject,VENDOR.md,Config/DefaultEngine.ini,Textures/manifest.json,Source/ChimeraTerrain/Game/RtsCameraPawn.cpp}`;
  `D:/Projects/Project_Chimera/docs/unreal-move/trial-checks/{plan-c-runtime-terrain.md,research/r3-ue-host-and-instancing.md}`.
- Previews (git-ignored): `D:/Projects/Chimera-Unreal/ChimeraTerrain/Out/refs/scatter/` (`ph_*.png`, `contact_sheet_polyhaven.jpg`, `contact_sheet_cc0_packs.jpg`, `looktest_A_noLumen_gameplay_1280.jpg`, Quaternius and Kenney previews).
