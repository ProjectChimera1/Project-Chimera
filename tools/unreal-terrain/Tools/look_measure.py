#!/usr/bin/env python3
"""look_measure.py - G1 look pass: measure our ground crops in an S1 run the way G0 measured Manor Lords (plan C 3.5, V1/V5).

Colour (HSV, hue = saturation x value weighted circular mean with S < 0.08 dropped) and the luma/colour octave ladder come from G0's
own scripts (T/Out/refs/manor_lords/scripts/measure3.py and spectral.py, imported, not copied), on G0's crop boxes for the rts80,
oblique and closeup shots (crops.py OURS). Those boxes fit the S1 terrain, whose heights and splat are fixed by S1 (hashes = s1_a).

The rts80 painted path is re-measured with a CLOSED mask (main-session G0 note): footprints.json rts80.paint is a sampled pixel list
with gaps, so it is rasterised, closed (binary_closing, 3 px), hole-filled, the snow stroke on the cone is cut with G0's fitted-line
rule, and the core is the part whose distance to the edge is >= 0.5 x that column's maximum (G0's rule, now on a solid mask).

Usage: python look_measure.py RUN_DIR [--shot rts80_full.png] [--json OUT] [--md OUT]   (RUN_DIR holds rts80_full.png, oblique.png,
closeup.png and footprints.json). Prints one table row per crop and per group mean; targets are G0's compare_c7.md table.
"""
import argparse
import json
import os
import sys

import numpy as np
from scipy.ndimage import binary_closing, binary_fill_holes, distance_transform_edt

T = os.path.dirname(os.path.dirname(os.path.abspath(__file__))).replace('\\', '/')
G0 = T + '/Out/refs/manor_lords/scripts'
sys.path.insert(0, G0)
import measure3  # noqa: E402  (G0's colour() and patch())
from crops import OURS  # noqa: E402

# G0 compare_c7.md, "Colour" and "Patch scale" tables (Manor Lords groups; summer RTS + near-RTS targets).
TARGETS = {
    'meadow': dict(hue=62, sat=0.55, val=0.40, hue_spread=7.5, sat_spread=0.066, luma_std=0.058,
                   luma_amp=[0.0220, 0.0257, 0.0202, 0.0161, 0.0139, 0.0052], chroma_amp=[1.54, 2.37, 2.29, 2.26, 2.01, 1.29]),
    'dry': dict(hue=44, sat=0.41, val=0.47),
    'path': dict(hue=36, sat=0.36, val=0.42, note='countryside dirt path'),
    'road': dict(hue=33, sat=0.17, val=0.66, note='pale village road'),
    'rock': dict(hue=None, sat=0.10, val=0.39),
    'snow': dict(hue=None, sat=0.09, val=0.64),
}


