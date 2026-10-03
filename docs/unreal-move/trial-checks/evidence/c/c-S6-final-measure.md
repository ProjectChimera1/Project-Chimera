# S6 final (fix pass): python Tools/look_measure.py --scatter Out/lookx_final (LOOKX at the shipped defaults, no scatter option beyond -ChimeraTerrainScatter=1). Reported, not gated (D4). M8 since the fix pass: bins need >= 10 % meadow and the at-end window is the grass end +-8 m; round 3's and the first S6-final at-end M8 rows (1.38 and 5.81/255) were frame-edge artefacts at about 63 m, not the end. Round-3 comparison: M3 image 0.302 (round 3 0.293), bounds 0.569 (0.596); M5 image masses/groves/singles 0.73/0.20/0.07 (0.72/0.18/0.10).

| measure | result | bar |
|---|---|---|
| M1 meadow colour (428647 px) | on hue 62.9 S 0.557 V 0.486; off hue 62.9 S 0.564 V 0.483; delta 0.0 / -0.007 / 0.003 (ML 62 / 0.55 / 0.40) | +-3 deg, +-0.05: within |
| M2 meadow luma bands 2-4..64-128 px (5 tiles) | on 0.0199 / 0.0209 / 0.0203 / 0.0141 / 0.0085 / 0.0057; off 0.0186 / 0.0193 / 0.0160 / 0.0103 / 0.0061 / 0.0047; ML 0.0220 / 0.0257 / 0.0202 / 0.0161 / 0.0139 / 0.0052 | reported (on/ML at 8-64: [1.0, 0.88, 0.61]) |
| M3 dark-vegetation share (image) / canopy bounds | ours image 0.302, bounds 0.569; ML ml_01.jpg 0.233, ml_08.jpg 0.185, press_03.jpg 0.188 (mean 0.202) | within +-30 % of ML: False |
| M4 tree shadow on meadow | luma ratio 0.511, linear 0.258, hue 84.8, S 0.480 (145713 px) | 0.27-0.33 x lit, olive: luma OUTSIDE, linear OUTSIDE |
| M5 canopy massing | ours image masses 0.73 groves 0.20 singles 0.07; ours bounds masses 0.96 groves 0.04 singles 0.01; ml_01.jpg masses 0.56 groves 0.33 singles 0.12; ml_08.jpg masses 0.50 groves 0.31 singles 0.19; press_03.jpg masses 0.59 groves 0.26 singles 0.15 | reported |
| M6 path core vs verge contrast | on 0.159, off 0.163 | on >= off (no tolerance): False |
| M7 map edge hidden | 0.42 of 195923 off-shot sky px covered | reported |
| M8 fade ring | step 0.83/255 (rows 368-384, 102.9-101.5 m, meadow px 3385/3059) in 90-130 m rows; floor 0.04/255 (rows 544-560, 79.3-78.6 m, meadow px 6253/6252) from rows < 80 m clear of every cull end +-8 m (raw 1.33/255; ends in window: 45, 70 m; bins >= 10 % meadow) | <= floor + 1: n/a: GrassT0 ends at 70 m, outside the 90-130 m rows |
| M8 at the grass end (70 m) | step 1.33/255 (rows 824-840, 64.4-64.2 m, meadow px 2102/1775) in the 64.0-78.0 m rows (end +-8 m); floor 0.95/255 (rows 472-488, 87.0-86.0 m, meadow px 4223/4513) from rows 15-55 m beyond the end clear of every cull end +-8 m (raw 0.95/255; ends in window: 100, 110 m) | <= floor + 1: True |
| M9 shimmer | on/off 1.08 (std 0.00130 / 0.00120) | <= 1.5 |
| EXP pair exposure (sky px, median linear luma) | rts80_full on/off 1.048, auto/on 1.583 (113709 px); oblique on/off 1.023, auto/on 1.841 (518392 px); closeup on/off 1.077, auto/on 2.475 (140657 px) | on/off ~1.00 (one held exposure) |
| M10 grass on rock (oblique) | 0.000 of 22489 rock px under grass bounds | reported (upper bound) |
