| measure | result | bar |
|---|---|---|
| M1 meadow colour (683751 px) | on hue 63.2 S 0.528 V 0.540; off hue 63.3 S 0.555 V 0.476; delta -0.1 / -0.027 / 0.064 (ML 62 / 0.55 / 0.40) | +-3 deg, +-0.05: OUTSIDE |
| M2 meadow luma bands 2-4..64-128 px (7 tiles) | on 0.0253 / 0.0237 / 0.0189 / 0.0119 / 0.0067 / 0.0042; off 0.0170 / 0.0182 / 0.0146 / 0.0092 / 0.0053 / 0.0037; ML 0.0220 / 0.0257 / 0.0202 / 0.0161 / 0.0139 / 0.0052 | reported (on/ML at 8-64: [0.94, 0.74, 0.48]) |
| M3 dark-vegetation share (image) / canopy bounds | ours image 0.235, bounds 0.409; ML ml_01.jpg 0.233, ml_08.jpg 0.185, press_03.jpg 0.188 (mean 0.202) | within +-30 % of ML: True |
| M4 tree shadow on meadow | luma ratio 0.618, linear 0.373, hue 78.6, S 0.515 (87078 px) | 0.27-0.33 x lit, olive |
| M5 canopy massing | ours image masses 0.76 groves 0.17 singles 0.07; ours bounds masses 0.95 groves 0.05 singles 0.01; ml_01.jpg masses 0.56 groves 0.33 singles 0.12; ml_08.jpg masses 0.50 groves 0.31 singles 0.19; press_03.jpg masses 0.59 groves 0.26 singles 0.15 | reported |
| M6 path core vs verge contrast | on 0.172, off 0.168 | on >= off: True |
| M7 map edge hidden | 0.336 of 195908 off-shot sky px covered | reported |
| M8 fade ring | step 0.69/255 in 90-130 m rows, floor 3.80/255 | <= floor + 1: True |
| M9 shimmer | on/off 2.72 (std 0.00329 / 0.00121) | <= 1.5 |
| M10 grass on rock (oblique) | 0.057 of 22558 rock px under grass bounds | reported (upper bound) |