def closed_path_mask(fp_json, shape, x_range=(900, 1600), max_above_line=60, core=0.5):
    """Painted path at rts80 from the paint footprint, closed before the core is taken (fixes G0's measure3.footprint_mask)."""
    fp = np.array(json.load(open(fp_json))['rts80']['paint'])
    m = np.zeros(shape, bool)
    m[fp[:, 1], fp[:, 0]] = True
    m = binary_fill_holes(binary_closing(m, structure=np.ones((3, 3), bool), iterations=3))
    x0, x1 = x_range
    m[:, :x0] = False
    m[:, x1:] = False
    ys, xs = np.where(m)
    k, b = np.polyfit(xs, ys, 1)
    for _ in range(3):  # refit near the path's line (drops the snow stroke on the cone), G0's rule
        keep = ys > k * xs + b - max_above_line
        k, b = np.polyfit(xs[keep], ys[keep], 1)
    yy, xx = np.indices(shape)
    m &= yy > k * xx + b - max_above_line
    d = distance_transform_edt(m)
    colmax = d.max(0)
    c = m & (d >= core * colmax[None, :]) & (colmax[None, :] > 0)
    return c, dict(footprint_px=int(m.sum()), core_px=int(c.sum()), raw_points=int(len(fp)), closing='3x3 x3 + fill holes',
                   core_rule='distance to edge >= %.1f x column max' % core)


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument('run')
    ap.add_argument('--json')
    ap.add_argument('--md')
    a = ap.parse_args(argv)
    run = a.run.replace('\\', '/').rstrip('/')
    shots = {'c7_rts80': 'rts80_full.png', 'c7_oblique': 'oblique.png', 'c7_closeup': 'closeup.png'}
    rows = []
    for spec in OURS:
        img = measure3.load(run + '/' + shots[spec['name']])
        view = spec['name'].split('_', 1)[1]
        for group, label, box in spec['crops']:
            if isinstance(box, dict):  # the rts80 painted path: closed mask
                m, info = closed_path_mask(run + '/footprints.json', img.shape[:2])
                d = measure3.colour(img[m])
                d.update(mask=info)
            else:
                x0, y0, x1, y1 = box
                c = img[y0:y1, x0:x1]
                d = measure3.colour(c.reshape(-1, 3))
                p = measure3.patch(c)
                if p:
                    d['luma_amp'] = p['luma']['amp']
                    d['chroma_amp'] = p['chroma']['amp']
                d['box'] = box
            d.update(view=view, group=group, label=label)
            rows.append(d)
    groups = {}
    for r in rows:
        groups.setdefault((r['view'], r['group']), []).append(r)
    summary = []
    for (view, group), rs in groups.items():
        g = dict(view=view, group=group, n=len(rs),
                 hue=round(float(np.mean([r['hue_w_deg'] for r in rs if r['hue_w_deg'] is not None] or [np.nan])), 1),
                 hue_spread=round(float(np.mean([r['hue_w_spread_deg'] or 0 for r in rs])), 1),
                 sat=round(float(np.mean([r['s_mean'] for r in rs])), 3), sat_spread=round(float(np.mean([r['s_std'] for r in rs])), 3),
                 val=round(float(np.mean([r['v_mean'] for r in rs])), 3), luma_std=round(float(np.mean([r['luma_std'] for r in rs])), 4))
        amps = [r['luma_amp'] for r in rs if 'luma_amp' in r and len(r['luma_amp']) >= 6]
        if amps:
            g['luma_amp_2_128'] = [round(float(x), 4) for x in np.mean([x[:6] for x in amps], 0)]
        camps = [r['chroma_amp'] for r in rs if 'chroma_amp' in r and len(r['chroma_amp']) >= 6]
        if camps:
            g['chroma_amp_2_128'] = [round(float(x), 2) for x in np.mean([x[:6] for x in camps], 0)]
        summary.append(g)
    lines = ['| view | group | n | hue | hue spread | sat | sat spread | V | luma std | luma amp 2-4/4-8/8-16/16-32/32-64/64-128 px | target (G0) |',
             '|---|---|---|---|---|---|---|---|---|---|---|']
    for g in summary:
        t = TARGETS.get(g['group'], {})
        tt = 'hue %s S %.2f V %.2f' % (t.get('hue'), t['sat'], t['val']) if t else ''
        if g['group'] == 'path':
            tt += '; road: hue 33 S 0.17 V 0.66'
        amp = ' / '.join('%.4f' % x for x in g.get('luma_amp_2_128', [])) or '-'
        lines.append('| %s | %s | %d | %s | %s | %.3f | %.3f | %.3f | %.4f | %s | %s |' % (
            g['view'], g['group'], g['n'], g['hue'], g['hue_spread'], g['sat'], g['sat_spread'], g['val'], g['luma_std'], amp, tt))
    lines.append('| ML target | meadow | 17 | 62 | 7.5 | 0.55 | 0.066 | 0.40 | 0.058 | %s | compare_c7.md |' % ' / '.join(
        '%.4f' % x for x in TARGETS['meadow']['luma_amp']))
    md = '\n'.join(lines)
    print(md)
    out = dict(run=run, rows=rows, summary=summary, targets=TARGETS, method=__doc__.strip())
    if a.json:
        with open(a.json, 'w', encoding='utf-8') as f:
            json.dump(out, f, indent=1)
    if a.md:
        with open(a.md, 'w', encoding='utf-8') as f:
            f.write(md + '\n')
    return 0


if __name__ == '__main__':
    sys.exit(main())
