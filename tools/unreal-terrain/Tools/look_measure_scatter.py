#!/usr/bin/env python3
"""look_measure_scatter.py - plan C scatter 3.9 look measures M1-M10 on one LOOKX run (task S6). Called by `look_measure.py --scatter RUN`.

Reads the LOOKX run folder: rts80_full.png / rts80_full_off.png, oblique.png / oblique_off.png, closeup.png / closeup_off.png (scatter on and
its scatter_visible 0 twin of the same build, settle 150 each), footprints.json (scatter_mask rows: rts80 mask_trees, mask_shrubs, mask_canopy,
mask_casters_body, mask_shadow; oblique mask_grass) and results.json (the two `movie 30` captures at rts80 in compare mode: scatter on, then
off). Colour and band energy use G0's own functions (Out/refs/manor_lords/scripts/measure3.py, spectral.py; imported, not copied).

Measures (reported, D4; nothing here is a gate):
  M1  meadow hue / S / V with scatter on against the same pixels with scatter off (the final ground), bar |dhue| <= 3 deg, |dS|, |dV| <= 0.05;
      meadow = pixels that are grass-coloured in the off shot (hue 35-110 deg, S >= 0.2) and outside every caster mask (bodies and shadows).
  M2  meadow luma band energy (2-4 ... 64-128 px) on 128 px tiles that are >= 97 % meadow, on and off, against G0's ML meadow ladder.
  M3  canopy fraction of ground pixels: ours bounds-based (mask_canopy / non-sky pixels), and image-based with one classifier applied the same
      way to ours and to the ML references (dark and green to blue-green: blurred luma <= 0.72 x the frame's median ground luma,
      hue 45-230 deg, S >= 0.08). Checked on overlays (--overlay): it finds the shaded parts of crowns and the tree shadows on the
      ground, not lit crowns or the hazy far forest, so it is a DARK-VEGETATION share (how much of the frame is shaded tree mass), not the
      true canopy cover; ours is also reported bounds-based. Bar: ours image-based within +-30 % of the ML mean.
  M4  tree shadow on meadow: pixels inside the casters' shadow hull (mask_shadow), outside every caster body, meadow in the off shot and
      darkened by >= 25 % with scatter on: mean on/off luma ratio (sRGB luma and linear luminance) and the on-shot hue. Bar 0.27-0.33, olive.
  M5  canopy massing: connected components of the image-based canopy mask (both sides) and of mask_canopy: share of canopy pixels in masses
      (>= 1 % of the frame), groves (0.1-1 %) and singles (< 0.1 %).
  M6  path core vs verge contrast at rts80: path = brown pixels of the off shot (hue 15-48 deg, S 0.18-0.65, V 0.15-0.6) in its largest
      components (>= 400 px); core = 2 px erosion, verge = the 3-10 px ring outside; contrast = 1 - core luma / verge luma, on and off.
      Bar: on >= off (no tolerance).
  M7  map edge hidden: sky pixels of the rts80 off shot (B > R, B >= G, V >= 0.45, S <= 0.55) that are not sky with scatter on.
  M8  fade ring: row profile of (on - off) luma over meadow pixels in 8-row bins; a bin counts only with a real meadow sample (>= 10 % of
      its pixels, M8_MIN_FRAC), so a few hundred pixels between trees and shadows at the frame edge never set a step. The largest
      adjacent-bin step inside the rows whose flat-ground slant distance is 90-130 m (camera at 61.3 m, pitch -50, vertical fov 75; the
      LOOKX hills make this approximate) against the largest step in the rows nearer than 80 m (the floor). Bar: step <= floor + 1/255.
      When GrassT0's cull end (results.json cull_end_m) is not 130 m, the same statistic is also taken AT that end: bin pairs whose distances
      both lie within +-8 m of the end (M8_END_HALF_M; S6 close: the old band from 0.69 x end took in the whole bottom third of the rts80
      frame, so its max step sat at the frame edge, not at the end), against the rows 15-55 m beyond it. Floors are taken only from bin
      pairs clear of every drawn unit's cull end (+-8 m around each end in results.json cull_end_m), so a floor never contains a ring
      itself; the raw floor and the ends inside each window are reported beside it, and every step names the bin pair that set it
      (rows, distances, meadow pixels). The plan's 90-130 m row is n/a (holds null) when GrassT0's end lies outside 90-130 m: it would
      test rows with no grass end in them. A window with no qualifying bin pair reports null.
  M9  shimmer: mean per-pixel temporal luma std over the 30 movie frames, scatter on / scatter off. Bar <= 1.5.
  M10 grass on rock at oblique: rock-coloured pixels of the off shot (S <= 0.16, 0.18 <= V <= 0.75) covered by the grass layer's projected
      bounds (mask_grass): the share. Bounds over-cover thin blades, so this is an upper bound.

Usage: python look_measure.py --scatter RUN [--json OUT] [--md OUT] [--overlay DIR]
"""
import json
import os
import sys

