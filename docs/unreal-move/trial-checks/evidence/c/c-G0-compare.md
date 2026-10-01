# C7 ground vs Manor Lords ground (G0, third pass)

## Reference set

Official Manor Lords screenshots: the Steam store page (app 1363080; the same 10 images are in the Hooded Horse press kit and on GOG),
the Hooded Horse press kit (Google Drive folder linked from gamespress.com) and the developer's official Steam announcements. URLs and sources are in refs.json;
the files are third-party pixels, kept only under the git-ignored ChimeraTerrain/Out/refs/manor_lords/. Every image is normalised to 1920 px wide before measuring.
Box overlays for every image (ours too) are in Out/refs/manor_lords/ov/; the labelled crop sheet is refs_sheet.jpg.

| image | camera | season | material target? | crops |
|---|---|---|---|---|
| ml_01.jpg | rts | summer | yes | meadow_mid, meadow_near_a, meadow_near_b, road_a, road_b, ploughed_field |
| ml_04.jpg | near_rts | summer | yes | grass_patch, grass_right, yard_dirt, street |
| ml_08.jpg | rts | summer | yes | meadow_b, dry_strip, ploughed_patch |
| press/press_03.jpg | rts | summer | yes | meadow_a, meadow_b, meadow_c, meadow_wide, fallow_field, ploughed_field |
| news/news_016.jpg | near_rts | summer | yes | meadow_a, meadow_b, meadow_c, dirt_patch, outcrop_lit |
| news/news_017.jpg | near_rts | summer | yes | meadow_a, meadow_wide, grey_path, tilled_strip |
| news/news_055.png | near_rts | summer | yes | meadow_a |
| news/news_080.png | rts | summer | yes | meadow_a, field_path |
| news/news_095.jpg | rts | summer | yes | meadow_a, meadow_b, meadow_path |
| news/news_053.jpg | near_rts | summer | yes | boulder_a, boulder_b |
| ml_02.jpg | near_rts | winter | no | snow_field |
| ml_06.jpg | rts | winter | no | snow_patchy |
| press/press_12.png | near_rts | winter | no | snow_field |
| ml_07.jpg | ground | summer | no | meadow_tall |
| ml_09.jpg | ground | summer | no | meadow_battle |
| ml_10.jpg | ground | autumn | no | dry_meadow |
| ml_03.jpg | near_rts | summer | no | elevated oblique battle view (near-RTS) but soldiers fill the ground; the old 'meadow_far' crop was a row of soldiers |
| ml_05.jpg | ground | autumn | no | ground-level cabbage garden with fences and autumn shrubs: no clean ground at strategy distance |

13 images show open ground at RTS or near-RTS distance and carry crops (10 summer images are the material targets; 3 winter images give the snow row only). That is one over the 8-12 asked for; the extra winter image is kept for the snow row.
Ground-level shots (3D grass fills the frame) form a separate 'scatter' row, compared only with our ground-level closeup. ml_03 and ml_05 carry no crops (reasons in the table).
Every target crop was re-boxed in this pass on clean ground: no shrubs, bushes, trees, tree shadows, figures or fences (checked on zoomed overlays). Manor Lords ground at these distances still carries 3D grass blades; those belong to its look and stay in.

Our shots (C7, `Out/s1_mat`, the C7 composite sources): rts80 (80 m class, the player view) and oblique (25 m up, 13 deg down: near-RTS) are compared with the Manor Lords targets.
The closeup is 3.5 m above the path, a ground-level view, so it is compared only with the ground-level scatter row. Our rts80 path is the S1 paint footprint (footprints.json), cored to its middle half, not the darkest pixels.

## Method

