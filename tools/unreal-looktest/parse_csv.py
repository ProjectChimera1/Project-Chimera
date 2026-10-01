"""Summarise UE CSV profiler captures from the look-test frame-rate runs (PLAN section 10.3). stdlib only.

    python parse_csv.py out/csv/game_A_r1.csv out/csv/game_A_r2.csv ... [--window 2000 3650] [--json out.json]

File layout, from CsvProfiler.cpp (FCsvStreamWriter): line 1 is a header "EVENTS,<series>..."; each
data row is "<event text, usually empty>,<value>,..." with one row per captured frame (times in ms);
after the last frame the header row is repeated, followed by ONE line of "[Key],Value" metadata pairs
with [Commandline] last. Series are created the first time a stat is seen, so line 1 only lists the
series that existed ~128 frames in and later rows grow: the column names come from the repeated
(final) header, and a row shorter than it leaves its missing trailing series absent for that frame.
Compressed captures (.csv.gz) are read transparently.

Per run: median / mean fps, 1% low, median GPU/GT/RT (and RHI) ms, which of them bounds the frame,
max frame, peak VRAM from <tag>.smi.csv, and whether the game's LAST viewport resize was 1920x1080.
Runs tagged game_<Look>_r<N> are also averaged per look. A result is FLAGGED when reps differ by more
than 5%, the window holds fewer than 1500 frames, any frame in the window exceeds 100 ms, or the
capture has no final summary header (the game did not finish the capture).
Exit 1 if a file is unreadable or lacks a required column; flags alone do not change the exit code.
"""
import argparse
import csv
import gzip
import json
import math
import os
import re
import statistics
import sys

# Real captures end with a metadata line whose fields can exceed the csv module's 128 KB default (seen live).
csv.field_size_limit(2**31 - 1)

LT = 'D:/Projects/Chimera-Unreal/ProjectChimera/LookTest'
REQUIRED = ('FrameTime', 'GPUTime', 'GameThreadTime', 'RenderThreadTime')
OPTIONAL = ('RHIThreadTime',)
RESIZE_RE = re.compile(r'Scene viewport resized to (\d+)x(\d+), mode (\w+)')
TAG_RE = re.compile(r'^game_(?P<look>.+)_r(?P<rep>\d+)$')


def read_capture(path):
    """Return (names, cols, meta, complete). cols maps column name -> one float (or None = series absent) per frame."""
    opener = gzip.open if path.endswith('.gz') else open
    with opener(path, 'rt', encoding='utf-8', newline='') as f:
        reader = csv.reader(f, quoting=csv.QUOTE_NONE)  # UE never quotes fields
        try:
            first = next(reader)
        except StopIteration:
            raise ValueError(f'{path}: empty file')
        if not first or first[0] != 'EVENTS':
            raise ValueError(f'{path}: first column is {first[:1]!r}, expected EVENTS (not a UE CSV profiler file)')
        data = []
        final = None
        metadata_line = None
        for row in reader:
            if not row:
                continue
            if row[0] == 'EVENTS':  # repeated summary header: data is over, metadata follows
                final = row
                metadata_line = next(reader, None)
                break
            data.append(row)
    header = final or first
    names = header[1:]
    missing = [c for c in REQUIRED if c not in names]
    if missing:
        raise ValueError(f'{path}: missing required columns {missing}; have {names[:11]}...')
    cols = {name: [] for name in names}
    for i, row in enumerate(data):
        if len(row) > len(header):
            raise ValueError(f'{path}: row {i + 2} has {len(row)} fields, final header has {len(header)}')
        try:
            vals = [float(x) for x in row[1:]]
        except ValueError:
            raise ValueError(f'{path}: non-numeric value in data row {i + 2}: {row[:6]}')
        vals += [None] * (len(names) - len(vals))
        for n, v in zip(names, vals):
            cols[n].append(v)
    meta = {}
    if metadata_line:
        i = 0
        while i < len(metadata_line):
            tok = metadata_line[i]
            if tok.startswith('[') and tok.endswith(']') and i + 1 < len(metadata_line):
                if tok == '[Commandline]':  # last entry; its value may itself contain commas
                    meta[tok[1:-1]] = ','.join(metadata_line[i + 1:])
                    break
                meta[tok[1:-1]] = metadata_line[i + 1]
                i += 2
            else:
                i += 1
    if not data:
        raise ValueError(f'{path}: no data rows')
    if None in cols['FrameTime']:
        raise ValueError(f'{path}: FrameTime is absent in some rows')
    return names, cols, meta, final is not None and meta.get('HasHeaderRowAtEnd') == '1'