import numpy as np
from PIL import Image
from scipy.ndimage import binary_dilation, binary_erosion, gaussian_filter, label, uniform_filter

T = os.path.dirname(os.path.dirname(os.path.abspath(__file__))).replace('\\', '/')
REFS = T + '/Out/refs/manor_lords'
sys.path.insert(0, REFS + '/scripts')
import measure3  # noqa: E402
import matplotlib.colors as mc  # noqa: E402

# G0 compare_c7.md meadow targets (ML summer RTS + near-RTS).
ML_MEADOW = dict(hue=62, sat=0.55, val=0.40, luma_amp=[0.0220, 0.0257, 0.0202, 0.0161, 0.0139, 0.0052])
# RTS references for M3 / M5 and the row from which their ground starts (ml_08's top rows are mountains and sky).
ML_RTS = [('ml_01.jpg', 0), ('ml_08.jpg', 200), ('press/press_03.jpg', 0)]
BANDS = ['2-4', '4-8', '8-16', '16-32', '32-64', '64-128']
# M8: rows per bin, the share of a bin's pixels that must be meadow for it to count, the half-width of the window at a grass end, and the
# clearance a floor bin pair keeps from every cull end.
M8_BIN = 8
M8_MIN_FRAC = 0.10
M8_END_HALF_M = 8.0
M8_CLEAR_M = 8.0


def load(path):
    return measure3.load(path)


def hsv(img):
    return mc.rgb_to_hsv(np.clip(img / 255.0, 0, 1))


def luma(img):
    return measure3.luma(img)


def lin_lum(img):
    l = measure3.lin(img)
    return 0.2126 * l[..., 0] + 0.7152 * l[..., 1] + 0.0722 * l[..., 2]


def mask_from(fp, pose, name, shape):
    """A scatter_mask row (row runs [y, x0, x1] inclusive) as a boolean image; None if the run has no such mask."""
    e = fp.get(pose, {}).get(name)
    if not isinstance(e, dict):
        return None
    m = np.zeros(shape, bool)
    for y, x0, x1 in e.get('runs', []):
        m[int(y), int(x0):int(x1) + 1] = True
    return m


def sky_mask(img):
    h = hsv(img)
    r, g, b = img[..., 0], img[..., 1], img[..., 2]
    return (b > r) & (b >= g) & (h[..., 2] >= 0.45) & (h[..., 1] <= 0.55)


def grass_mask(img):
    h = hsv(img)
    hue = h[..., 0] * 360
    return (hue >= 35) & (hue <= 110) & (h[..., 1] >= 0.2) & (h[..., 2] >= 0.12)


def canopy_classifier(img, row0=0):
    """Image-based canopy (and tree-shadow) mask: dark, green, textured (see M3)."""
    h = hsv(img)
    L = luma(img)
    ground = np.zeros(L.shape, bool)
    ground[row0:, :] = True
    ground &= ~sky_mask(img)
    Lb = gaussian_filter(L, 3)
    med = float(np.median(L[ground])) if ground.any() else 0.5
    hue = h[..., 0] * 360
    m = ground & (Lb <= 0.72 * med) & (hue >= 45) & (hue <= 230) & (h[..., 1] >= 0.08)
    # Clean speckle: open then close a little.
    m = binary_dilation(binary_erosion(m, iterations=2), iterations=2)
    return m, ground