- Colour: HSV per pixel. Hue = circular mean weighted by saturation x value, pixels with S < 0.08 dropped. Crops with mean S < 0.18 (pale roads, rock, snow) are left out of a group hue: their hue is noise.
- Patch scale: octave band-energy ladder. Each crop is plane-detrended (lighting and view falloff), Hann-windowed and Fourier transformed; power is summed per octave of wavelength
  (2-4px = 0.10-0.21 %W, 4-8px = 0.21-0.42 %W, 8-16px = 0.42-0.83 %W, 16-32px = 0.83-1.67 %W, 32-64px = 1.67-3.33 %W, 64-128px = 3.33-6.67 %W, 128-256px = 6.67-13.33 %W). A band is used only when it fits the crop's short side. 'amp' = absolute std in that band (luma 0-1, or CIELAB a*b* for colour patches);
  'share' over the four bands 2-32 px that every crop of short side >= 32 px holds; the peak band is the dominant patch scale (blob diameter about half its wavelength).
- The second pass's r50 autocorrelation radius is dropped: on 128 x 64 tiles blurred by 1 px it read about 0.2 %W for plain pixel noise and could not see blobs above about 1.7 %W, so its 'patch scale matches' was the metric's floor.
  The ladder was validated on synthetic noise fields of known blob size before use (expected peak wavelength about 2 pi sigma):

| synthetic field | crop | expected peak | measured peak |
|---|---|---|---|
| white noise | 256x256 | 2.0 px | 2-4px |
| noise sigma 1 px | 256x256 | 6.3 px | 4-8px |
| noise sigma 2 px | 256x256 | 12.6 px | 8-16px |
| noise sigma 4 px | 256x256 | 25.1 px | 16-32px |
| noise sigma 8 px | 256x256 | 50.3 px | 32-64px |
| noise sigma 16 px | 256x256 | 100.5 px | 64-128px |
| white noise | 128x64 | 2.0 px | 2-4px |
| noise sigma 1 px | 128x64 | 6.3 px | 4-8px |
| noise sigma 2 px | 128x64 | 12.6 px | 8-16px |
| noise sigma 4 px | 128x64 | 25.1 px | 16-32px |
| noise sigma 8 px | 128x64 | 50.3 px | 32-64px |
| noise sigma 16 px | 128x64 | 100.5 px | 32-64px |

  Peaks are correct and monotonic wherever the scale fits the crop; on the 128 x 64 strip the 100 px field pins to the largest band it holds (censored, as expected).
- Caveats: several reference images were 2560-4096 px and are downsampled to 1920, which sharpens their 2-4 px band slightly; Steam JPEG blocks add a little 8 px energy; ground-only Manor Lords crops are small (trees, houses and fields break the ground up), so bands above 64 px rest on 1-3 crops.

## Colour

