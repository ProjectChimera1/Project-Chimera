| measure | result | bar |
|---|---|---|
| M1 meadow colour (594015 px) | on hue 63.2 S 0.554 V 0.482; off hue 63.0 S 0.561 V 0.478; delta 0.2 / -0.007 / 0.004 (ML 62 / 0.55 / 0.40) | +-3 deg, +-0.05: within |
| M2 meadow luma bands 2-4..64-128 px (5 tiles) | on 0.0212 / 0.0205 / 0.0160 / 0.0104 / 0.0058 / 0.0042; off 0.0209 / 0.0205 / 0.0161 / 0.0106 / 0.0061 / 0.0042; ML 0.0220 / 0.0257 / 0.0202 / 0.0161 / 0.0139 / 0.0052 | reported (on/ML at 8-64: [0.79, 0.65, 0.42]) |
| M3 dark-vegetation share (image) / canopy bounds | ours image 0.317, bounds 0.459; ML ml_01.jpg 0.233, ml_08.jpg 0.185, press_03.jpg 0.188 (mean 0.202) | within +-30 % of ML: False |
| M4 tree shadow on meadow | luma ratio 0.518, linear 0.264, hue 84.0, S 0.490 (158332 px) | 0.27-0.33 x lit, olive |
| M5 canopy massing | ours image masses 0.84 groves 0.09 singles 0.07; ours bounds masses 0.95 groves 0.04 singles 0.00; ml_01.jpg masses 0.56 groves 0.33 singles 0.12; ml_08.jpg masses 0.50 groves 0.31 singles 0.19; press_03.jpg masses 0.59 groves 0.26 singles 0.15 | reported |
| M6 path core vs verge contrast | on 0.177, off 0.174 | on >= off: True |
| M7 map edge hidden | 0.662 of 195920 off-shot sky px covered | reported |
| M8 fade ring | step 0.60/255 in 90-130 m rows, floor 2.95/255 | <= floor + 1: True |
| M9 shimmer | on/off 1.15 (std 0.00138 / 0.00120) | <= 1.5 |
| EXP pair exposure (sky px, median linear luma) | rts80_full on/off 1.068, auto/on 1.573 (66177 px); oblique on/off 1.021, auto/on 1.902 (501850 px); closeup on/off 1.05, auto/on 2.098 (220409 px) | on/off ~1.00 (one held exposure) |
| M10 grass on rock (oblique) | 0.061 of 22194 rock px under grass bounds | reported (upper bound) |
