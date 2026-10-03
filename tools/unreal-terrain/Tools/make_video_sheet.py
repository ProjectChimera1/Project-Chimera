#!/usr/bin/env python3
"""make_video_sheet.py - the six-frame contact sheet of a VIDEO run's sculpt.mp4 (task C9 evidence c-C9-sculpt-sheet.jpg). Light work (no lock).

Usage: make_video_sheet.py <run_dir> <out.jpg> [--frames 5,70,150,230,300,325]
Decodes the listed 1-based frames of <run_dir>/sculpt.mp4 with ffmpeg (select by frame number, exact), scales each to 800x450 and tiles them
2 across x 3 down (1600x1350), each labelled with its frame number and time. JPEG quality 85. Prints "SHEET <out> <w>x<h> bytes=<n>".
"""
import argparse
import os
import subprocess
import sys
import tempfile

from PIL import Image, ImageDraw

FFMPEG = os.environ.get("FFMPEG", "C:/Users/MD_Ki/AppData/Local/Microsoft/WinGet/Links/ffmpeg.exe")
TILE_W, TILE_H, COLS = 800, 450, 2


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("run")
    ap.add_argument("out")
    ap.add_argument("--frames", default="5,70,150,230,300,325")
    ap.add_argument("--fps", type=float, default=30.0)
    a = ap.parse_args(argv)
    frames = [int(x) for x in a.frames.split(",")]
    mp4 = os.path.join(a.run, "sculpt.mp4")
    if not os.path.isfile(mp4):
        print("no sculpt.mp4 in %s" % a.run)
        return 1
    rows = (len(frames) + COLS - 1) // COLS
    sheet = Image.new("RGB", (TILE_W * COLS, TILE_H * rows), (0, 0, 0))
    with tempfile.TemporaryDirectory() as d:
        for i, fr in enumerate(frames):
            png = os.path.join(d, "f%05d.png" % fr)
            # select is 0-based (n); the sheet names frames 1-based
            subprocess.run([FFMPEG, "-y", "-loglevel", "error", "-i", mp4, "-vf", "select=eq(n\\,%d),scale=%d:%d:flags=lanczos" % (fr - 1, TILE_W, TILE_H),
                            "-frames:v", "1", png], check=True)
            im = Image.open(png).convert("RGB")
            dr = ImageDraw.Draw(im)
            label = "frame %d  t=%.2f s" % (fr, (fr - 1) / a.fps)
            dr.rectangle([0, 0, 8 + 7 * len(label), 20], fill=(0, 0, 0))
            dr.text((4, 4), label, fill=(255, 255, 255))
            sheet.paste(im, ((i % COLS) * TILE_W, (i // COLS) * TILE_H))
    sheet.save(a.out, "JPEG", quality=85)
    print("SHEET %s %dx%d bytes=%d" % (a.out, sheet.width, sheet.height, os.path.getsize(a.out)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