| group | n | hue (min-max) | hue spread | sat | sat spread | V (min-max) | V spread | luma std |
|---|---|---|---|---|---|---|---|---|
| ML meadow, summer RTS + near-RTS (target) | 17 | 62 (45-72) | 7.5 | 0.55 | 0.066 | 0.40 (0.25-0.51) | 0.062 | 0.058 |
| ML meadow, RTS only | 10 | 59 (45-72) | 8.0 | 0.59 | 0.063 | 0.40 (0.25-0.51) | 0.058 | 0.052 |
| ML meadow, near-RTS only | 7 | 67 (66-70) | 6.8 | 0.51 | 0.071 | 0.39 (0.25-0.46) | 0.069 | 0.065 |
| ML meadow wide crops (large scales only) | 2 | 62 (58-66) | 8.2 | 0.53 | 0.059 | 0.41 (0.36-0.45) | 0.084 | 0.079 |
| ML dry / fallow grass | 2 | 44 (41-47) | 3.6 | 0.41 | 0.057 | 0.47 (0.39-0.55) | 0.085 | 0.074 |
| ML village road (pale, trodden) | 3 | 33 (33) | 3.4 | 0.17 | 0.071 | 0.66 (0.50-0.77) | 0.089 | 0.089 |
| ML countryside dirt path | 3 | 36 (34-40) | 6.9 | 0.36 | 0.070 | 0.42 (0.33-0.47) | 0.068 | 0.059 |
| ML bare dirt yard | 2 | 36 (32-39) | 7.2 | 0.30 | 0.051 | 0.47 (0.42-0.52) | 0.055 | 0.048 |
| ML field soil (ploughed, tilled) | 4 | 30 (27-33) | 6.3 | 0.26 | 0.054 | 0.34 (0.23-0.55) | 0.054 | 0.046 |
| ML rock (lit faces) | 3 | grey | - | 0.10 | 0.053 | 0.39 (0.21-0.50) | 0.094 | 0.094 |
| ML snow (winter shots) | 3 | grey | - | 0.09 | 0.078 | 0.64 (0.47-0.81) | 0.195 | 0.191 |
| ML ground-level 3D grass (scatter; vs our closeup) | 3 | 52 (45-60) | 13.1 | 0.47 | 0.136 | 0.23 (0.21-0.27) | 0.088 | 0.080 |
| C7 rts80 meadow | 2 | 34 (34) | 1.6 | 0.59 | 0.010 | 0.47 (0.47) | 0.046 | 0.036 |
| C7 oblique meadow | 2 | 34 (34) | 1.7 | 0.58 | 0.017 | 0.53 (0.53-0.54) | 0.054 | 0.044 |
| C7 closeup meadow (ground-level) | 3 | 34 (34-35) | 1.8 | 0.57 | 0.015 | 0.52 (0.49-0.58) | 0.051 | 0.041 |
| C7 rts80 painted path (paint footprint) | 1 | 32 (32) | 2.7 | 0.51 | 0.050 | 0.43 (0.43) | 0.090 | 0.070 |
| C7 closeup painted path (ground-level) | 1 | 30 (30) | 1.1 | 0.46 | 0.022 | 0.42 (0.42) | 0.071 | 0.059 |
| C7 rts80 rock cone (snow-painted top) | 1 | 31 (31) | 1.8 | 0.32 | 0.062 | 0.70 (0.70) | 0.076 | 0.085 |
| C7 oblique rock (cone + plateau cliff) | 2 | 30 (30-31) | 1.9 | 0.32 | 0.057 | 0.68 (0.63-0.73) | 0.082 | 0.089 |

## Patch scale: luma amplitude per octave (absolute std, luma 0-1)

