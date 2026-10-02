# R9: Free routes to Manor Lords-class ground and scatter assets (and what Megascans costs instead)

Research only, 2026-10-02 (revision 2, after a critique pass; review log in section 8). Nothing was installed, downloaded as a model,
bought, claimed or signed in to. Previews are in `D:/Projects/Chimera-Unreal/ChimeraTerrain/Out/refs/free_routes/` (git-ignored).
**MEASURED** = read from a file, API, listing page or licence text in this session. **UNVERIFIED** = inferred; the check that settles it is
named. **Estimate** = my judgement from the named evidence, not a score. Builds on `r8b-scatter-assets.md` and the plan of record
`plan-c-scatter.md` (§3.6 assets); it does not repeat their tables. P1 candidate ids and their filters: `r9-p1-candidate-ids.json` (beside this note).

## 0. Answer

1. **Prices.** Most of the Megascans fallback costs $0. All 37 Quixel Megaplants are $0 on both licence tiers (re-verified in the critique pass; 7 of them again here). They are mostly
   European trees and shrubs (English Oak, Silver Birch, European Beech, Common Hazel, Elder, Norway Spruce...) plus a few others such as
   Ginkgo, Giant Bamboo and Japanese Cypress. The earlier price workflow (`wf_0a8ee40c`) counted about 1,700 $0 listings among about 22,000
   Megascans listings; that count was not re-checked. A minimum set for every class is **$0**; a broader set is **$35.87 Personal / $359.87
   Professional** (every listing and uid in §2.2). Free is a base price, not a sale (`discountedPrice` is null on every listing checked).
2. **The real cost of Fab is the licence, but only for Standard-licence items.** Fab's EULA bars making Standard-licence content available in
   "world- or level-editing tools". In Chimera, users paint ground and scatter in the map editor, so this hits every Quixel item, free or paid.
   Free **CC-BY** items on Fab are a different licence ("You are not granted a Standard License to such Content") with no editor clause.
   CC0 (Poly Haven, ambientCG) has none either.