def percentile(sorted_vals, p):
    """Nearest-rank percentile of an already sorted list."""
    k = max(0, math.ceil(p / 100 * len(sorted_vals)) - 1)
    return sorted_vals[k]


def peak_vram_mib(smi_path):
    """Peak memory.used from an nvidia-smi --format=csv log, or None if the file is absent/unparseable."""
    if not os.path.isfile(smi_path):
        return None
    with open(smi_path, encoding='utf-8', errors='replace') as f:
        lines = [l.strip() for l in f if l.strip()]
    if len(lines) < 2:
        return None
    heads = [h.strip() for h in lines[0].split(',')]
    idx = next((i for i, h in enumerate(heads) if h.startswith('memory.used')), None)
    if idx is None:
        return None
    peak = None
    for l in lines[1:]:
        parts = l.split(',')
        if len(parts) > idx:
            m = re.search(r'\d+', parts[idx])
            if m:
                peak = max(peak or 0, int(m.group()))
    return peak


def summarise(path, window, logs_dir):
    """Parse one capture and return its summary dict."""
    names, cols, meta, complete = read_capture(path)
    lo, hi = window
    ft_all = cols['FrameTime']
    sl = slice(lo, hi)
    ft = ft_all[sl]
    if not ft:
        raise ValueError(f'{path}: window [{lo},{hi}) is empty; capture has {len(ft_all)} frames')
    tag = os.path.basename(path)
    tag = re.sub(r'\.csv(\.gz)?$', '', tag)
    thread_cols = [c for c in REQUIRED[1:] + OPTIONAL if c in cols]
    # None = the series did not exist yet in that frame; it is left out of the median
    vals_of = {c: [v for v in cols[c][sl] if v is not None] for c in thread_cols}
    empty = [c for c in thread_cols if not vals_of[c]]
    if empty:
        raise ValueError(f'{path}: no values for {empty} in window [{lo},{hi})')
    med = {c: statistics.median(vals_of[c]) for c in thread_cols}
    bound = max(('GPUTime', 'GameThreadTime', 'RenderThreadTime'), key=lambda c: med[c])
    s = sorted(ft)
    med_ft = statistics.median(ft)
    out = {
        'tag': tag, 'file': path.replace('\\', '/'),
        'frames_total': len(ft_all), 'window': [lo, hi], 'window_frames': len(ft),
        'median_fps': round(1000 / med_ft, 2),
        'mean_fps': round(len(ft) / sum(ft) * 1000, 2),
        'low1_fps': round(1000 / percentile(s, 99), 2),
        'median_ms': {c: round(med[c], 3) for c in thread_cols},
        'median_frame_ms': round(med_ft, 3),
        'bound': {'GPUTime': 'GPU', 'GameThreadTime': 'game thread', 'RenderThreadTime': 'render thread'}[bound],
        'max_frame_ms': round(max(ft), 2),
        'peak_vram_mib': peak_vram_mib(f'{logs_dir}/{tag}.smi.csv'),
        'commandline': meta.get('Commandline'),
    }
    log = f'{logs_dir}/{tag}.log'
    if os.path.isfile(log):
        with open(log, encoding='utf-8', errors='replace') as f:
            resizes = RESIZE_RE.findall(f.read())
        # the LAST resize is the size the capture ran at
        out['resolution_1920x1080_in_log'] = bool(resizes) and resizes[-1][:2] == ('1920', '1080')
        out['last_viewport_resize'] = 'x'.join(resizes[-1][:2]) + ' ' + resizes[-1][2] if resizes else None
    else:
        out['resolution_1920x1080_in_log'] = None  # no game log to check
    flags = []
    if len(ft) < 1500:
        flags.append(f'window has only {len(ft)} frames (<1500)')
    if out['max_frame_ms'] > 100:
        flags.append(f'max frame {out["max_frame_ms"]} ms (>100)')
    if out['resolution_1920x1080_in_log'] is False:
        flags.append(f'last "Scene viewport resized to" in the game log is {out["last_viewport_resize"]}, not 1920x1080')
    out['capture_complete'] = complete
    if not complete:
        flags.append('no final summary header / [HasHeaderRowAtEnd]: the capture did not finish (crash?)')
    out['flags'] = flags
    return out