| group | crops | 2-4px (0.10-0.21 %W) | 4-8px (0.21-0.42 %W) | 8-16px (0.42-0.83 %W) | 16-32px (0.83-1.67 %W) | 32-64px (1.67-3.33 %W) | 64-128px (3.33-6.67 %W) | 128-256px (6.67-13.33 %W) | peak (2-32 px) | share 2-4/4-8/8-16/16-32 |
|---|---|---|---|---|---|---|---|---|---|---|
| ML meadow (target) | 16 | 0.0220 | 0.0257 | 0.0202 | 0.0161 | 0.0139 (n=11) | 0.0052 (n=3) | - | 4-8px | 0.27 / 0.36 / 0.22 / 0.15 |
| ML meadow, RTS only | 10 | 0.0185 | 0.0244 | 0.0200 | 0.0160 | 0.0149 (n=7) | 0.0052 (n=3) | - | 4-8px | 0.23 / 0.37 / 0.24 / 0.15 |
| ML meadow, near-RTS only | 6 | 0.0277 | 0.0279 | 0.0206 | 0.0162 | 0.0122 (n=4) | - | - | 2-4px | 0.34 / 0.33 / 0.20 / 0.14 |
| ML meadow wide crops | 2 | 0.0316 | 0.0303 | 0.0220 | 0.0166 | 0.0118 | 0.0120 (n=1) | - | 2-4px | 0.36 / 0.34 / 0.19 / 0.11 |
| C7 rts80 meadow | 2 | 0.0063 | 0.0076 | 0.0080 | 0.0062 | 0.0059 | 0.0087 | 0.0113 | 8-16px | 0.20 / 0.29 / 0.32 / 0.19 |
| C7 oblique meadow | 2 | 0.0078 | 0.0090 | 0.0087 | 0.0079 | 0.0075 | 0.0094 | 0.0126 | 4-8px | 0.22 / 0.29 / 0.27 / 0.22 |
| ML scatter (ground-level) | 3 | 0.0336 | 0.0324 | 0.0248 | 0.0191 | 0.0168 | 0.0168 | 0.0133 (n=2) | 2-4px | 0.36 / 0.32 / 0.20 / 0.12 |
| C7 closeup meadow (ground-level) | 3 | 0.0100 | 0.0114 | 0.0101 | 0.0088 | 0.0082 | 0.0095 (n=1) | 0.0058 (n=1) | 4-8px | 0.25 / 0.31 / 0.25 / 0.19 |
| ML dry / fallow | 2 | 0.0243 | 0.0310 | 0.0312 | 0.0543 (n=1) | 0.0327 (n=1) | - | - | 16-32px | 0.09 / 0.21 / 0.31 / 0.40 |
| ML village road | 3 | 0.0166 | 0.0304 | 0.0263 | 0.0261 (n=1) | - | - | - | 4-8px | 0.16 / 0.44 / 0.25 / 0.14 |
| ML countryside path | 2 | 0.0225 | 0.0227 | 0.0211 | 0.0085 (n=1) | - | - | - | 4-8px | 0.25 / 0.42 / 0.19 / 0.14 |
| ML rock (lit faces) | 3 | 0.0426 | 0.0431 | 0.0377 | 0.0358 | 0.0343 (n=2) | - | - | 4-8px | 0.28 / 0.29 / 0.22 / 0.20 |
| C7 oblique rock | 2 | 0.0165 | 0.0315 | 0.0497 | 0.0526 | 0.0473 (n=1) | - | - | 16-32px | 0.04 / 0.16 / 0.37 / 0.43 |
| C7 rts80 rock cone | 1 | 0.0118 | 0.0232 | 0.0369 | 0.0378 | 0.0398 | - | - | 16-32px | 0.04 / 0.16 / 0.39 / 0.41 |

## Patch scale: colour amplitude per octave (CIELAB a*b* std)

| group | crops | 2-4px (0.10-0.21 %W) | 4-8px (0.21-0.42 %W) | 8-16px (0.42-0.83 %W) | 16-32px (0.83-1.67 %W) | 32-64px (1.67-3.33 %W) | 64-128px (3.33-6.67 %W) | 128-256px (6.67-13.33 %W) | peak (2-32 px) | share 2-4/4-8/8-16/16-32 |
|---|---|---|---|---|---|---|---|---|---|---|
| ML meadow (target) | 16 | 1.54 | 2.37 | 2.29 | 2.26 | 2.01 (n=11) | 1.29 (n=3) | - | 4-8px | 0.14 / 0.29 / 0.28 / 0.29 |
| ML meadow, RTS only | 10 | 1.88 | 2.64 | 2.53 | 2.42 | 2.21 (n=7) | 1.29 (n=3) | - | 4-8px | 0.17 / 0.29 / 0.28 / 0.26 |
| ML meadow, near-RTS only | 6 | 0.97 | 1.92 | 1.89 | 1.99 | 1.65 (n=4) | - | - | 16-32px | 0.08 / 0.29 / 0.30 / 0.34 |
| ML meadow wide crops | 2 | 1.89 | 2.30 | 1.99 | 1.98 | 1.93 | 1.80 (n=1) | - | 4-8px | 0.20 / 0.30 / 0.23 / 0.27 |
| C7 rts80 meadow | 2 | 0.62 | 0.76 | 0.81 | 0.65 | 0.58 | 0.57 | 0.68 | 8-16px | 0.19 / 0.28 / 0.32 / 0.21 |
| C7 oblique meadow | 2 | 0.70 | 0.83 | 0.85 | 0.75 | 0.67 | 0.59 | 0.69 | 8-16px | 0.20 / 0.28 / 0.29 / 0.23 |
| ML scatter (ground-level) | 3 | 2.94 | 3.53 | 2.78 | 2.13 | 1.83 | 1.75 | 1.83 (n=2) | 4-8px | 0.28 / 0.37 / 0.22 / 0.13 |
| C7 closeup meadow (ground-level) | 3 | 0.77 | 0.90 | 0.85 | 0.82 | 0.72 | 0.89 (n=1) | 0.58 (n=1) | 4-8px | 0.22 / 0.29 / 0.25 / 0.23 |
| ML dry / fallow | 2 | 1.93 | 2.27 | 2.13 | 3.82 (n=1) | 2.25 (n=1) | - | - | 16-32px | 0.16 / 0.27 / 0.28 / 0.29 |
| ML village road | 3 | 0.73 | 1.20 | 1.18 | 1.26 (n=1) | - | - | - | 4-8px | 0.14 / 0.40 / 0.30 / 0.16 |
| ML countryside path | 2 | 0.88 | 1.17 | 1.29 | 0.60 (n=1) | - | - | - | 4-8px | 0.35 / 0.38 / 0.15 / 0.11 |
| ML rock (lit faces) | 3 | 0.65 | 1.11 | 1.38 | 1.35 | 1.47 (n=2) | - | - | 8-16px | 0.10 / 0.25 / 0.34 / 0.32 |
| C7 oblique rock | 2 | 0.71 | 1.05 | 1.48 | 1.30 | 0.83 (n=1) | - | - | 8-16px | 0.09 / 0.20 / 0.40 / 0.31 |
| C7 rts80 rock cone | 1 | 0.69 | 1.03 | 1.66 | 1.35 | 1.00 | - | - | 8-16px | 0.08 / 0.17 / 0.45 / 0.30 |