3. **Expected coverage (estimates; the pilot measures them).** The only measured score so far is **5/10, ground only** (art director,
   `plan-c-scatter.md` §3.9). CC0 is expected to reach Manor Lords level at the RTS camera for five of seven classes: meadow and dry ground,
   dirt path, rock, grass clumps, wildflowers. **Shrubs and broadleaf trees are the weak spot.** For trees, the best-looking free candidates
   are now **CC-BY trees on Fab** (KV Beech Tree 01, Next Spring's HighPoly Tree, the European Forest Environment kit). That rests on vendor
   renders only, none seen at our camera. The UE 5.8 Procedural Vegetation Editor (PVE) samples are an **unseen** candidate. They are one
   generic deciduous tree, one shrub, one conifer and one plant; the beech, hazel, aspen and maple sets are growth skeletons with no leaves
   or bark. No free shrub found yet matches Manor Lords' dense hedges.
4. **Local generation.** Yes, this PC can produce assets. Worth running: the procedural generators (PVE, EZ-Tree, Blender scripts for grass
   cards, bushes and decimation), your own photo capture through RealityScan, and albedo-to-normal tools (DeepBump, Materialize). SDXL
   seamless textures are **untested for photoreal**: our only SDXL ground texture was prompted to look stylised. They are also not needed
   while CC0 photo scans cover the ground. AI image-to-3D does not fit foliage: Hunyuan3D-2 and TripoSG make closed solids, and TRELLIS.2,
   which can make leaves, needs 24 GB of VRAM and a non-commercial renderer. FLUX.1 dev's licence bars commercial use.
5. **Recommendation.** Run the pilot in section 4 with CC0, CC-BY and the PVE samples. Ship in the map editor only CC0, CC-BY and
   project-original content until Epic answers the editor question (section 6, decision 1). **If Alec accepts that rule, the Megascans prices
   do not matter for anything the editor paints or places.** Each slot's fallback is then another free, non-Fab route (listed in §4). Fab
   content stays usable in trailers and other rendered video (Fab EULA 4(b)).

## 1. What was re-verified

| claim | how | result |
|---|---|---|
| Fab licence clauses | https://www.fab.com/eula in headed Chrome (page says "Last updated: October 1st, 2024") | 2(a), 2(c), the Section 6 heading, 4(b), 4(c), 6(b)(iii), 6(b)(vii) and 7(a) are quoted in §2.2 and §5 |
| Fab prices, licence group, NoAI flag | listing API `/i/listings/<uid>` from inside the page: 63 listings (`Out/refs/free_routes/megascans_fab/_prices_2026-10-02.json`) | all Quixel and Project Nature items are `standard` and `isAiForbidden: true`; prices are in §2.2. One (Raspberry `e1ad5dd1`) did not resolve and is not re-checked |
| CC-BY trees and shrubs on Fab | listing API for 12 free listings (`Out/refs/free_routes/ccby/_listings.json`); listing pages; gallery previews viewed (`ccby/sheet_ccby_candidates.jpg`) | licence `cc-by`, which the pages show as "Creative Commons Attribution (CC BY 4.0)"; `isFree: true`, `isAiGenerated: false` on all 12 |
| Engine versions | the "Unreal Engine versions" line on each listing page | European Beech and Norway Maple packs 5.1–5.6; Megaplants English Oak 5.7–5.8; KV Beech 5.6; European Forest Environment 5.0–5.8; HighPoly Tree 5.3–5.8; Project Nature grass 4.19–4.27 and 5.0–5.4; Greenleaf shrubs 5.4–5.8 |
| CC BY 4.0 | https://creativecommons.org/licenses/by/4.0/legalcode.en and https://creativecommons.org/faq/ | grant, attribution and no-DRM clauses quoted in §5 |
| Legacy Megascans | Epic forum post by Yasir Q, 2024-11-22 (https://forums.unrealengine.com/t/access-legacy-megascans-on-bridge-and-quixel-com/2142664); Epic Content License https://www.unrealengine.com/eula/content | quoted in §5 flag 3 |
| Unreal Engine EULA | https://www.unrealengine.com/eula/unreal (current web text) | definitions, 4(c) and the Engine Tools rule quoted in §5 flag 4 |
| PVE content on disk | `Engine/Plugins/Experimental/ProceduralVegetationEditor/Content/SampleAssets/` | `StarterContent/` holds DeciduousTree_01 (Leaf_Twig_01-05, bark and foliage textures), ConiferTree_01 (own twigs and textures), Deciduous_Shrub_01 (3 files, no textures of its own), Plant_01 and 10 presets. `Tree_European_Beech_01`, `Tree_Common_Hazel_01`, `Tree_European_QuakingAspen_01` and `Tree_Norway_Maple_01` hold only `Instances/*.json`: 32 growth attributes, skeleton points and primitives, no meshes, bark, leaves or materials. `Maps/Asset_Zoo.umap` exists |
| SDXL evidence | ComfyUI prompt embedded in `godot/assets/textures/terrain/grass.png` | positive "low-contrast matte stylized game ground albedo"; negative includes "photoreal high detail, high contrast" |
| CC0 licences | WebFetch of https://polyhaven.com/license and https://docs.ambientcg.com/license/ | quoted in §5 |
| Model and tool licences | SDXL `LICENSE.md`; FLUX.1 dev https://github.com/black-forest-labs/flux/blob/main/model_licenses/LICENSE-FLUX1-dev (v1.1.1); `D:/tools/hy3d20/LICENSE.txt`; HF API for Hunyuan3D-2.1, FLUX.1 schnell, Wan, TripoSG, TRELLIS.2; TRELLIS.2 README; nvdiffrast `LICENSE.txt`; GitHub for EZ-Tree, DeepBump, Materialize, Meshroom; https://www.realityscan.com/eula | quoted in §3 and §5 |
| ambientCG recount | API dump (`full_json?type=Material`, 2,013 materials) | 137 with `creationMethod == PBRPhotogrammetry` in Ground, Gravel, Rock, Moss, Pathway, Snow, Sand or Grass; 13 already screened by G0; **124 unscreened** (ids in the JSON). Do not filter on `creationMethodName`: it says "Height field photogrammetry" for every material |
| Poly Haven recount | API `assets?t=textures` (864) | 107 tagged both `terrain` and `natural`; 29 screened; **78 unscreened** (ids in the JSON). The earlier "49" could not be reproduced and is replaced |
| Fab ownership on disk | `C:/ProgramData/Epic/EpicGamesLauncher/VaultCache/FabLibrary/listings_v1.db` | 0 rows in every table at the first check. Only Alec's own view can tell |

Corrections to the earlier notes:
- `searsia_lucida` is not "4.8 m tall". The Poly Haven API size (4.78 x 2.84 x 2.34 m) is the box around all 7 variants laid out together;
  `grass_medium_01`'s box is 7.3 m and holds 14 to 32 cm clumps. Per-variant height is UNVERIFIED (read the glTF accessors).
- `Ground075` was screened in G0 (it is in `Out/refs/manor_lords/cand_cache`), although the texture note lists "Ground070 to Ground078" as new.
- The "UE-Only exception" exists only in the legacy Epic Content License, not in the Fab licence (§5 flag 3).
- Revision 1 of this note said the Fab editor ban "applies to free Fab items too". It applies to Standard-licence items, free or paid; CC-BY
  items are outside it. It also said `Ground082`; the ambientCG ids are `Ground082S` and `Ground082L`.

## 2. Per asset class: best free route, quality, licence, effort, Megascans price

Camera facts from r8b §4: at `rts80` a 25 cm grass clump is about 2 px, a 1.5 m shrub about 13 px and a 12 m tree about 105 px. So at the RTS
view, density, colour, canopy massing and shadow matter more than detail. `closeup` (3.5 m above the path) is compared with Manor Lords'
ground-level shots (`ml_07`, `ml_09`, `ml_10`). Scores below are **estimates** on the art director's 0-10 scale; the pilot measures them.

| class | best free route | expected vs Manor Lords (evidence) | output licence | effort and risk on this rig | Fab Standard fallback, Personal / Professional (uids §2.2) |
|---|---|---|---|---|---|
| Meadow and dry grass ground | Mine the 124 unscreened ambientCG scans and 78 unscreened Poly Haven terrain textures (`r9-p1-candidate-ids.json`). Keep `Grass004` for green meadow unless one ranks higher | rts80: little change for green meadow, because every CC0 grass set was already screened and Manor Lords' meadow reads through 3D blades (`compare_c7.md`). Dry grass and leaf litter can improve: `forest_leaves_04`, `dry_decay_leaves`, `Ground082S`/`Ground082L` (contact sheets viewed) | CC0 | 1 to 2 h; no code change (`Tools/fetch_textures.py` `LAYERS`). Low risk | Uncut Grass, Grass Dried, Lush Grass **$0 / $0**. Other surfaces $0.99 / $9.99 |
| Dirt path | ambientCG Ground070-074, 076-078 and Pathway001/003/004; Poly Haven `rocky_trail`, `brown_mud_dry`, `gravel_ground_01` | At par at rts80: all are photo scans, and the path is a colour and edge job our HLSL already does. Closeup: straw-flecked Ground071-073 match the Manor Lords yard dirt better than `brown_mud_02` (viewed) | CC0 | the same 1 to 2 h pass. Low risk | Forest Path, Dry Trampled Soil **$0 / $0** |
| Rock | Textures: ambientCG Rock040, Rock046L, Rock051 (lichen), Poly Haven `aerial_rocks_01/02/04` (mossy). Boulders: Poly Haven `rock_09`, `rock_07`, `boulder_01`, `rock_moss_set_01` (scans, 23 k to 124 k tris, Nanite) | At par: the same kind of scan as Megascans. Manor Lords boulders (`news_053`) are mossy grey; `aerial_rocks_*` and `rock_moss_set_01` fit | CC0 | textures in the same pass; boulders are part of scatter S1. Low risk | Rock Cliff, Layered Rock Cliff, Rough Rock surfaces **$0 / $0**. 3D rocks **$1.99-$2.99 / $19.99-$29.99** (18 sampled), one Mossy Forest Rock $0 / $0 |
| Grass clumps | Plan L0 procedural blades (project-original) for the bulk. Poly Haven `grass_medium_01` (17 variants), `grass_medium_02` (dry), `grass_bermuda_01`. Tall seed-head card clumps built in Blender from ambientCG `Foliage002`/`003`/`004` | Estimate 7 at rts80 (density and colour carry it), 5.5 to 6.5 at closeup. Poly Haven clumps are 14 to 32 cm lawn tussocks, while `ml_07` shows knee-high grass with seed heads. The ambientCG atlases are real photo seed heads (viewed `contact_ambientcg_atlases.jpg`), so cards close part of that gap | CC0 / project-original | one session inside scatter S1 (Blender card builder). Risk: masked-card overdraw at `oblique` (r8b open question 3) | Project Nature "Optimized Grass Library" **$0 / $0**: 12 grass types, 110 clusters, knee-high seed heads, the closest free match to `ml_07` (viewed `sheet_project_nature.jpg`; UE 4.19-5.4). Wild Grass x2, Ribbon Grass, Dry Grass, Wheat Grass **$0 / $0**. Rough Meadow Grass, Grass Clumps x2, Tall Grass **$3.99 / $39.99** |
| Wildflowers | Poly Haven `dandelion_01`, `celandine_01` (yellow), `periwinkle_plant` (pink), `shrub_sorrel_01`; OpenGameArt Croomfolk clover cutouts (CC0); hue-shift a flower head in the material for the purple and blue Manor Lords uses | At par at rts80, where a flower is a 1 px dot and only colour and density count. Closeup: real scans (viewed `contact_sheet_polyhaven.jpg`). No blue or purple species exists in CC0 | CC0. The clover pack is "remixed from previous CC0 texture releases from OpenGameArt and CC0/Kopimi photos from Wikimedia Commons" (provenance noted) | low; part of scatter S1. Low risk | Project Nature "Meadow Flowers" (20 kinds, 96 clusters), Field Poppy, Yellow Archangel, Violet Wood Sorrel, Megaplants Wood Anemone **$0 / $0**. Chamomile, Blue Lupin **$3.99 / $39.99** |
| Shrubs | 1) Poly Haven `searsia_lucida` (7 variants, 6 k to 113 k tris) and the plan's composite bush, clustered into hedgerows. 2) CC-BY: the bushes in Blackridge's "European Forest Environment" kit (`c849e5f2`, UE 5.0-5.8, 29 meshes). 3) PVE `PVE_Deciduous_Shrub_01` (engine sample), only if P4's render step passes | Estimate 6 to 7 at rts80 if massed, 5 to 6 at closeup. `searsia_lucida` reads as a believable leafy scan, but open and sparse, and it is a South African species (viewed `contact_ph_shrubs_trees.jpg`). Manor Lords hedges (`ml_01`) are dense dark masses. The kit's bushes appear only at road-scene scale in its gallery (viewed). CC-BY "Kalmia latifolia" (`32968342`) is an American flowering ornamental, not a hedge plant (viewed). No PVE shrub render has been seen | CC0; CC BY 4.0 (§5 flag 2); PVE under the UE EULA (§5 flag 4) | medium: one Blender session. CC-BY: Alec adds it to his Fab library (decision 3). PVE: Experimental plugin, export by hand in the editor UI | Elderberry, European Spindle, Megaplants Elder, Bilberry and Common Heather **$0 / $0** (Bilberry and Heather are knee-high dwarf shrubs, viewed). Greenleaf Vision "Free Shrubs Pack" **$0 / $0** (11 Nanite shrubs, UE 5.4-5.8). Blackberry, Rose Hip **$4.99 / $49.99** |
| Trees | 1) CC-BY: KV Beech Tree 01 (`fb155516`; UE 5.6; Nanite; LowPoly 206,100 tris, HighPoly 1,638,848; seasons and WPO wind), Next Spring "HighPoly Tree Model" (`2f1f43fe`; UE 5.3-5.8; "about 500k triangles"), European Forest Environment (`c849e5f2`; oak, birch, pine). 2) Poly Haven `tree_small_02` (broadleaf, 2.06 M tris) and `fir_tree_01`, decimated to 30 k or fewer. 3) PVE `PVE_Deciduous_Tree_01` and `PVE_Conifer_01` with their generic foliage. The beech, hazel, aspen and maple sets need foliage and bark from elsewhere (ambientCG LeafSet/Foliage atlases and Bark sets, CC0; or Megaplants, Fab Standard) | CC-BY: the vendor renders read as photoreal broadleaf crowns, the best free evidence (viewed `sheet_ccby_candidates.jpg`). None are seen at rts80, and their triangle counts exceed r8b's 8-30 k budget, so they need Nanite or decimation. Poly Haven: estimate 6 to 7 at rts80. It is a believable crown (viewed), but one small species: the plan scales it about 4x, so its leaves grow too, and one species repeats across a forest. PVE: no render seen; "same generator as Megaplants" says nothing about leaf or bark quality | CC BY 4.0; CC0; PVE under the UE EULA | CC-BY: Alec adds it to his library; the KV Beech is listed for 5.6, so 5.8 loading is UNVERIFIED (open it in a 5.8 scratch project). Poly Haven: Blender decimation of a 2 M-tri mesh needs a few GB of RAM (7.9 GB free at the last check). PVE: plugin and "Nanite Foliage (Experimental)" off by default; export is a modal dialog, not scriptable (`PVEditor.cpp:802-849`); Shipping packaging unproven | Megaplants English Oak, Norway Spruce, Silver Birch, Baltic Pine (and European Beech, Common Hazel, European Aspen, European Hornbeam, Black Alder) **$0 / $0**; the English Oak listing says UE 5.7-5.8. Legacy Megascans packs European Beech, Norway Maple, European Hornbeam, European Black Alder, Common Hazel **$0 / $0**, listed for UE 5.1-5.6 (5.8 UNVERIFIED) |

