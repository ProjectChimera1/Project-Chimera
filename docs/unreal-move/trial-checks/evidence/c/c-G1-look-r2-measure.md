# G1 round 2 - our ground measured like G0 and the art director (look_measure.py on Out/look_r2, closed path mask; AD round-1 methods lab.py / khaki.py / within.py and r1m.py)

| view | group | n | hue | hue spread | sat | sat spread | V | luma std | luma amp 2-4/4-8/8-16/16-32/32-64/64-128 px | target (G0) |
|---|---|---|---|---|---|---|---|---|---|---|
| rts80 | meadow | 2 | 63.5 | 5.5 | 0.589 | 0.077 | 0.391 | 0.0566 | 0.0236 / 0.0258 / 0.0180 / 0.0106 / 0.0064 / 0.0044 | hue 62 S 0.55 V 0.40 |
| rts80 | path | 1 | 35.0 | 5.2 | 0.421 | 0.029 | 0.382 | 0.0448 | - | hue 36 S 0.36 V 0.42; road: hue 33 S 0.17 V 0.66 |
| rts80 | rock | 1 | 25.6 | 6.1 | 0.139 | 0.037 | 0.385 | 0.0607 | - | hue None S 0.10 V 0.39 |
| oblique | meadow | 2 | 60.0 | 5.7 | 0.562 | 0.073 | 0.383 | 0.0598 | 0.0265 / 0.0282 / 0.0206 / 0.0122 / 0.0072 / 0.0043 | hue 62 S 0.55 V 0.40 |
| oblique | rock | 2 | 24.4 | 5.2 | 0.147 | 0.033 | 0.289 | 0.0475 | - | hue None S 0.10 V 0.39 |
| closeup | meadow | 3 | 70.7 | 6.3 | 0.434 | 0.070 | 0.444 | 0.0488 | 0.0419 / 0.0452 / 0.0340 / 0.0196 / 0.0120 / 0.0072 | hue 62 S 0.55 V 0.40 |
| closeup | path | 1 | 32.7 | 1.7 | 0.344 | 0.019 | 0.434 | 0.0440 | - | hue 36 S 0.36 V 0.42; road: hue 33 S 0.17 V 0.66 |
| closeup | rock | 1 | 21.1 | 4.3 | 0.125 | 0.019 | 0.346 | 0.0552 | 0.0130 / 0.0192 / 0.0212 / 0.0173 / 0.0151 / 0.0097 | hue None S 0.10 V 0.39 |
| ML target | meadow | 17 | 62 | 7.5 | 0.55 | 0.066 | 0.40 | 0.058 | 0.0220 / 0.0257 / 0.0202 / 0.0161 / 0.0139 / 0.0052 | compare_c7.md |

## Round-2 bars the art director asked for (Manor Lords pooled meadow crops vs ours; ours boxes = the AD's)

| measure | ML | round 1 | round 2 | AD aim |
|---|---|---|---|---|
| within-window hue range (200x60, 6 px blur), median deg | 9.7 | 11.9 | 4.0 | <= 10 |
| within-window L* range, median | 5.8 | 5.4 | 3.2 | - |
| khaki share (Lab h<100, C<30, 6 px blur) | 0.087 | 0.054 | 0.009 | <= 0.09, concentrated |
| lime share (h 100-112, C>34) | 0.249 | 0.121 | 0.260 | >= 0.20 |
| deep-green share (h>112, C<30) | 0.123 | 0.064 | 0.106 | - |
| Lab chroma SD in crop | 5.5 | 3.18 | 5.07 | toward 5.5 |
| Lab C p90 (raw) | 37.8 | 36.0 | 39.3 | - |
| Lab L mean / h mean / C mean | 40.4 / 106.7 / 31.2 | 48.5 / 107.3 / 32.7 | 42.3 / 107.4 / 32.8 | - |
| low-passed hue SD (sigma 16) | 3.09 | 5.10 | 2.35 | toward 3.1 |
| luma 64-128 px band (rts80 meadow) | 0.0052 | 0.0108 | 0.0044 | <= 0.006 |
| luma 8-16 / 16-32 / 32-64 px | 0.0202 / 0.0161 / 0.0139 | 0.0153 / 0.0087 / 0.0073 | 0.0180 / 0.0106 / 0.0064 | toward ML |
| rts80 V p05 / p95 | 0.23 / 0.55 | 0.33 / 0.53 | 0.29 / 0.49 | p05 toward 0.23 |
| oblique meadow L* | 45-47 (sunlit ML) | 53.8 | 43.0 | 45-47 |
| plateau cast shadow / lit | 0.29-0.31 olive | 0.305, hue 86 | 0.240, hue 88 olive | 0.29-0.31 |
| patch-repeat ACF peak (rts80 rows 75-115) | - | 0.13 | 0.198 at 120 px (the 5 m clump noise; 0.0 beyond) | < 0.15 |

```
ML {'within_hue_range_med': 9.7, 'within_L_range_med': 5.8, 'khaki': 0.087, 'lime': 0.249, 'deep': 0.123, 'C_p90': 37.764, 'lab_L': 40.44, 'lab_C': 31.19, 'lab_Csd': 5.5, 'lab_h': 106.7, 'lab_Lfine': 4.4, 'lab_hlp16': 3.09}
== D:/Projects/Chimera-Unreal/ChimeraTerrain/Out/look_r2
  r2 {'within_hue_range_med': 4.0, 'within_L_range_med': 3.2, 'khaki': 0.009, 'lime': 0.26, 'deep': 0.106, 'C_p90': 39.262, 'lab_L': 42.32, 'lab_C': 32.82, 'lab_Csd': 5.07, 'lab_h': 107.41, 'lab_Lfine': 3.79, 'lab_hlp16': 2.35, 'obl_meadow_L': 43.0}
  acf_75_115 [(120, 0.198), (487, 0.0), (468, -0.005)]
  acf_120_170 [(278, 0.067), (421, -0.076), (754, -0.121)]
  rts80_dist {'hue_p10_50_90': [56.3, 62.9, 71.5], 'S_p10_90': [0.49, 0.69], 'V_p05_95': [0.29, 0.49], 'share_40_50': 0.002, 'share_70_80': 0.133}
  oblique_dist {'hue_p10_50_90': [53.2, 60.0, 67.8], 'S_p10_90': [0.47, 0.65], 'V_p05_95': [0.28, 0.49], 'share_40_50': 0.023, 'share_70_80': 0.055}
  closeup_dist {'hue_p10_50_90': [60.9, 70.6, 81.5], 'S_p10_90': [0.35, 0.63], 'V_p05_95': [0.29, 0.53], 'share_40_50': 0.004, 'share_70_80': 0.395}
  plateau_shadow {'ratio': 0.24, 'shadow_srgb': [52, 63, 39], 'lit_srgb': [122, 122, 60], 'shadow_hue': 87.5, 'shadow_S': 0.38}
```