Manor Lords / C7 rts80 luma amplitude by band: 2-4px 3.5x, 4-8px 3.4x, 8-16px 2.5x, 16-32px 2.6x, 32-64px 2.4x, 64-128px 0.6x.
Manor Lords / C7 oblique: 2-4px 2.8x, 4-8px 2.9x, 8-16px 2.3x, 16-32px 2.0x, 32-64px 1.9x, 64-128px 0.6x.
Manor Lords / C7 rts80 colour amplitude: 2-4px 2.5x, 4-8px 3.1x, 8-16px 2.8x, 16-32px 3.5x, 32-64px 3.4x, 64-128px 2.3x.

## The gap in plain words

- **Hue (the main gap).** Manor Lords meadows read yellow-green: 62 deg on average (59 in the RTS shots, 67 near-RTS; crops range 45-72). Ours is 34 deg at rts80 and oblique, orange-straw: about 28-29 deg too warm. That is why it reads as a dry steppe.
- **Where that hue comes from.** About a third is C7's light, two thirds the texture. grass_ground's albedo is 43 deg; C7's sun, sky and tonemapper move it to 34 on screen (linear gains R 1.20, G 0.85, B 0.64 at rts80) and raise its saturation 1.35x. The same light raises dirt saturation 1.76x and rock saturation 1.78x. Under white-balanced light, grass_ground would match Manor Lords' DRY/fallow grass (d 0.9), not its meadow: the texture we have is a dry-grass texture.
- **Hue and saturation spread.** Inside one crop Manor Lords varies 8 deg in hue and 0.066 in saturation (live green, yellow, dead brown blades); ours varies 1.6-1.7 deg and 0.010-0.017: about 5x less hue spread and 4-7x less saturation spread. Ours is one colour.
- **Saturation mean** is close today (0.55 against 0.58-0.59), but only because a warm orange is as saturated as Manor Lords' green. Any greener texture under C7's light comes out MORE saturated than the target (Grass004 about 0.65), so a per-layer saturation scalar (about x0.85) or a more neutral light is needed with it.
- **Value.** Ours is brighter: V 0.47-0.53 against 0.40 (0.40 in the RTS shots), 18-34 % brighter. Manor Lords' crops span 0.25-0.51 with its lighting; its sunlit meadows reach about 0.45-0.48, so part of the gap is lighting.
- **Contrast (luma).** On clean crops (no shrubs or shadows) Manor Lords' meadow has 2.4-3.5x the luma contrast of our rts80 meadow and 1.9-2.9x that of our oblique, in every band from 2 to 64 px (0.1-3.3 %W), most at the finest grain (2-8 px). Ours is too smooth at the grain and clump scales.
- **Patch scale.** Manor Lords' ground is grain-dominated: its luma energy peaks in the 4-8px band (0.21-0.42 %W) and falls with scale to 0.0052 at 64-128 px (on the 3 crops that hold that scale; the one wide crop that does reads 0.0120). Inside 2-32 px the SHAPE of ours is similar (peak 8-16px at rts80, 4-8px oblique), only 2-3x weaker; but its LARGEST amplitude is at the largest scale: 0.0087 at 64-128 px and 0.0113 at 128-256 px (3.3-13 %W, the 72 m macro noise), above any of its finer bands. So ours is weak in grain and relatively blotchy in brightness; Manor Lords is the opposite. The second pass's 'patch scale matches' and 'keep or raise the macro' are withdrawn.
- **Colour patches.** Manor Lords' colour (a*b*) amplitude is 2.5-3.5x ours from 2 to 64 px and spread almost evenly over 4-32 px (shares 0.14 / 0.29 / 0.28 / 0.29); at 64-128 px it is 1.29 against our 0.57 (n=3, indicative). Its large-scale variation is colour (green against yellow patches), not brightness; ours is brightness.
- **Paths: two looks.** Manor Lords has pale, grey trodden village roads (S 0.17, V 0.66; ml_01:road_a, ml_01:road_b, ml_04:street) and darker countryside dirt paths through meadow (hue 36, S 0.36, V 0.42; news_017:grey_path, news_080:field_path, news_095:meadow_path). Our painted path at rts80 (paint footprint) is hue 32, S 0.51, V 0.43: the countryside-path brightness and hue, but 1.4x too saturated. As a village road it is 3.0x too saturated and too dark (V 0.43 vs 0.66).
- **Field soil** (ploughed or tilled) is hue 30, S 0.26, V 0.34; C7 has no field layer.
- **Dry / fallow grass** is hue 44, S 0.41, V 0.47 with strong contrast (luma std 0.074).
- **Rock.** Manor Lords' lit rock faces are lichen-grey: S 0.10, V 0.39. Our rock (oblique, no snow) is S 0.32, V 0.68: 3.2x too saturated and 1.7x too bright. (The second pass's rock target, V 0.30, was pulled down by crevice shadow; re-boxed on lit faces it is V 0.39.)