### 2.1 Quality evidence for the CC-BY and Fab Standard options

- **Trees, CC-BY (viewed `ccby/sheet_ccby_candidates.jpg`).** KV Beech reads as a broad-crowned European beech with spring-to-winter
  variants. Next Spring's HighPoly Tree shows dense broadleaf crowns in an Unreal scene. European Forest Environment shows game-grade oaks,
  birches and pines in editor screenshots and a road scene. Acer freemanii (`115f4a08`) is a narrow urban street tree, archviz, "Polys:1007238",
  3ds Max/Blender only, so it is a poor fit. Quercus rubra (`3ccde119`, FBX/Maya) is a believable crown rendered on grey. All are vendor
  renders; none is compared at rts80 yet.
- **Trees, Fab Standard.** The Megaplants English Oak and Silver Birch renders (`sheet_trees_megaplants_and_packs.jpg`) are studio shots on
  white. They look photoreal but are not compared at rts80. The European Beech gallery's forest floor is **not** evidence for that pack: the
  listing says "Rendered images include elements (e.g., terrain materials, ground and additional 3D scatter) for visualization only that are
  not included in the pack." The pack is a 2023 one with "unique LODs ... and various wind implementations (both simple and Pivot Painter)",
  not Nanite foliage.
- **Grass clumps, Fab Standard.** Wild Grass is a taller, mixed green-and-dry clump with seed heads (`sheet_plants_free.jpg`). At rts80 the
  difference from CC0 is mostly density.
- **Ground, Fab Standard.** Uncut Grass is strand-level 8K (`sheet_surfaces_free.jpg`), but at 80 m the 3D grass carries the read.

### 2.2 Megascans and Fab prices (MEASURED 2026-10-02, listing API; URL `https://www.fab.com/listings/<uid>`)

