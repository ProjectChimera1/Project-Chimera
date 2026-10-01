# Look test, 2026-10-01 (UE 5.8.3)

The same battle scene rendered two ways in Unreal: **A** Manor-Lords-style (near-photoreal, Lumen) and **B**
Northgard-style (stylised: no GI, higher sun, softer shadows, more saturation), plus **A_noLumen** (A with
screen-space GI instead of Lumen). Units, buildings, trees, ground, layout and cameras are identical in all three
(one layout hash in `results.json`); only lighting and the colour grade differ. The renders are unretouched.

## Frame rate (RTX 3060, 1080p, uncooked `-game`, 2 runs each, 1,650-frame window)

| Look | Median fps | 1% low | GPU ms | VRAM |
|---|---|---|---|---|
| A, Lumen | 62.9 | 58.9 | 15.4 | 3.9 GB |
| A_noLumen | 76.2 | 66.6 | 12.7 | 3.1 GB |
| B | 107.9 | 101.7 | 9.0 | 2.9 GB |

Lumen costs 2.7 ms of GPU time per frame, but on this open, sunlit field it changes the picture by under 1/255 on
average (A vs A_noLumen).

## What it showed

- **Readability is a camera problem first.** At Chimera's real camera (Godot default: 80 m out, pitch 50°, 75°
  vertical FOV) a unit is about 5–10 px tall in either look. The close camera (30 m, 40° FOV) reads well.
- **A grounds units better.** Its longer, harder shadows and warmer contrast set units on the ground; B's flat light
  makes them read as stickers on the ground.
- **B is not Northgard.** A grade cannot repaint photo-style textures; a real stylised look needs painted assets.
- **The Tripo models hold up under physical lighting.** The one model that looks wrong is `covenant_transmuter`
  (alpha infantry, the white cloaked blocks), the last Hunyuan model; there is no Tripo version yet.

## Not shown

No Fab/Megascans ground, grass or terrain relief (the ground is the engine's sample moss texture plus Chimera's
dirt); trees are the engine's stylised PCG sample; no weapons, VFX or UI; not a 1,000-unit stress test. Full caveat
list and every recipe value: `results.json`. Tooling and plan: `tools/unreal-looktest/`.

Files: `composite_close.jpg` (close camera), `composite_gameplay.jpg` (game camera). Full-size PNGs and their
SHA-256 hashes are in `D:\Projects\Chimera-Unreal\ProjectChimera\LookTest\out\` and in `results.json`.
