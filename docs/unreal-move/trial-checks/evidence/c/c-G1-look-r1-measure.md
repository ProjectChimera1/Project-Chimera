# G1 round 1 - our ground measured like G0 (look_measure.py on Out/look_r1; closed path mask)

| view | group | n | hue | hue spread | sat | sat spread | V | luma std | luma amp 2-4/4-8/8-16/16-32/32-64/64-128 px | target (G0) |
|---|---|---|---|---|---|---|---|---|---|---|
| rts80 | meadow | 2 | 62.0 | 8.8 | 0.526 | 0.027 | 0.425 | 0.0542 | 0.0219 / 0.0230 / 0.0153 / 0.0087 / 0.0073 / 0.0108 | hue 62 S 0.55 V 0.40 |
| rts80 | path | 1 | 36.0 | 4.6 | 0.337 | 0.026 | 0.411 | 0.0437 | - | hue 36 S 0.36 V 0.42; road: hue 33 S 0.17 V 0.66 |
| rts80 | rock | 1 | 33.0 | 3.0 | 0.157 | 0.025 | 0.400 | 0.0493 | - | hue None S 0.10 V 0.39 |
| oblique | meadow | 2 | 61.9 | 8.5 | 0.500 | 0.041 | 0.494 | 0.0641 | 0.0267 / 0.0283 / 0.0199 / 0.0114 / 0.0086 / 0.0117 | hue 62 S 0.55 V 0.40 |
| oblique | rock | 2 | 34.5 | 3.0 | 0.139 | 0.016 | 0.407 | 0.0396 | - | hue None S 0.10 V 0.39 |
| closeup | meadow | 3 | 62.7 | 8.2 | 0.452 | 0.030 | 0.502 | 0.0519 | 0.0458 / 0.0487 / 0.0363 / 0.0206 / 0.0112 / 0.0053 | hue 62 S 0.55 V 0.40 |
| closeup | path | 1 | 33.9 | 1.7 | 0.311 | 0.017 | 0.479 | 0.0429 | - | hue 36 S 0.36 V 0.42; road: hue 33 S 0.17 V 0.66 |
| closeup | rock | 1 | 30.7 | 2.6 | 0.152 | 0.013 | 0.395 | 0.0438 | 0.0117 / 0.0167 / 0.0176 / 0.0138 / 0.0110 / 0.0060 | hue None S 0.10 V 0.39 |
| ML target | meadow | 17 | 62 | 7.5 | 0.55 | 0.066 | 0.40 | 0.058 | 0.0220 / 0.0257 / 0.0202 / 0.0161 / 0.0139 / 0.0052 | compare_c7.md |

## Pixel distributions, patch repeat and shadow (the art director's round-0 methods: period.py, huedist.py, shad.py boxes)

```
== D:/Projects/Chimera-Unreal/ChimeraTerrain/Out/look_r1
  acf_75_115 [(161, 0.13), (557, 0.112), (246, 0.031)]
  acf_120_170 [(449, 0.062), (326, 0.032), (189, 0.029)]
  rts80_dist {'hue_p10_50_90': [50.9, 62.8, 73.8], 'S_p10_90': [0.49, 0.56], 'V_p05_95': [0.33, 0.53], 'share_40_50': 0.078, 'share_70_80': 0.201}
  oblique_dist {'hue_p10_50_90': [51.0, 62.2, 73.0], 'S_p10_90': [0.45, 0.55], 'V_p05_95': [0.38, 0.61], 'share_40_50': 0.076, 'share_70_80': 0.177}
  closeup_dist {'hue_p10_50_90': [51.3, 61.0, 73.4], 'S_p10_90': [0.41, 0.57], 'V_p05_95': [0.35, 0.62], 'share_40_50': 0.067, 'share_70_80': 0.146}
  plateau_shadow {'ratio': 0.305, 'shadow_srgb': [62, 71, 50], 'lit_srgb': [121, 125, 70], 'shadow_hue': 85.7, 'shadow_S': 0.3}
```

Round 0 on the same methods: acf_75_115 peak 0.553 at 248 px (harmonics 0.232, 0.073); rts80 hue p10/50/90 53.0/61.0/68.9, share 40-50 0.022, 70-80 0.070, V p05/95 0.33/0.50; plateau shadow 0.109x lit, sRGB (33,42,37), hue 147.
Manor Lords pooled meadow (art director): hue p10/50/90 48/64/74, share 40-50 0.12, 70-80 0.23, V p05/95 0.23/0.55; tree shadows on meadow 0.29-0.31x lit, olive.