def massing(m):
    n_px = m.size
    lab, n = label(m)
    if n == 0:
        return dict(components=0, masses=0.0, groves=0.0, singles=0.0)
    sizes = np.bincount(lab.ravel())[1:]
    tot = float(sizes.sum())
    big = sizes >= 0.01 * n_px
    mid = (sizes >= 0.001 * n_px) & ~big
    return dict(components=int(n), masses=round(float(sizes[big].sum()) / tot, 3), groves=round(float(sizes[mid].sum()) / tot, 3),
                singles=round(float(sizes[~big & ~mid].sum()) / tot, 3))


def colour_of(img, m):
    if m.sum() < 50:
        return None
    return measure3.colour(img[m])


def band_ladder(img, meadow, tile=128):
    amps = []
    H, W = meadow.shape
    for y in range(0, H - tile + 1, tile):
        for x in range(0, W - tile + 1, tile):
            if meadow[y:y + tile, x:x + tile].mean() >= 0.97:
                p = measure3.patch(img[y:y + tile, x:x + tile])
                if p and len(p['luma']['amp']) >= 6:
                    amps.append(p['luma']['amp'][:6])
    if not amps:
        return None, 0
    return [round(float(v), 4) for v in np.mean(amps, 0)], len(amps)


def row_distance_m(H=1080, cam_h=61.28, pitch=50.0, vfov=75.0):
    f = (H / 2.0) / np.tan(np.radians(vfov / 2.0))
    y = np.arange(H)
    theta = np.radians(pitch) + np.arctan((y - H / 2.0) / f)
    with np.errstate(divide='ignore', invalid='ignore'):
        d = np.where(theta > 0.01, cam_h / np.sin(theta), np.inf)
    return d


def cull_ends_m(res):
    """{slot: metres} of the run's drawn cull ends (results.json scatter options `cull_end_m`), or None."""
    stack = [res]
    while stack:
        x = stack.pop()
        if isinstance(x, dict):
            c = x.get('cull_end_m')
            if isinstance(c, dict):
                return {k: float(v) for k, v in c.items()}
            stack.extend(x.values())
        elif isinstance(x, list):
            stack.extend(x)
    return None


def cull_end_m(res, slot):
    """The run's drawn cull end of `slot` in metres (results.json scatter options `cull_end_m`, written since S6 round 3), or None."""
    stack = [res]
    while stack:
        x = stack.pop()
        if isinstance(x, dict):
            c = x.get('cull_end_m')
            if isinstance(c, dict) and slot in c:
                return float(c[slot])
            stack.extend(x.values())
        elif isinstance(x, list):
            stack.extend(x)
    return None


def m8_profile(diff, meadow, d, bin_rows=M8_BIN, min_frac=M8_MIN_FRAC):
    """M8 row profile: [(y0, mean on-off luma over the bin's meadow pixels or None, median slant distance m, meadow px)], one per bin.
    A bin with fewer meadow pixels than min_frac of its area holds None (too small a sample to compare)."""
    H = diff.shape[0]
    prof = []
    for y0 in range(0, H, bin_rows):
        m = meadow[y0:y0 + bin_rows]
        px = int(m.sum())
        ok = px >= max(1, int(min_frac * m.size))
        prof.append((y0, float(diff[y0:y0 + bin_rows][m].mean()) if ok else None, float(np.median(d[y0:y0 + bin_rows])), px))
    return prof