## What textures and the material can reach, and what needs scatter

Texture, material and lighting changes can close the hue gap, the hue/saturation spread, the brightness, the grain contrast and the path and rock colours.
They cannot produce what 3D grass and shrubs give Manor Lords: blade silhouettes against the light, the soft shadow each clump casts, and bushes. The ground-level scatter row (V 0.23, hue spread 13 deg, luma 2-4 px 0.0336) shows that part. Grass scatter is out of scope in this pass (plan C §3.5 and §7), so expect a ground that is the right colour and grain at rts80 but flatter than Manor Lords close up.

## What this implies for the next pass (costed against the 3.0 ms bar)

C7 measured terrain_gpu_ms 2.96 ms against the 3.0 ms bar (grey material 2.66 ms): 0.04 ms of headroom. Each slot (Grass, Dirt, Rock, Snow) already samples C, N and ARH at two UV scales (6 fetches) plus 2 macro-noise fetches.
Predictions below use per-channel linear gains calibrated on C7's own shots (candidates.json: calibration, method); they hold only under C7's light, and the range across the three models and two views is given. G1 re-measures every round.

1. **Swap, no extra fetches, each with per-layer scalars (ALU only):**
   - Grass: grass_ground -> ambientCG **Grass004** (best meadow fit under both lights). Under C7 light it lands at about hue 56 deg (models 56-63), S 0.65, V 0.42 against 62 / 0.55 / 0.40: it needs about +7 deg hue and saturation x0.85. Under white-balanced light it would land at 72 deg (too green by 10), so the right answer sits between: partly neutralise the light, or keep it and add the scalars. Grass001 is the darker alternative (hue 71, V 0.33).
   - Dirt (countryside path): keep **brown_mud_02** or take **brown_mud_03**; the texture's hue and value already fit (d 2.6 / 2.7); it needs saturation x0.68. For pale village roads instead: **ground_grey** (needs saturation x0.41 under C7 light; d 1.1 under neutral light). The second pass's Ground030 is withdrawn as first pick: on screen it lands at V about 0.89, and its moss blobs repeat. Pathway002 is a path strip with faded edges (decal use only).
   - Rock: rocks_ground_05 -> ambientCG **Rock030** (dark grey natural face): predicted S 0.29, V 0.42 against S 0.10, V 0.39: value fits, it needs saturation x0.34 (under neutral light d 0.3, nearly exact). mossy_rock (the second pass's pick) lands at S 0.34, V 0.57: it would barely move the rock gap.
   ambientCG needs a zip path in fetch_textures.py and its NormalDX/Roughness/AO/Displacement packed into the existing C/N/ARH files.
2. **Material and lighting, about free:** drive grass hue and saturation from the macro noise already sampled (about +-8 deg / +-0.07, the in-crop spread, plus green-against-yellow patches at 3-7 %W); lower MacroStrength's brightness swing rather than raise it (our 64-128 px luma, 0.0087, is at or above Manor Lords' 0.0052-0.0120 on the few crops that hold that scale) and move that variation into colour; add grain contrast at 0.1-3 %W (darken by ARH height and AO, about 2-3x); bring grass V down about 15 %. Lighting (sun colour, sky tint, exposure; Lumen stays off) is the lever that moves every layer's hue and saturation at once: C7's warm, saturating light is about a third of the hue gap.
3. **Needs room under 3.0 ms:** a second, noise-blended texture inside the Grass slot adds 6 fetches wherever it shows. Best sub-layers: grass_ground itself as the dry patches (it IS Manor Lords' dry-grass colour under neutral light), or Ground024 / forest_ground_04 (closest to the dry target), or Ground037 (greener grass-with-wear). Ground075 is unsuitable (paver grid with a maker's mark). Variation layers stay noise-driven inside the existing four splat slots (plan C §3.2 and §7, DW-1019).

