# G1 round 3 - our ground measured like G0 and the art director (look_measure.py on Out/look_r3, closed path mask; AD methods lab.py / khaki.py / within.py and r1m.py)

| view | group | n | hue | hue spread | sat | sat spread | V | luma std | luma amp 2-4/4-8/8-16/16-32/32-64/64-128 px | target (G0) |
|---|---|---|---|---|---|---|---|---|---|---|
| rts80 | meadow | 2 | 67.8 | 11.0 | 0.569 | 0.072 | 0.374 | 0.0562 | 0.0222 / 0.0247 / 0.0184 / 0.0129 / 0.0094 / 0.0079 | hue 62 S 0.55 V 0.40 |
| rts80 | path | 1 | 37.7 | 10.4 | 0.270 | 0.043 | 0.417 | 0.0598 | - | hue 36 S 0.36 V 0.42; road: hue 33 S 0.17 V 0.66 |
| rts80 | rock | 1 | 23.7 | 6.1 | 0.106 | 0.034 | 0.415 | 0.0553 | - | hue None S 0.10 V 0.39 |
| oblique | meadow | 2 | 57.7 | 10.1 | 0.579 | 0.063 | 0.364 | 0.0604 | 0.0256 / 0.0279 / 0.0216 / 0.0150 / 0.0111 / 0.0068 | hue 62 S 0.55 V 0.40 |
| oblique | rock | 2 | 12.1 | 43.0 | 0.058 | 0.023 | 0.329 | 0.0406 | - | hue None S 0.10 V 0.39 |
| closeup | meadow | 3 | 78.1 | 8.5 | 0.508 | 0.077 | 0.420 | 0.0537 | 0.0402 / 0.0434 / 0.0328 / 0.0196 / 0.0144 / 0.0108 | hue 62 S 0.55 V 0.40 |
| closeup | path | 1 | 38.4 | 9.1 | 0.307 | 0.036 | 0.467 | 0.0645 | - | hue 36 S 0.36 V 0.42; road: hue 33 S 0.17 V 0.66 |
| closeup | rock | 1 | 26.3 | 2.7 | 0.178 | 0.019 | 0.362 | 0.0584 | 0.0182 / 0.0235 / 0.0224 / 0.0200 / 0.0213 / 0.0155 | hue None S 0.10 V 0.39 |
| ML target | meadow | 17 | 62 | 7.5 | 0.55 | 0.066 | 0.40 | 0.058 | 0.0220 / 0.0257 / 0.0202 / 0.0161 / 0.0139 / 0.0052 | compare_c7.md |

## Bars the art director set for round 3 (Manor Lords pooled meadow crops vs ours; ours boxes = the AD's)

| measure | ML | round 2 | round 3 | AD aim | met |
|---|---|---|---|---|---|
| within-window hue range (200x60, 6 px blur), median deg | 9.7 | 4.0 | 9.2 | 8-10 | yes |
| within-window L* range, median | 5.8 | 3.2 | 4.1 | >= 4.5 | no |
| khaki share (Lab h<100, C<30) | 0.087 | 0.009 | 0.105 | 0.06-0.10 | just over |
| lime share (h 100-112, C>34) | 0.249 | 0.260 | 0.050 | 0.2-0.3 | no (regressed) |
| deep-green share (h>112, C<30) | 0.123 | 0.106 | 0.359 | - | far over |
| low-passed hue SD (sigma 16) | 3.09 | 2.35 | 5.78 | about 3.1 | overshoot |
| luma 16-32 / 32-64 px (rts80 meadow) | 0.0161 / 0.0139 | 0.0106 / 0.0064 | 0.0129 / 0.0094 | >= 0.013 / >= 0.010 | just short |
| luma 64-128 px | 0.0052 | 0.0044 | 0.0079 | <= 0.006 | no |
| path S / V (rts80, closed mask) | 0.36 / 0.42 | 0.42 / 0.38 | 0.27 / 0.42 | 0.25-0.32 / 0.42-0.50 | yes |
| path / meadow V ratio (rts80) | - | 0.98 | 1.11 | 1.0-1.15 | yes |
| rock S (rts80) | 0.10 | 0.14 | 0.106 | 0.10-0.12 | yes |
| plateau cast shadow / lit | 0.29-0.31 | 0.240 hue 88 olive | 0.307 hue 152 (cool green) | 0.29-0.31 | yes |
| patch-repeat ACF peak, rts80 rows 75-115 | - | 0.198 at 120 px | 0.092 at 463 px | < 0.15 | yes |
| ACF peak, rows 120-170 (holds the mound and plateau) | - | 0.067 | 0.288 at 137 px | - | no visible repeat in a native crop; the rows carry the sculpted shapes |
| gate G1 hill_sun_over_shadow | - | 2.60 | 1.89 | >= 1.3 | yes |

```
== D:/Projects/Chimera-Unreal/ChimeraTerrain/Out/look_r3
  r2 {'within_hue_range_med': 9.2, 'within_L_range_med': 4.1, 'khaki': 0.105, 'lime': 0.05, 'deep': 0.359, 'C_p90': 34.923, 'lab_L': 40.01, 'lab_C': 30.37, 'lab_Csd': 4.53, 'lab_h': 110.31, 'lab_Lfine': 3.7, 'lab_hlp16': 5.78, 'obl_meadow_L': 40.0}
  acf_75_115 [(463, 0.092), (569, 0.031), (695, -0.1)]
  acf_120_170 [(137, 0.288), (276, 0.047), (768, -0.058)]
  rts80_dist {'hue_p10_50_90': [52.7, 66.2, 88.5], 'S_p10_90': [0.47, 0.67], 'V_p05_95': [0.28, 0.48], 'share_40_50': 0.049, 'share_70_80': 0.152}
  oblique_dist {'hue_p10_50_90': [47.5, 55.5, 72.0], 'S_p10_90': [0.5, 0.65], 'V_p05_95': [0.26, 0.48], 'share_40_50': 0.232, 'share_70_80': 0.08}
  closeup_dist {'hue_p10_50_90': [63.6, 77.1, 91.3], 'S_p10_90': [0.4, 0.74], 'V_p05_95': [0.27, 0.52], 'share_40_50': 0.003, 'share_70_80': 0.296}
  plateau_shadow {'ratio': 0.307, 'shadow_srgb': [56, 71, 64], 'lit_srgb': [119, 123, 82], 'shadow_hue': 152.0, 'shadow_S': 0.21}
```