def m8_max_step(prof, lo, hi, ends=(), clear=False, clear_m=M8_CLEAR_M, bin_rows=M8_BIN):
    """Largest |step| (x255) between adjacent qualifying bins whose distances both lie in [lo, hi]; with clear, pairs within clear_m of
    any end in `ends` are skipped. Returns (step or None when no pair qualifies, the pair that set it: rows, distances, meadow px)."""
    best, where = None, None
    for (ya, va, da, pa), (yb, vb, db, pb) in zip(prof, prof[1:]):
        if va is None or vb is None or not (lo <= da <= hi and lo <= db <= hi):
            continue
        if clear and any(min(da, db) - clear_m <= e <= max(da, db) + clear_m for e in ends):
            continue
        step = abs(vb - va) * 255.0
        if best is None or step > best:
            best, where = step, dict(rows=[ya, yb + bin_rows], d_m=[round(da, 1), round(db, 1)], meadow_px=[pa, pb])
    return best, where


def movie_std(files):
    stack = []
    for f in files:
        if not os.path.isfile(f):
            return None
        stack.append(luma(np.asarray(Image.open(f).convert('RGB')).astype(np.float64)))
    if len(stack) < 2:
        return None
    return float(np.std(np.stack(stack), axis=0).mean())


def measure(run, overlay_dir=None):
    run = run.replace('\\', '/').rstrip('/')
    out = dict(run=run, method=__doc__.strip(), measures={})
    M = out['measures']
    fp = json.load(open(run + '/footprints.json')) if os.path.isfile(run + '/footprints.json') else {}
    res = json.load(open(run + '/results.json')) if os.path.isfile(run + '/results.json') else {}
    on, off = load(run + '/rts80_full.png'), load(run + '/rts80_full_off.png')
    shape = on.shape[:2]
    masks = {n: mask_from(fp, 'rts80', n, shape) for n in ('mask_trees', 'mask_shrubs', 'mask_canopy', 'mask_casters_body', 'mask_shadow')}
    casters_all = np.zeros(shape, bool)
    for n in ('mask_trees', 'mask_shrubs', 'mask_casters_body', 'mask_shadow'):
        if masks[n] is not None:
            casters_all |= masks[n]
    sky_off = sky_mask(off)
    meadow = grass_mask(off) & ~casters_all & ~sky_off

    # M1
    c_on, c_off = colour_of(on, meadow), colour_of(off, meadow)
    if c_on and c_off:
        dh = (c_on['hue_w_deg'] - c_off['hue_w_deg'] + 180) % 360 - 180
        ds, dv = c_on['s_mean'] - c_off['s_mean'], c_on['v_mean'] - c_off['v_mean']
        M['M1'] = dict(px=int(meadow.sum()), on=dict(hue=c_on['hue_w_deg'], sat=c_on['s_mean'], val=c_on['v_mean']),
                       off=dict(hue=c_off['hue_w_deg'], sat=c_off['s_mean'], val=c_off['v_mean']),
                       delta=dict(hue=round(dh, 1), sat=round(ds, 3), val=round(dv, 3)), ml=dict(hue=ML_MEADOW['hue'], sat=ML_MEADOW['sat'],
                                                                                              val=ML_MEADOW['val']),
                       within=bool(abs(dh) <= 3 and abs(ds) <= 0.05 and abs(dv) <= 0.05))
    # M2
    l_on, n_on = band_ladder(on, meadow)
    l_off, n_off = band_ladder(off, meadow)
    M['M2'] = dict(bands_px=BANDS, on=l_on, off=l_off, tiles=n_on, ml=ML_MEADOW['luma_amp'],
                   on_vs_ml_8_64=None if not l_on else [round(l_on[i] / ML_MEADOW['luma_amp'][i], 2) for i in (2, 3, 4)])
    # M3 + M5
    cm_on, ground_on = canopy_classifier(on)
    ours_img = float(cm_on.sum()) / max(1, ground_on.sum())
    ml_rows = []
    for name, row0 in ML_RTS:
        p = REFS + '/' + name
        if not os.path.isfile(p):
            continue
        im = load(p)
        cm, gr = canopy_classifier(im, row0)
        ml_rows.append(dict(ref=name, canopy_frac=round(float(cm.sum()) / max(1, gr.sum()), 3), massing=massing(cm)))
        if overlay_dir:
            save_overlay(im, cm, os.path.join(overlay_dir, 'canopy_' + os.path.basename(name).replace('.jpg', '.jpg')))
    ml_mean = float(np.mean([r['canopy_frac'] for r in ml_rows])) if ml_rows else None
    bounds = None
    if masks['mask_canopy'] is not None:
        bounds = round(float((masks['mask_canopy'] & ~sky_off).sum()) / max(1, (~sky_off).sum()), 3)
    M['M3'] = dict(ours_bounds=bounds, ours_image=round(ours_img, 3), ml=ml_rows, ml_mean=None if ml_mean is None else round(ml_mean, 3),
                   ratio_image=None if not ml_mean else round(ours_img / ml_mean, 2),
                   within_30pct=None if not ml_mean else bool(abs(ours_img / ml_mean - 1) <= 0.30))
    M['M5'] = dict(ours_image=massing(cm_on), ours_bounds=None if masks['mask_canopy'] is None else massing(masks['mask_canopy']),
                   ml=[dict(ref=r['ref'], **r['massing']) for r in ml_rows])
    if overlay_dir:
        save_overlay(on, cm_on, os.path.join(overlay_dir, 'canopy_ours_rts80.jpg'))
    # M4
    if masks['mask_shadow'] is not None:
        body = np.zeros(shape, bool)
        for n in ('mask_canopy', 'mask_casters_body'):
            if masks[n] is not None:
                body |= masks[n]
        Lon, Loff = luma(on), luma(off)
        cand = masks['mask_shadow'] & ~body & grass_mask(off) & ~sky_off
        sh = cand & (Lon <= 0.75 * Loff)
        if sh.sum() >= 50:
            lon_lin, loff_lin = lin_lum(on), lin_lum(off)
            col = measure3.colour(on[sh])
            M['M4'] = dict(px=int(sh.sum()), hull_px=int(cand.sum()), luma_ratio=round(float(Lon[sh].mean() / Loff[sh].mean()), 3),
                           linear_ratio=round(float(lon_lin[sh].mean() / loff_lin[sh].mean()), 3), hue=col['hue_w_deg'], sat=col['s_mean'],
                           bar='0.27-0.33 x lit, olive')
            M['M4']['luma_in_band'] = bool(0.27 <= M['M4']['luma_ratio'] <= 0.33)
            M['M4']['linear_in_band'] = bool(0.27 <= M['M4']['linear_ratio'] <= 0.33)
        else:
            M['M4'] = dict(px=int(sh.sum()), note='fewer than 50 shadowed meadow pixels')
    # M6
    h_off = hsv(off)
    hue = h_off[..., 0] * 360
    brown = (hue >= 15) & (hue <= 48) & (h_off[..., 1] >= 0.18) & (h_off[..., 1] <= 0.65) & (h_off[..., 2] >= 0.15) & (h_off[..., 2] <= 0.6)
    brown &= ~casters_all
    lab, n = label(binary_dilation(binary_erosion(brown, iterations=1), iterations=1))
    if n:
        sizes = np.bincount(lab.ravel())
        sizes[0] = 0
        path = np.isin(lab, np.nonzero(sizes >= 400)[0])
        core = binary_erosion(path, iterations=2)
        verge = binary_dilation(path, iterations=10) & ~binary_dilation(path, iterations=3) & grass_mask(off) & ~casters_all
        if core.sum() > 100 and verge.sum() > 100:
            def contrast(img):
                L = luma(img)
                return round(1.0 - float(L[core].mean()) / float(L[verge].mean()), 3)
            con, coff = contrast(on), contrast(off)
            M['M6'] = dict(core_px=int(core.sum()), verge_px=int(verge.sum()), on=con, off=coff, holds=bool(con >= coff))
    # M7
    sky_on = sky_mask(on)
    beyond = sky_off.sum()
    M['M7'] = dict(sky_px_off=int(beyond), hidden_frac=None if beyond == 0 else round(float((sky_off & ~sky_on).sum()) / beyond, 3))
    # M8
    d = row_distance_m(shape[0])
    diff = luma(on) - luma(off)
    prof = m8_profile(diff, meadow, d)
    # Every drawn unit's cull end (an undrawn unit reports 0: skipped). A floor bin pair within 8 m of an end could hold that end's ring.
    ends = sorted({v for v in (cull_ends_m(res) or {}).values() if v > 1.0})

    def ends_in(lo, hi):
        return [e for e in ends if lo - M8_CLEAR_M <= e <= hi + M8_CLEAR_M]

    def r2(x):
        return None if x is None else round(x, 2)

    def holds(step, fl):
        return None if step is None or fl is None else bool(step <= fl + 1.0)
    e_t0 = cull_end_m(res, 'GrassT0')
    band, band_at = m8_max_step(prof, 90, 130)
    floor_raw, _ = m8_max_step(prof, 0, 80)
    floor, floor_at = m8_max_step(prof, 0, 80, ends, clear=True)
    plan_row = e_t0 is None or 90.0 <= e_t0 <= 130.0
    M['M8'] = dict(min_meadow_frac=M8_MIN_FRAC, step_90_130_255=r2(band), step_90_130_at=band_at, floor_lt80_255=r2(floor),
                   floor_lt80_at=floor_at, floor_lt80_raw_255=r2(floor_raw), ends_in_floor_m=ends_in(0, 80), cull_ends_m=ends,
                   holds=holds(band, floor) if plan_row else None,
                   note=None if plan_row else 'n/a: GrassT0 ends at %.0f m, outside the 90-130 m rows' % e_t0)
    # M8 at the run's own grass end (since S6 the shipped GrassT0 end is 70 m, outside the plan's 90-130 m rows). Band = bin pairs within
    # +-8 m of the end; floor = the rows 15-55 m beyond the end, where no grass draws (the same on-off steps without any grass ring).
    if e_t0 is not None and e_t0 > 1.0 and abs(e_t0 - 130.0) > 0.5:
        lo, hi = e_t0 - M8_END_HALF_M, e_t0 + M8_END_HALF_M
        band2, band2_at = m8_max_step(prof, lo, hi)
        floor2_raw, _ = m8_max_step(prof, e_t0 + 15.0, e_t0 + 55.0)
        floor2, floor2_at = m8_max_step(prof, e_t0 + 15.0, e_t0 + 55.0, ends, clear=True)
        vis = [p[2] for p in prof if p[1] is not None]
        M['M8'].update(grass_end_m=e_t0, band_m=[round(max(lo, min(vis)) if vis else lo, 1), round(min(hi, max(vis)) if vis else hi, 1)],
                       step_at_end_255=r2(band2), step_at_end_at=band2_at,
                       floor_beyond_255=r2(floor2), floor_beyond_at=floor2_at, floor_beyond_raw_255=r2(floor2_raw),
                       ends_in_floor_beyond_m=ends_in(e_t0 + 15.0, e_t0 + 55.0), holds_at_end=holds(band2, floor2))
    # M9
    mv = res.get('movies') or []
    if len(mv) >= 2:
        f_on = [mv[0]['dir'] + f for f in mv[0]['files']]
        f_off = [mv[1]['dir'] + f for f in mv[1]['files']]
        s_on, s_off = movie_std(f_on), movie_std(f_off)
        if s_on is not None and s_off:
            M['M9'] = dict(std_on=round(s_on, 5), std_off=round(s_off, 5), ratio=round(s_on / s_off, 2), holds=bool(s_on / s_off <= 1.5))
        else:
            M['M9'] = dict(note='movie frames missing')
    # EXP (S6 round 2): exposure of each on/off pair. LOOKX now shoots <name>_off and <name> at one held exposure and <name>_auto (on) under auto
    # exposure; the median linear-luma ratio over sky pixels present in both frames (scatter never draws there) says whether the pair really
    # shares an exposure (on/off ~1.00) and how much auto exposure lifts the scatter-on frame (auto/on).
    exp = {}
    for nm in ('rts80_full', 'oblique', 'closeup'):
        fo, ff, fa = run + '/%s.png' % nm, run + '/%s_off.png' % nm, run + '/%s_auto.png' % nm
        if not (os.path.isfile(fo) and os.path.isfile(ff)):
            continue
        a, b = load(fo), load(ff)
        sk = sky_mask(a) & sky_mask(b)
        row = dict(sky_px=int(sk.sum()))
        if sk.sum() >= 200:
            la, lb = lin_lum(a)[sk], lin_lum(b)[sk]
            row['on_over_off'] = round(float(np.median(la) / max(np.median(lb), 1e-6)), 3)
            if os.path.isfile(fa):
                c = load(fa)
                sk2 = sk & sky_mask(c)
                if sk2.sum() >= 200:
                    row['auto_over_on'] = round(float(np.median(lin_lum(c)[sk2]) / max(np.median(lin_lum(a)[sk2]), 1e-6)), 3)
        exp[nm] = row
    if exp:
        M['EXP'] = exp
    # M10
    if os.path.isfile(run + '/oblique_off.png'):
        ob_off = load(run + '/oblique_off.png')
        mg = mask_from(fp, 'oblique', 'mask_grass', ob_off.shape[:2])
        ho = hsv(ob_off)
        rock = (ho[..., 1] <= 0.16) & (ho[..., 2] >= 0.18) & (ho[..., 2] <= 0.75) & ~sky_mask(ob_off)
        if mg is not None and rock.sum() > 100:
            M['M10'] = dict(rock_px=int(rock.sum()), covered_frac=round(float((rock & mg).sum()) / rock.sum(), 3), note='bounds: an upper bound')
    return out