def per_look(runs):
    """Average median_fps / mean_fps / low1_fps over reps of each game_<Look>_r<N> tag, with the rep-spread flag."""
    groups = {}
    for r in runs:
        m = TAG_RE.match(r['tag'])
        if m:
            groups.setdefault(m.group('look'), []).append(r)
    res = {}
    for look, rs in sorted(groups.items()):
        meds = [r['median_fps'] for r in rs]
        spread = (max(meds) - min(meds)) / (sum(meds) / len(meds)) * 100 if len(rs) > 1 else 0.0
        flags = [f for r in rs for f in r['flags']]
        if spread > 5:
            flags.append(f'reps differ by {spread:.1f}% (>5%)')
        res[look] = {
            'reps': len(rs),
            'median_fps': round(sum(meds) / len(meds), 2),
            'mean_fps': round(sum(r['mean_fps'] for r in rs) / len(rs), 2),
            'low1_fps': round(sum(r['low1_fps'] for r in rs) / len(rs), 2),
            'rep_spread_pct': round(spread, 2),
            'bound': sorted({r['bound'] for r in rs}),
            'flags': flags,
        }
    return res


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('files', nargs='+')
    ap.add_argument('--window', nargs=2, type=int, default=(2000, 3650), metavar=('FIRST', 'END'),
                    help='frame window [FIRST, END) after warm-up and before the screenshot (default 2000 3650)')
    ap.add_argument('--logs', default=LT + '/logs', help='folder holding <tag>.log and <tag>.smi.csv (default LookTest/logs)')
    ap.add_argument('--json', metavar='PATH', help='also write the full summary as JSON')
    a = ap.parse_args()

    runs = []
    for p in a.files:
        try:
            runs.append(summarise(p, tuple(a.window), a.logs))
        except (OSError, ValueError) as e:
            print(f'parse_csv: {e}', file=sys.stderr)
            return 1

    print(f'{"run":22} {"frames":>6} {"med fps":>8} {"mean fps":>8} {"1% low":>7} {"GPU ms":>7} {"GT ms":>6} {"RT ms":>6} {"bound":>13} {"max ms":>7} {"VRAM MiB":>8}')
    for r in runs:
        m = r['median_ms']
        print(f'{r["tag"]:22} {r["window_frames"]:6d} {r["median_fps"]:8.1f} {r["mean_fps"]:8.1f} {r["low1_fps"]:7.1f} '
              f'{m["GPUTime"]:7.2f} {m["GameThreadTime"]:6.2f} {m["RenderThreadTime"]:6.2f} {r["bound"]:>13} '
              f'{r["max_frame_ms"]:7.1f} {str(r["peak_vram_mib"]):>8}')
        for fl in r['flags']:
            print(f'    FLAG {r["tag"]}: {fl}')
    looks = per_look(runs)
    if looks:
        print()
        for look, d in looks.items():
            print(f'LOOK {look}: median {d["median_fps"]} fps (mean {d["mean_fps"]}, 1% low {d["low1_fps"]}) over {d["reps"]} rep(s), '
                  f'spread {d["rep_spread_pct"]}%, bound by {"/".join(d["bound"])}')
            for fl in d['flags']:
                print(f'    FLAG {look}: {fl}')
    if a.json:
        with open(a.json, 'w', encoding='utf-8') as f:
            json.dump({'window': list(a.window), 'runs': runs, 'looks': looks}, f, indent=1)
    return 0


if __name__ == '__main__':
    sys.exit(main())