## Changes from the second G0 pass

- Patch scale: r50 replaced by a validated octave band ladder for luma and colour. New finding: ours is grain-poor and blotchy at 3-13 %W; the 'scale matches' conclusion and 'keep or raise macro' advice are withdrawn.
- Candidate colour is now predicted on screen in hue, saturation AND value with per-channel gains calibrated on C7, with two more models for the uncertainty and a neutral-light what-if; every candidate's colour map was looked at (cand_montage.jpg).
- Picks changed: Grass004 stays (now with its scalars), Ground030 -> brown_mud_02/03 (country path) or ground_grey (village road), mossy_rock -> Rock030. Ground075 (paver grid), Rock064 (masonry), Ground054 and Ground087 (sand) are marked unsuitable.
- Crops: shrub, bush, shadow and figure crops re-boxed (ml_01 meadow_left -> meadow_mid, meadow_near_b, news_095 meadow_a, news_017 meadow_a and meadow_wide, news_053 boulder, news_016 outcrop, press_03 fallow and ploughed fields). news_017 path_wide was a tilled strip and is now soil; its grey path, news_080 and a news_095 meadow path form the countryside-path row; ml_01 and ml_04 form the village-road row.
- Ours: the closeup is ground-level and is compared only with the scatter row; the oblique (near-RTS) is added; the rts80 path comes from the paint footprint (V 0.43; the second pass's darkest-pixel strip read 0.37).
