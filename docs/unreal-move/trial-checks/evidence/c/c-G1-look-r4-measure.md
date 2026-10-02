# G1 round 4 look measures (ML / r2 / r3 / r4)

Measures: the art director's round-1..3 methods (scratchpad r2m.py on S1 shots rts80_full/oblique, same boxes) and Tools/look_measure.py (G0 HSV boxes). ML = Manor Lords (G0 refs). Our shots: T/Out/look_r2, look_r3, look_r4 (S1, gate configuration, no overrides).

| measure | ML | r2 | r3 | r4 | aim |
|---|---|---|---|---|---|
| lime share (Lab hue 100-112, C > 34) | 0.249 | 0.26 | 0.05 | 0.237 | 0.20-0.30 |
| khaki share (hue < 100, C < 30) | 0.087 | 0.009 | 0.105 | 0.065 | 0.06-0.10 |
| deep-green share (hue > 112, C < 30) | 0.123 | 0.106 | 0.359 | 0.054 | <= 0.12 |
| within-window hue range (deg, median) | 9.7 | 4.0 | 9.2 | 7.5 | 8-10 |
| within-window L* range (median) | 5.8 | 3.2 | 4.1 | 4.0 | >= 4.5 |
| Lab L* (meadow boxes) | 45-50 sunlit | 42.3 | 40.0 | 43.4 | about 45 |
| Lab chroma | 33-40 | 32.8 | 30.4 | 35.6 | 36-40 lush |
| low-passed hue SD (16 px) | 3.09 | 2.35 | 5.78 | 4.67 | about 3.1 |
| plateau shadow / lit ratio | 0.30 | 0.24 | 0.307 | 0.271 | 0.29-0.31 |
| rts80 meadow HSV (look_measure.py) | 62 / 0.55 / 0.40 | 63.5 / 0.59 / 0.39 | 67.8 / 0.57 / 0.37 | 59.0 / 0.65 / 0.415 | G0 target |
| rts80 path HSV | 36 / 0.36 / 0.42 | 35 / 0.42 / 0.38 | 37.7 / 0.27 / 0.42 | 37.1 / 0.34 / 0.42 | S 0.25-0.36, V 0.42-0.50 |
| rts80 rock S / V | 0.10 / 0.39 | - | 0.106 / - | 0.159 / 0.419 | S 0.10-0.12 |
| meadow-rows ACF peak (rows 75-115) | - | 0.198 @120 px | 0.092 | 0.331 @91 px | < 0.15 (measured, not seen) |
| closeup whorls | none | none | yes | none (seen) | none |
| terrain_gpu_ms (median of 3) | - | 2.946 (r2 day) | 3.026 | 2.907 | <= 3.0 (aim 2.95) |
| paint bar changed_frac | - | 0.475 | 0.312 | 0.399 | >= 0.30 |

Notes: the ACF rise is a measure, not a visible repeat in a native-resolution strip of rows 640-1040 (checked side by side with r3); its cause is not isolated (candidates, untested: the stronger grass saturation on the 10 m far tile, or the smaller clump warp, 4 -> 1 m). Rock S rose for a reason not isolated: it cannot be RockFlat, because the next line (CG_SV with SatRock = 0) reduces the rock albedo to luma; candidates are the warmer, stronger light and grade (5700 K, contrast 1.22), the moss/lichen terms, the RockR/G/B tint and the grass blend.
