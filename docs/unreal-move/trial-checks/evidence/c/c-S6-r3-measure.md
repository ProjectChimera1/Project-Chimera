| measure | result | bar |
|---|---|---|
| M1 meadow colour (399361 px) | on hue 63.2 S 0.554 V 0.488; off hue 63.2 S 0.564 V 0.485; delta 0.0 / -0.010 / 0.003 (ML 62 / 0.55 / 0.40) | +-3 deg, +-0.05: within |
| M2 meadow luma bands 2-4..64-128 px (4 tiles) | on 0.0209 / 0.0205 / 0.0191 / 0.0138 / 0.0083 / 0.0058; off 0.0182 / 0.0185 / 0.0149 / 0.0097 / 0.0058 / 0.0043; ML 0.0220 / 0.0257 / 0.0202 / 0.0161 / 0.0139 / 0.0052 | reported (on/ML at 8-64: [0.95, 0.86, 0.6]) |
| M3 dark-vegetation share (image) / canopy bounds | ours image 0.293, bounds 0.596; ML ml_01.jpg 0.233, ml_08.jpg 0.185, press_03.jpg 0.188 (mean 0.202) | within +-30 % of ML: False |
| M4 tree shadow on meadow | luma ratio 0.516, linear 0.263, hue 85.4, S 0.472 (149711 px) | 0.27-0.33 x lit, olive: luma OUTSIDE, linear OUTSIDE |
| M5 canopy massing | ours image masses 0.72 groves 0.18 singles 0.10; ours bounds masses 0.98 groves 0.02 singles 0.00; ml_01.jpg masses 0.56 groves 0.33 singles 0.12; ml_08.jpg masses 0.50 groves 0.31 singles 0.19; press_03.jpg masses 0.59 groves 0.26 singles 0.15 | reported |
| M6 path core vs verge contrast | on 0.159, off 0.163 | on >= off (no tolerance): False |
| M7 map edge hidden | 0.561 of 195925 off-shot sky px covered | reported |
| M8 fade ring | step 0.97/255 in 90-130 m rows; floor 0.08/255 from rows < 80 m clear of every cull end +-8 m (raw 1.38; ends in window: 32, 45, 70 m) | <= floor + 1: n/a: GrassT0 ends at 70 m, outside the 90-130 m rows |
| M8 at the grass end (70 m) | step 1.38/255 in 61.3-80.0 m rows; floor 1.02/255 from rows 15-55 m beyond the end clear of every cull end +-8 m (raw 1.02; ends in window: 100, 110 m) | <= floor + 1: True |
| M9 shimmer | on/off 1.12 (std 0.00135 / 0.00120) | <= 1.5 |
| EXP pair exposure (sky px, median linear luma) | rts80_full on/off 1.065, auto/on 1.634 (85937 px); oblique on/off 1.02, auto/on 1.925 (505690 px); closeup on/off 1.08, auto/on 2.486 (134498 px) | on/off ~1.00 (one held exposure) |
| M10 grass on rock (oblique) | 0.000 of 22369 rock px under grass bounds | reported (upper bound) |
