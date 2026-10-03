#!/usr/bin/env python3
"""make_video_frames.py - lists the MovieFrame PNGs of a run for make_video.sh (plan C 4 C9).

Usage: make_video_frames.py <run_dir> <staging_dir>
Reads <run_dir>/results.json movies[]: each entry names the folder the engine dumped into (the screenshot dir, searched exactly as the CSV is: the
director's own record, not a glob) and the exact files that movie op wrote (older MovieFrame files in that folder belong to earlier runs). The
files of the LAST movie are hard-linked (copied when linking fails) as <staging_dir>/f00001.png ... in frame order. Prints "frames=<n> src=<dir>".
Exit 0 on success, 1 when the run has no movie or a file is missing.
"""
import json
import re
import os
import shutil
import sys


def main(argv):
    if len(argv) != 3:
        print(__doc__)
        return 2
    run, stage = argv[1], argv[2]
    path = os.path.join(run, "results.json")
    if not os.path.isfile(path):
        print("no results.json in %s" % run)
        return 1
    with open(path, encoding="utf-8") as f:
        res = json.load(f)
    movies = res.get("movies") or []
    if not movies:
        print("results.json lists no movie")
        return 1
    m = movies[-1]
    src = m["dir"]
    # frame order = the engine's frame counter in the name (MovieFrame02085.png), as an integer: a text sort breaks past 99999
    files = sorted(m["files"], key=lambda n: (int(re.sub(r"\D", "", os.path.basename(n)) or 0), n))
    if not files or len(files) != m.get("frames_requested", len(files)):
        print("movie lists %d files for %s frames" % (len(files), m.get("frames_requested")))
        return 1
    os.makedirs(stage, exist_ok=True)
    for old in os.listdir(stage):
        if old.startswith("f") and old.endswith(".png"):
            os.remove(os.path.join(stage, old))
    for i, name in enumerate(files, 1):
        s = os.path.join(src, name)
        if not os.path.isfile(s) or os.path.getsize(s) == 0:
            print("missing movie frame %s" % s)
            return 1
        d = os.path.join(stage, "f%05d.png" % i)
        try:
            os.link(s, d)
        except OSError:
            shutil.copyfile(s, d)
    print("frames=%d src=%s" % (len(files), src))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