| set | listing | uid | Personal / Professional |
|---|---|---|---|
| minimum | Uncut Grass (surface) | 69f0e5f2-0fce-43f5-b737-b3d18de2eb62 | $0 / $0 |
| minimum | Grass Dried (surface) | 69f9045d-1fc7-4316-82fb-12252719bc69 | $0 / $0 |
| minimum | Forest Path (surface) | 6614ed94-d322-48eb-aa7c-658c2d7db47e | $0 / $0 |
| minimum | Forest Floor (surface) | dae56d09-862c-4f77-9e72-a41281c49633 | $0 / $0 |
| minimum | Rock Cliff (surface) | 22e499dd-3e5c-4422-8662-2aced7a8bac4 | $0 / $0 |
| minimum | Layered Rock Cliff (surface) | 79b84e82-541b-49db-869b-ce3e83d292f5 | $0 / $0 |
| minimum | Wild Grass | 50d9a417-73ed-4132-9421-6be3d4f7432e | $0 / $0 |
| minimum | Wild Grass | 5848d16a-f6ab-45a7-b69f-fe21dcbe4669 | $0 / $0 |
| minimum | Ribbon Grass | 6b7af6c8-e24b-4beb-a943-4ddb35d2db87 | $0 / $0 |
| minimum | Field Poppy | 66cb2706-bc30-4f26-92ec-cad48723bd4a | $0 / $0 |
| minimum | Yellow Archangel | 2ddbbce2-5424-4585-988b-535baff65cee | $0 / $0 |
| minimum | Elderberry | d72dd7bb-d514-4a14-8b4b-3333c99057fb | $0 / $0 |
| minimum | European Spindle | f3c45cd9-f301-4358-8699-b5bbf436c851 | $0 / $0 |
| minimum | Megaplants: English Oak | 83642c38-7661-4df1-8629-0422e1898d26 | $0 / $0 |
| minimum | Megaplants: Silver Birch | 94262633-61ed-4f4b-993e-dbc24af3e6ba | $0 / $0 |
| minimum | Megaplants: Baltic Pine | a2b04e81-5075-479f-a9d2-4940022f330a | $0 / $0 |
| minimum | Megaplants: Norway Spruce | f87364c9-10a2-4834-8566-a0291c717758 | $0 / $0 |
| comfortable add-on | Rough Meadow Grass | 82b4a272-5e9a-4113-a393-e513772da73f | $3.99 / $39.99 |
| comfortable add-on | Grass Clumps | 3818b256-85ec-4c6c-9dcd-596f9ae56bbd | $3.99 / $39.99 |
| comfortable add-on | Tall Grass | cce05344-1e2b-4cba-a022-28fc5cab1781 | $3.99 / $39.99 |
| comfortable add-on | Chamomile | 60c8edcc-9503-4160-bc58-167d213f54ac | $3.99 / $39.99 |
| comfortable add-on | Blue Lupin | e652a744-2003-407e-abdf-745de209776a | $3.99 / $39.99 |
| comfortable add-on | Blackberry | 5839c15b-97f3-452f-9ddd-3318ea2d44b4 | $4.99 / $49.99 |
| comfortable add-on | Rose Hip | 07e36f85-9470-44ef-a799-f7634aa0e62d | $4.99 / $49.99 |
| comfortable add-on | 6 surfaces, not yet picked ("e.g. 3 meadow variants, a path and 2 rocks" in `wf_0a8ee40c`) | to pick | 6 x $0.99 / 6 x $9.99 |
| other $0 | Dry Grass `472c99c7-2d67-4190-9ad4-3bb04cfc80f3`; Wheat Grass `9070350f-dea8-40ec-ba57-0777a2c1fb24`; Violet Wood Sorrel `bdcf3408-c6bc-4541-9fd7-378848b27705`; Dry Trampled Soil `e9c8521d-0d32-46ee-8607-bca13605159a`; Rough Rock `9544433b-ee7f-4da6-acb2-bd9f4a57c762`; Lush Grass `9b7b7bfa-5654-4486-bf8b-46ce6e30fb10`; Mossy Forest Rock `957f0b2e-59a4-42dd-976f-11edc1bb7bc4`; Megaplants Bilberry `5ed2fbb3-9da1-4718-847e-5b02dd2beedc`, Common Heather `5d88b60a-524c-4774-a9d7-7e1cd4510be6`, Wood Anemone `636348cc-22c5-49ea-b93c-ff60af8384c7`, European Beech `cefe5722-9c31-4aa2-9ee2-e5426610d5e6`, Common Hazel `27ded0d2-8bb2-4e44-a795-3a35725af218`, European Aspen `ffa90e1a-e420-43d6-ade3-daa4bc189a0a`, European Hornbeam `43679e75-6ed0-4b03-9288-9e2bc49376d2`, Black Alder `9257b627-e37a-4112-82c7-4a99c1a8aec6`, Elder `fe7a58b3-19fe-4249-b496-2214b32650b3` (these six from the critique pass's search re-fetch); legacy European Beech pack `d11cc01d-9422-41b7-950f-416c9ce79caf`; Norway Maple `1f65d477-f44e-4ccb-95c1-788c99e33b56`; European Hornbeam `c6f917b6-ffcb-4b86-9d9f-5274ba7f6a8e`; European Black Alder `9de7ce19-5813-42d2-a5f0-5e6447006f72`; Common Hazel `81bc7ba6-4686-4f94-9d2b-83eb1fdc4079`; Project Nature Grass Library `8b68642e-35f4-438e-82b4-799fc2228303`, Meadow Flowers `e648b1bd-11f6-4895-bd43-15e399c80297`, Spruce Forest `f8044501-17a2-498f-b198-5f1bc71ee87a`; Greenleaf Vision Free Shrubs Pack `7ca465ab-fb9c-4d6b-bddb-82c20f604657` | | $0 / $0 |
| other paid | Grass Clumps (second listing) `d22b2fc3-9389-4d39-894c-5369a7e49ddf` | | $3.99 / $39.99 |
| 3D rocks (18 Quixel sampled) | e.g. Lichened Forest Boulder `710d5a75-8ae9-4ef8-8b4d-43618452ca0f`, Forest Boulder `97f473d2-8ef8-4414-a554-db85f3f1d911`, Icelandic Mossy Rock `de298beb-865c-4417-8dc6-2cf6ea49cfb6`, Tundra Boulder `4a368dcd-f677-40b8-9c83-4d53c05ecb11` (all uids in the JSON) | | $1.99-$2.99 / $19.99-$29.99 |

Totals: minimum set (17 listings above) **$0 / $0**. Comfortable set = minimum + 7 named add-ons + 6 unpicked surfaces = **$35.87 /
$359.87**. Professional is 10 times Personal for plants and surfaces. Raspberry (`e1ad5dd1`, $0 in `wf_0a8ee40c`) did not resolve this pass.

Tiers. Fab EULA 2(a): "You are only eligible for a Personal - Reference Only tier or Personal tier if, at the time of the Transaction you,
together with any controlling entity and other entities under common control with you, have not generated more than $100,000 USD in gross
revenue from your commercial activity in the digital content industry in the last 12 months. For purposes of this calculation, revenue
includes any advances received or other funds raised." That counts crowdfunding and a publisher advance. The Fab summary adds: "There is no
need for upgrading from Personal to Professional tier if you cross the revenue threshold after the purchase", and "Both pricing tiers
(Personal and Professional) grant you the same scope of rights". For $0 items, claim at Professional so eligibility never comes up.

## 3. Local generation pipelines on this PC

| pipeline | on disk? | makes | output licence | evidence | verdict |
|---|---|---|---|---|---|
| UE 5.8 Procedural Vegetation Editor (PVE) | yes, `Engine/Plugins/Experimental/ProceduralVegetationEditor`; not enabled in `ChimeraTerrain.uproject` | 4 samples (deciduous tree, shrub, conifer, plant) with generic foliage; 10 presets; 4 species growth sets without foliage or bark | UE EULA (engine content, §5 flag 4) | no render seen; `Maps/Asset_Zoo.umap` is the cheapest look | **candidate, render first** (P4 step 0). Export is a UI action, so a few species by hand |
| EZ-Tree | no (standalone web app or three.js library; nothing to install to try it in a browser) | parametric trees with leaf cards, exported as GLB | MIT (github.com/dgreenheck/ez-tree); bark textures are ambientCG CC0 per `textures/LICENSE.md`; "leaf textures ... are licensed under the project's own license" | its own card (viewed `eztree_og_960.jpg`) reads as a game tree, below Manor Lords | fallback tree generator; could be fed CC0 photo leaf atlases |
| Blender 4.5.10 headless scripts | yes | grass cards from ambientCG atlases, composite bushes, decimated scan trees, foliage assignment for PVE skeletons | "What you create with Blender is your sole property" (blender.org/about/license) | the plan's §3.6 tooling | **yes, the workhorse** |
| Blender tree add-ons (Sapling, Modular Tree) | no (needs an extension install) | parametric trees with leaf cards | add-on code is GPL; output is yours (GNU FAQ, below) | MTree page thumbnail only | fallback if the others fail |
| Own photo capture + RealityScan or Meshroom | no tool installed; needs a camera and outdoor time | ground, bark, stumps, rocks, flat-scanned leaf atlases: Manor Lords' own method | RealityScan: free "During any period that you ... have generated less than $1,000,000 USD in gross revenue over the last 12 months"; "Your use of the Software does not grant to Licensor any right, title, or interest in any of the content that you produce" (EULA §2(b)(i), §6). Meshroom: MPL-2.0 (`LICENSE-MPL2.md`) | none here yet | worth one trial for bark and leaf atlases if CC0 runs short |
| DeepBump (Blender add-on), Materialize | no | normal and height maps from one albedo (Materialize also smoothness and AO) | GPL-3.0 tools (GitHub API). GNU FAQ: "copyright law does not give you any say in the use of the output people make from their data using your program" | none here | closes the "albedo only" gap of any generated or photographed texture |
| SDXL + seamless tiling (ComfyUI) | yes | tileable textures, plant card images | OpenRAIL++-M: "Licensor claims no rights in the Output You generate using the Model... No use of the output can contravene any provision as stated in the License" (ties output to the Attachment A use restrictions, none of which touch ground textures) | **no photoreal test.** Our only SDXL ground texture (`grass.png`) was prompted "low-contrast matte stylized game ground albedo" with "photoreal high detail" in the negative, for the June cel look | not needed while CC0 scans cover ground; one photoreal run (queue count, not batch_size) only if P1 finds a gap |
| FLUX.1 dev Q5 GGUF | yes | images | non-commercial model (§5 flag 9) | Flux seamless LoRA samples read as fur and CGI (viewed `compare_fluxlora_vs_grass004.jpg`) | **do not use for anything shipped** |
| FLUX.1 schnell | no (7.7 GB download) | images | Apache-2.0 | none on this rig | untested; not needed |
| Hunyuan3D-2 shape and paint | yes | closed, watertight meshes | territory and user-count clauses, §5 flag 8 | our units: limbs fuse into blobs (memory `chimera-asset-shape-quality-root-causes.md`) | **no** for foliage |
| TripoSG | no | closed, watertight meshes | MIT | no plant evidence | no for foliage |
| TRELLIS.2 | no | meshes, including "Open Surfaces (e.g., clothing, leaves)" (README) | code MIT; its README sends rendering to nvdiffrast, whose licence 3.3 says it "only may be used or intended for use non-commercially" | README: "An NVIDIA GPU with at least 24GB of memory is necessary" | **no**: 24 GB VRAM (we have 12) and a non-commercial dependency |
| Wan 2.2 orbit video, then photogrammetry (Alec's idea) | Wan 2.1/2.2 GGUFs and workflows in ComfyUI; no photogrammetry tool installed | a boulder or stump mesh from a generated 360° orbit | Wan: Apache-2.0, "We claim no rights over the your generate contents" (model card); then RealityScan or Meshroom as above | none: video models are not guaranteed multi-view consistent, and no run exists here. **RAM risk:** the Wan 2.2 I2V A14B Q4_K_M experts on disk are 9.7 GB each (19.4 GB for the pair, before the text encoder) against 16 GB of RAM, the same swap-freeze class recorded for FLUX fp8; the single Wan 2.1 I2V 14B 480p Q4_K_M (11.3 GB) is lighter | optional experiment for solid objects only, on an otherwise idle PC. CC0 scanned rocks already cover the class |

## 4. Pilot: prove the free routes before committing

**Scope.** Four slots: one ground set, one grass clump set, one shrub, one tree. It reuses the S1 look map, the `rts80_full`, `oblique` and
`closeup` shots, `Tools/look_measure.py` and `Tools/composite.py`, and the scatter plan's S1 tooling (`fetch_scatter_assets.py`,
`prep_polyhaven.py`, `make_bush.py`). Placement is a throwaway editor Python script that puts static instanced meshes on a 60 x 60 m plot inside
the `rts80` frame. It does not need the runtime scatter system (S2 and later). The pilot ships nothing, so it may test any route whose files
Alec has added; the licence rule decides only what ships.

**References.** RTS: `ml_01`, `ml_08`, `press/press_03`. Ground level: `ml_07`, `ml_09`, `ml_10`. All in
`D:/Projects/Chimera-Unreal/ChimeraTerrain/Out/refs/manor_lords/`. Third-party pixels stay in `Out/`.

| step | what | candidates, in fallback order (all free, non-Fab-Standard) | measured by |
|---|---|---|---|
| P1 ground set | rerun G0's colour ranking (`candidates.json` method) on the ids in `r9-p1-candidate-ids.json`; pick meadow, dry grass, dirt path, rock; swap `LAYERS` | the next-ranked CC0 id per layer | `look_measure.py` against the `compare_c7.md` targets, plus the art director, ground only, against G1 round 2 |
| P2 grass clump set | L0 procedural blades vs Poly Haven `grass_medium_01` (3 meshes) + `grass_medium_02` vs ambientCG seed-head card clumps, at the plan's density and 2 to 3x scale | L0 → ambientCG cards → Poly Haven clumps, or a mix | paired scatter-on and scatter-off shots |
| P3 shrub | `searsia_lucida` (2 variants, ≤ 20 k tris) and the composite bush as a hedgerow, vs the European Forest Environment bushes (CC-BY) | composite bush → CC-BY kit bushes → `PVE_Deciduous_Shrub_01` (only if P4 step 0 passes) | same |
| P4 step 0 | in a scratch copy with the PVE plugin and "Nanite Foliage (Experimental)" enabled, open `Asset_Zoo.umap` (or place `PVE_Deciduous_Tree_01` and `PVE_Deciduous_Shrub_01`) and take one `rts80` and one `closeup` shot | — | the art director, against `tree_small_02` in the same frame. Continue with PVE only if it scores at least as well |
| P4 tree | a grove of 40 trees: decimated `tree_small_02` vs the best one or two CC-BY trees (KV Beech Tree 01, HighPoly Tree Model) vs `PVE_Deciduous_Tree_01` as a Nanite StaticMesh (if step 0 passed). A PVE species set is tried only with foliage assigned from CC0 atlases (ambientCG LeafSet/Foliage, ambientCG or Poly Haven bark) | CC-BY tree → PVE with CC0 foliage → Poly Haven | same, plus the plan's M4 (tree-shadow luma) and M5 (canopy massing) |

**Judging.** The Opus xhigh art director scores each slot 0-10 at `rts80` and `closeup` against the references, on paired on/off shots of the
same build, with the numbers beside it (the plan's §3.9 rubric). The main session sends each composite to Alec.

**Gate, per slot, checked in this order after every scoring round.**
1. **Switch at once:** `rts80` below 5.5, or `closeup` below 5. Move to the slot's next free route (table above); no tune round.
2. **Keep:** `rts80` ≥ 7 and `closeup` ≥ 6, inside the plan's triangle budget (r8b §3) or as a Nanite mesh.
3. **Tune for closeup:** `rts80` ≥ 7 and `closeup` 5 to 6. One round aimed at closeup (variant, scale, material), then keep the better round.
4. **Tune once:** `rts80` 5.5 to 7. One round of palette, scale, density and variant choice, then apply rules 1 to 3. If it is still under 7,
   switch to the next free route.
5. **Stop:** when a slot's free routes are used up, keep the best scorer and report the gap with its score.
6. **Fab Standard and paid listings:** only if decision 1 ends with Epic allowing Fab content in the map editor. Then test the slot's $0 Fab
   candidate (§2) and keep it if it scores at least 1 point higher at `rts80`; buy a paid listing ($0.99 to $4.99 Personal) only for a gap the
   art director names. Until then the Megascans prices play no part in the pilot.

**Cost.** About two sessions (P1+P2, then P3+P4) plus P4 step 0. Unreal and Blender need the RAM: 7.9 GB was free at the last check, with other
sessions running, so the pilot runs serialised with them. Downloads about 120 MB at 1k for the CC0 assets (no 487 MB fir), plus the CC-BY
files Alec adds.

## 5. Licence flags (quoted)

1. **Fab Standard licence vs the map editor (highest priority).** Fab EULA Section 6 is headed "STANDARD LICENSE RESTRICTIONS", and its list
   opens "For any Content licensed to you under a Standard License, you may not:". 6(b)(iii): "allow any third party to incorporate Content
   into their own products, services, or other projects (this means, for example, that you may not make Content available in world- or
   level-editing tools or templates or other modeling tools that allow works to be exported)". 4(c): you "must restrict end users from
   extracting or otherwise using Content outside of the Project". This covers every Standard-licence item, free or paid, including every
   Quixel listing checked (`group: standard`). It does not cover CC-BY items. EULA 2(c): "When you complete a Transaction for such Content, the
   Content Licensor grants you a license for the Content under the open source license identified ('Alternative License'). You are not granted
   a Standard License to such Content." Whether in-game maps that only run inside Chimera count as "works ... exported" is untested. Not legal advice.
2. **CC BY 4.0 (free CC-BY items on Fab, including old Sketchfab uploads such as 4DATUM's 2016 dead-beech scan `250beff5`).** Grant: "reproduce
   and Share the Licensed Material, in whole or in part; and produce, reproduce, and Share Adapted Material", with no commercial-use limit and
   no editor clause. Duties: 3(a)(1) attribution (creator, copyright notice, licence notice, link, and "indicate if You modified the Licensed
   Material"); 3(a)(2): "You may satisfy the conditions in Section 3(a)(1) in any reasonable manner based on the medium, means, and context in
   which You Share the Licensed Material." 2(a)(5)(C): "You may not offer or impose any additional or different terms or
   conditions on, or apply any Effective Technological Measures to, the Licensed Material if doing so restricts exercise of the Licensed Rights
   by any recipient". The CC FAQ: "The use of any effective technical protection measures (such as digital rights management or 'DRM') by
   licensees to prevent others from exercising the licensed rights is prohibited." What this means for Chimera:
   - a credits page that names each CC-BY asset, its author, link and licence, and says it was modified;
   - no pak encryption over CC-BY assets, and no clause in Chimera's own end-user licence that forbids extracting them;
   - provenance risk: a CC-BY upload can be someone else's ripped work. `isAiGenerated` is false on all 12 checked, but that is the seller's
     own flag. Prefer sellers with a history, and record the listing URL, author and licence in the manifest.
   Not legal advice.
3. **Legacy Megascans may be the exception, but only Bridge or Quixel.com claims.** Epic Content License 5(c)(iii) has the same editor ban
   and adds "provided, however, this restriction does not apply to UE-Only Content". Megascans Content Addendum 1(c): "Megascans Content that
   you acquire from Epic while your account is enrolled in an Unreal Engine plan may only be used and shared as UE-Only Content. You qualify
   for an Unreal Engine plan if you have entered into an Unreal Engine End User License Agreement with Epic that is still active and valid."
   1(e): content from an account on no Megascans plan "may only be used for your internal evaluation purposes". Epic's forum post
   (Yasir Q, 2024-11-22) says claims on Quixel.com or Bridge "are available under the Epic Content License", while "Claiming all the free
   Megascans on Fab gives you access to an updated and improved catalog of Megascans under the Fab Standard License". It also says "upgraded
   versions of legacy Megascans assets claimed from Bridge or Quixel.com from November 22, 2024 to December 31, 2024 will be available in My
   Library on Fab", and "You will still have access to past acquisitions ... via Bridge and Quixel.com". So the test is where and under which
   plan a claim was made, not whether it was made before 2025. The Fab Library shows Fab-delivered versions, not the UE-Only legacy files. ECL
   7(b): "If we make changes to this Agreement, you are not required to accept the amended Agreement, and this Agreement will continue to
   govern your use of any Licensed Content you already have access to", but "If you accept an amended Agreement, the amended terms will apply
   to your use of Licensed Content including Licensed Content that you downloaded under any prior version". The current ECL still keeps the
   UE-Only exception. Whether Bridge and Quixel.com still serve legacy downloads in October 2026 is UNVERIFIED.
4. **PVE engine samples (engine content).** UE EULA: "The Licensed Technology ... includes Engine Code, Examples, and Starter Content", and
   "'Starter Content' means the code, artwork, or other content made available by us and installed to your computer in connection with Unreal
   Engine". The plugin's sample assets fit that. 4(c): end users may use Licensed Technology "solely (1) as incorporated in the Product in
   object code as an inseparable part of the Product, (2) to the extent necessary for end users to make permitted uses of the Product ...
   You may not permit your end users to incorporate any Licensed Technology into their own products, services, or other projects."
   **How it differs from Fab:** Fab names "world- or level-editing tools" as a banned example; the UE EULA does not. It lets end users use
   engine content "to the extent necessary ... to make permitted uses of the Product", and every UE game ships Licensed Technology that its
   players run, in-game editors included. **What is the same:** whether a user-made map is one of "their own ... projects". The same written
   question goes to Epic for both (decision 1). Separately: "If your Product includes Engine Tools, you may only Distribute it through either
   the Fab Marketplace or through a fork of Epic's GitHub UnrealEngine Network". Chimera's map editor must stay runtime code, with no
   `Editor`- or `Developer`-folder modules in the shipped build. The plugin's `CreatedBy` is blank, so texture provenance is UNVERIFIED.
5. **CC0 has no such limit.** Poly Haven: "You can use our assets for any purpose, including commercial work... or even in a product you sell."
   ambientCG: "You can copy, modify, distribute and perform the assets, even for commercial purposes, all without asking permission."
6. **NoAI.** Every Fab Standard listing checked has `isAiForbidden: true`. Fab EULA 6(b)(vii) bars using NoAI Content "in datasets utilized by
   Generative AI Programs", "in the development of" them, or "as training inputs". Never feed Megascans or Megaplants pixels into SDXL, Flux,
   Hunyuan or Wan. The 12 CC-BY listings have `isAiForbidden: false`.
7. **Experimental.** Megaplants listing: "Megaplants and the Procedural Vegetation Editor are currently Experimental... We do not recommend
   shipping projects with Experimental features." `DynamicWind.uplugin`: "Extremely experimental dynamic wind support for Nanite foliage."
   StaticMesh export avoids DynamicWind ("StaticMesh: no wind animation support", `PVExportParams.h:57`).
8. **Hunyuan3D 2.0** (`D:/tools/hy3d20/LICENSE.txt`): "DOES NOT APPLY IN THE EUROPEAN UNION, UNITED KINGDOM AND SOUTH KOREA"; 5(c) bars
   displaying "Output or results ... outside the Territory". Line 30: "If, on the Tencent Hunyuan 3D 2.0 version release date, the monthly
   active users of all products or services made available by or for Licensee is greater than 1 million monthly active users in the
   preceding calendar month, You must request a license from Tencent". As written, it tests users once, at the model's release date, so
   Chimera is under it. Hunyuan3D-2.1 uses the same `tencent-hunyuan-community` licence (HF API). Not needed for foliage; flagged because a
   worldwide release would show its output.
9. **FLUX.1 dev** (FLUX.1 [dev] Non-Commercial License v1.1.1, https://github.com/black-forest-labs/flux/blob/main/model_licenses/LICENSE-FLUX1-dev).
   1(c): a Non-Commercial Purpose is use "not directly or indirectly connected to any commercial activities, business operations, or employment
   responsibilities", and "use (a) for revenue-generating activity ... is not a Non-Commercial Purpose". 2(b): "You may only access, use,
   Distribute, or create Derivatives of the FLUX.1 [dev] Model or Derivatives for Non-Commercial Purposes." 2(d): "You may use Output for any
   purpose (including for commercial purposes), except as expressly prohibited herein." But 4(a)(i) prohibits using "any data produced by the
   FLUX.1 [dev] Model ... for any commercial or production purposes", and 2(e) adds content-filter and AI-disclosure duties. Making assets for
   a sold game is commercial use of the model. Do not ship its output (DW-1029).
10. **Plan of record.** `plan-c-scatter.md` §3.6 says "No Epic sample content, no Fab or Quixel, no login", and its manifest check accepts only
    `CC0-1.0 | project-original | epic`. PVE samples need an amendment: the `epic` class exists, but the deps assertion allows only
    `/Game/Terrain/**`, `/Engine/**`, `/BaseMaterial/**` and `/Script/**`, so samples must be copied under `/Game/Terrain`. CC-BY needs a new
    `CC-BY-4.0` class carrying author, title, URL and a modified flag for the credits page, and needs Alec's Fab login to fetch. Fab Standard,
    if ever allowed, needs its own class and a rule that its files never leave git-ignored folders.
11. **Paragon (added 2026-10-02).** Fab's search API (`/i/listings/search?q=paragon&seller=Epic Games`, 24 character packs, Gideon
    `51935254` to Muriel `c16f2277`) lists every pack at $0 with only the `Personal` and `Professional` tiers, the Standard-licence tiers of
    EULA 2(a). So Paragon on Fab is Fab Standard content, no longer the UE-Only content of the old Marketplace (a forum thread,
    https://forums.unrealengine.com/t/fab-ue-only-content-licensing/2082870, notes the UE-Only label was dropped; no Epic reply). Flag 1 applies
    to it unchanged: anything a creator can place or pick in Chimera's editors (units, effects, props) is "Content available in world- or
    level-editing tools". Copies claimed on the old Marketplace under the Epic Content License may keep ECL 5(c)(iii)'s UE-Only exception (as in
    flag 3); UNVERIFIED which version Alec's library holds. The environment pack "Paragon: Agora and Monolith Environment" (`6f401fb5`, 528
    meshes, 56 FX, 1,152 textures) has the same two tiers; its tags are Stonework, Arena, Fantasy, Forest, Moba: a fantasy MOBA arena, not a
    temperate meadow, so it is not a scatter candidate for the Manor Lords look (style judged from tags, not from images). The `isAiForbidden`
    flag is not in the search results and the listing pages return 403 to scripts; treat Paragon as NoAI (flag 6).

## 6. What Alec must decide

1. **The editor licence rule.** Recommended: what the map editor can paint or place ships only as **CC0, CC-BY 4.0 (credited, never encrypted)
   or project-original**. Engine sample content (PVE) and Fab Standard content (every Quixel item) both wait for one written question to Epic
   (legal@epicgames.com, the notice address in Fab EULA 7(c)): "May end users place this content in maps made with our in-game editor, when the
   maps run only inside our game and never export meshes or textures?" Engine content has the better case (§5 flag 4). Under this rule the
   Megascans prices do not matter for scatter. If Fab is later allowed, `plan-c-scatter.md` §3.6 needs the amendment in §5 flag 10.
2. **Legacy Megascans check (about a minute).** Open **Bridge or quixel.com**, not the Fab Library, and look for past legacy acquisitions, and
   which Megascans plan the account was on when they were claimed. Only Bridge or Quixel.com claims made on the Unreal Engine plan are UE-Only
   Content and escape the editor ban (§5 flag 3). Claims made on Fab, including the free Fab claims of late 2024, are Fab Standard. The local
   cache `listings_v1.db` showed 0 rows.
3. **Add the CC-BY candidates to your Fab library** for P3 and P4: KV Beech Tree 01 (`fb155516`), HighPoly Tree Model (`2f1f43fe`) and
   European Forest Environment (`c849e5f2`). They are free, but adding them needs your sign-in. Accepting them also means a credits page and no
   pak encryption over them (§5 flag 2).
4. **Optional: claim the $0 Fab items now to lock the price.** Fab EULA 7(a): "Any Content you acquired (whether free or paid) prior to the
   modified terms will remain governed by the license terms applicable at the time when you acquired the Content." Claim them at the
   Professional tier, which has the same rights and avoids the Personal revenue test. This guards against a later price or terms change; it
   does **not** lift the editor clause.
5. **Accept Experimental PVE in a shipping build?** Ask only if P4 step 0 shows PVE is worth it. Megaplants carry the same status.
6. **When to run the pilot.** It needs Unreal and about 8 GB of free RAM, so after the ground-look pass or when other sessions are idle.
7. **Your orbit-video idea for rocks** (Wan 2.2, Apache-2.0, then photogrammetry): keep it as an optional experiment after P1, on an idle PC
   (RAM caveat in §3). CC0 scanned rocks (`rock_09`, `rock_07`, `boulder_01`) already cover the class.

## 7. Sources

- Fab EULA https://www.fab.com/eula (2(a), 2(c), 4(b), 4(c), Section 6, 6(b)(iii), 6(b)(vii), 7(a), 7(c)); Fab tiers
  https://dev.epicgames.com/documentation/en-us/fab/licenses-and-pricing-in-fab; listing API `https://www.fab.com/i/listings/<uid>` and listing
  pages for every uid in §2.2 and §2 (saved: `Out/refs/free_routes/megascans_fab/_prices_2026-10-02.json`, `Out/refs/free_routes/ccby/_listings.json`).
- CC BY 4.0 https://creativecommons.org/licenses/by/4.0/legalcode.en; CC FAQ https://creativecommons.org/faq/.
- Epic Content License https://www.unrealengine.com/eula/content (5(c)(iii), 7(b), Megascans Content Addendum 1(c), 1(e)); legacy Megascans
  post https://forums.unrealengine.com/t/access-legacy-megascans-on-bridge-and-quixel-com/2142664; UE EULA https://www.unrealengine.com/eula/unreal
  (definitions, 4(c), Engine Tools).
- https://polyhaven.com/license; https://docs.ambientcg.com/license/; ambientCG API `https://ambientcg.com/api/v2/full_json?type=Material`;
  Poly Haven API `https://api.polyhaven.com/assets?t=textures` and `/info/<id>`; OpenGameArt https://opengameart.org/content/clover-cutouts-improved-and-expanded.
- SDXL https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/blob/main/LICENSE.md; FLUX.1 dev (link in §5 flag 9); Wan
  https://huggingface.co/Wan-AI/Wan2.1-I2V-14B-480P and `Wan-AI/Wan2.2-I2V-A14B`; Hugging Face API for `black-forest-labs/FLUX.1-schnell`,
  `VAST-AI/TripoSG`, `microsoft/TRELLIS.2-4B`, `tencent/Hunyuan3D-2.1`; TRELLIS.2 README; nvdiffrast https://github.com/NVlabs/nvdiffrast/blob/main/LICENSE.txt.
- Tools: https://github.com/dgreenheck/ez-tree (MIT, `src/app/public/textures/LICENSE.md`); https://github.com/HugoTini/DeepBump and
  https://github.com/BoundingBoxSoftware/Materialize (GPL-3.0); https://github.com/alicevision/Meshroom (`LICENSE-MPL2.md`);
  https://www.realityscan.com/eula; GNU GPL FAQ https://www.gnu.org/licenses/gpl-faq.html#GPLOutput.
- Local: `D:/Epic Games/UE_5.8/Engine/Plugins/Experimental/{ProceduralVegetationEditor,DynamicWind}`; `D:/tools/hy3d20/LICENSE.txt`;
  `D:/tools/ComfyUI_windows_portable/ComfyUI/models/{unet,diffusion_models}` (Wan sizes); `godot/assets/textures/terrain/grass.png` (embedded
  prompt); `ChimeraTerrain/Out/refs/{free_routes,manor_lords,scatter}/`; `docs/unreal-move/trial-checks/{plan-c-scatter.md,research/r8b-scatter-assets.md}`.
- Earlier research this builds on (workflow `wf_0a8ee40c-1be`): the Megascans price enumeration (22,029 listings, about 1,700 at $0; not
  re-checked), the ground-texture route survey and the mesh route survey. Their prices used above were re-fetched as listed in §2.2.

## 8. Review log (critique pass, 2026-10-02)

| # | critique item | verdict | reason / what changed |
|---|---|---|---|
| 1 | Free CC-BY content on Fab never researched; "applies to free Fab items too" is wrong | accepted | EULA 2(c) and the Section 6 heading confirmed; 12 CC-BY listings re-fetched and their galleries viewed. Added the CC-BY route (trees, shrubs), a CC BY 4.0 flag with its attribution and no-DRM duties (which the critique did not raise) and a provenance screen. "Archviz-grade, well above CC0" is narrowed to "vendor renders, best free evidence, not seen at rts80"; Acer freemanii is judged a poor fit (urban form) |
| 2 | PVE species sets have no foliage | accepted | Verified on disk: `Instances/*.json` only. Restated in §0, §2, §3 and P4 |
| 3 | PVE quality rests on inference | accepted | "Best free route" and "scanned-looking" dropped; P4 step 0 renders PVE before any setup work |
| 4 | Fab evidence is vendor renders; the Beech gallery shows content not in the pack | accepted | Listing text quoted in §2.1; "clearly better" removed |
| 5 | SDXL grass was prompted stylised | accepted | Prompt confirmed from the PNG metadata. Took the critique's second option: no SDXL run now (shared RAM, and the gate does not depend on it); the verdict now rests on CC0 scans covering ground |
| 6 | Editor rule allows engine content with the same risk | accepted | Engine content moved out of the "ships now" list; the reason it has the better case is stated; one question to Epic covers both |
| 7 | The pilot's Fab fallback cannot fire under the rule | accepted | §0.5 and gate rule 6 say so; each slot now has its own free fallback chain |
| 8 | Legacy-Megascans check looks in the wrong library | accepted | Decision 2 rewritten (Bridge or quixel.com, UE plan); ECL 7(b) and the forum post quoted |
| 9 | §0.3 states estimates as findings | accepted | Reworded as expectations, with the measured 5/10 ground-only baseline |
| 10 | Hunyuan 1M monthly-user cap not flagged | accepted, with a correction | Quoted. The clause tests users "on the ... release date", once, not as an ongoing cap, so it does not bind Chimera |
| 11 | FLUX.1 dev claim unquoted | accepted, with a correction | Quoted 1(c), 2(b), 2(d), 2(e). The critique said outputs "as such are not restricted", but 4(a)(i) bars commercial use of "any data produced by" the model; conclusion unchanged |
| 12 | Personal tier description incomplete | accepted | 2(a) quoted in full; no-upgrade note; claim $0 items at Professional |
| 13 | 3D rock price range inconsistent | accepted | 18 Quixel rocks re-fetched: $1.99-$2.99 / $19.99-$29.99, one $0 (uids in §2.2 and the JSON) |
| 14 | Listings named only, ambiguous | accepted | Full uids in §2.2; the comfortable set itemised (7 named listings plus 6 surfaces that were never picked, now said plainly) |
| 15 | Unverified totals carried forward | partly accepted | The 22k/1.7k figure is marked "not re-checked". The ambientCG 137/13/124 reproduces exactly with the stated filter (the critique's 144 used another filter); Poly Haven's "49" did not reproduce and is replaced by 78. Both id lists are saved in `r9-p1-candidate-ids.json` |
| 16 | Small factual errors | accepted | `Ground082S`/`Ground082L`; §1 table fixed; "temperate" dropped; EZ-Tree has its own row (MIT, CC0 bark) |
| 17 | Image-to-3D claim wrong for TRELLIS.2 | accepted | Claim narrowed to Hunyuan3D-2 and TripoSG; TRELLIS.2 excluded on 24 GB VRAM and nvdiffrast 3.3, both quoted |
| 18 | Project Nature grass missing | accepted | Added to the grass and wildflower rows ($0 / $0, Standard, UE 4.19-5.4) |
| 19 | Missing free local routes | accepted | Own photo capture with RealityScan or Meshroom, DeepBump and Materialize, and "claim Megaplants now" (decision 4, which also notes it does not lift the editor clause) |
| 20 | Unquoted licence claims (RealityScan, Meshroom, clover, SDXL) | accepted | Quotes and links added; Kopimi provenance and the OpenRAIL "No use of the output can contravene" sentence included |
| 21 | Pilot gate has gaps | accepted | The gate is now an ordered list that covers every score region |
| 22 | Beech pack version unverified; Wan RAM caveat missing | partly accepted | The version is on the listing page: "Unreal Engine versions 5.1 – 5.6" (MEASURED); 5.8 loading stays UNVERIFIED. Wan RAM caveat added with the on-disk file sizes |