def save_overlay(img, m, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    o = img.copy()
    o[m] = o[m] * 0.4 + np.array([255, 0, 255]) * 0.6
    Image.fromarray(o.astype(np.uint8)).resize((960, int(960 * o.shape[0] / o.shape[1]))).save(path, quality=85)


def to_md(out):
    M = out['measures']
    L = ['| measure | result | bar |', '|---|---|---|']
    if 'M1' in M:
        m = M['M1']
        L.append('| M1 meadow colour (%d px) | on hue %.1f S %.3f V %.3f; off hue %.1f S %.3f V %.3f; delta %.1f / %.3f / %.3f (ML 62 / 0.55 / 0.40) | '
                 '+-3 deg, +-0.05: %s |' % (m['px'], m['on']['hue'], m['on']['sat'], m['on']['val'], m['off']['hue'], m['off']['sat'],
                                             m['off']['val'], m['delta']['hue'], m['delta']['sat'], m['delta']['val'], 'within' if m['within'] else 'OUTSIDE'))
    m = M.get('M2', {})
    if m.get('on'):
        L.append('| M2 meadow luma bands 2-4..64-128 px (%d tiles) | on %s; off %s; ML %s | reported (on/ML at 8-64: %s) |' % (
            m['tiles'], ' / '.join('%.4f' % x for x in m['on']), ' / '.join('%.4f' % x for x in (m['off'] or [])),
            ' / '.join('%.4f' % x for x in m['ml']), m['on_vs_ml_8_64']))
    m = M.get('M3', {})
    if m:
        L.append('| M3 dark-vegetation share (image) / canopy bounds | ours image %.3f, bounds %s; ML %s (mean %s) | within +-30 %% of ML: %s |' % (
            m['ours_image'], m['ours_bounds'], ', '.join('%s %.3f' % (r['ref'].split('/')[-1], r['canopy_frac']) for r in m['ml']), m['ml_mean'],
            m['within_30pct']))
    if 'M4' in M:
        m = M['M4']
        L.append('| M4 tree shadow on meadow | %s | 0.27-0.33 x lit, olive: %s |' % (
            ('luma ratio %.3f, linear %.3f, hue %s, S %.3f (%d px)' % (m['luma_ratio'], m['linear_ratio'], m['hue'], m['sat'], m['px']))
            if 'luma_ratio' in m else m.get('note'),
            ('luma %s, linear %s' % ('within' if m['luma_in_band'] else 'OUTSIDE', 'within' if m['linear_in_band'] else 'OUTSIDE'))
            if 'luma_ratio' in m else '-'))
    m = M.get('M5', {})
    if m:
        def f(x):
            return 'masses %.2f groves %.2f singles %.2f' % (x['masses'], x['groves'], x['singles']) if x else '-'
        L.append('| M5 canopy massing | ours image %s; ours bounds %s; %s | reported |' % (
            f(m['ours_image']), f(m['ours_bounds']), '; '.join('%s %s' % (r['ref'].split('/')[-1], f(r)) for r in m['ml'])))
    if 'M6' in M:
        m = M['M6']
        L.append('| M6 path core vs verge contrast | on %.3f, off %.3f | on >= off (no tolerance): %s |' % (m['on'], m['off'], m['holds']))
    if 'M7' in M:
        L.append('| M7 map edge hidden | %s of %d off-shot sky px covered | reported |' % (M['M7']['hidden_frac'], M['M7']['sky_px_off']))
    if 'M8' in M:
        m = M['M8']

        def ends(xs):
            return ', '.join('%.0f' % e for e in xs) or 'none'

        def st(x, at):
            if x is None:
                return 'none (no qualifying bin pair)'
            if not at:
                return '%.2f/255' % x
            return '%.2f/255 (rows %d-%d, %.1f-%.1f m, meadow px %d/%d)' % (x, at['rows'][0], at['rows'][1], at['d_m'][0], at['d_m'][1],
                                                                          at['meadow_px'][0], at['meadow_px'][1])
        L.append('| M8 fade ring | step %s in 90-130 m rows; floor %s from rows < 80 m clear of every cull end +-8 m (raw %s; ends in window: '
                 '%s m; bins >= %.0f %% meadow) | <= floor + 1: %s |' % (
                     st(m['step_90_130_255'], m.get('step_90_130_at')), st(m['floor_lt80_255'], m.get('floor_lt80_at')),
                     st(m.get('floor_lt80_raw_255'), None), ends(m.get('ends_in_floor_m', [])), 100 * m.get('min_meadow_frac', 0),
                     m['note'] if m.get('note') else m['holds']))
        if 'grass_end_m' in m:
            L.append('| M8 at the grass end (%.0f m) | step %s in the %.1f-%.1f m rows (end +-8 m); floor %s from rows 15-55 m beyond the end '
                     'clear of every cull end +-8 m (raw %s; ends in window: %s m) | <= floor + 1: %s |' % (
                         m['grass_end_m'], st(m['step_at_end_255'], m.get('step_at_end_at')), m['band_m'][0], m['band_m'][1],
                         st(m['floor_beyond_255'], m.get('floor_beyond_at')), st(m['floor_beyond_raw_255'], None),
                         ends(m['ends_in_floor_beyond_m']), m['holds_at_end']))
    if 'M9' in M:
        m = M['M9']
        L.append('| M9 shimmer | %s | <= 1.5 |' % (('on/off %.2f (std %.5f / %.5f)' % (m['ratio'], m['std_on'], m['std_off'])) if 'ratio' in m else m['note']))
    if 'EXP' in M:
        L.append('| EXP pair exposure (sky px, median linear luma) | %s | on/off ~1.00 (one held exposure) |' % '; '.join(
            '%s on/off %s, auto/on %s (%d px)' % (k, v.get('on_over_off', '-'), v.get('auto_over_on', '-'), v['sky_px']) for k, v in M['EXP'].items()))
    if 'M10' in M:
        m = M['M10']
        L.append('| M10 grass on rock (oblique) | %.3f of %d rock px under grass bounds | reported (upper bound) |' % (m['covered_frac'], m['rock_px']))
    return '\n'.join(L)


def main(argv):
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument('run')
    ap.add_argument('--json')
    ap.add_argument('--md')
    ap.add_argument('--overlay')
    a = ap.parse_args(argv)
    out = measure(a.run, a.overlay)
    md = to_md(out)
    print(md)
    if a.json:
        with open(a.json, 'w', encoding='utf-8') as f:
            json.dump(out, f, indent=1)
    if a.md:
        with open(a.md, 'w', encoding='utf-8') as f:
            f.write(md + '\n')
    print('LOOK_MEASURE_SCATTER OK measures=%d' % len(out['measures']))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
