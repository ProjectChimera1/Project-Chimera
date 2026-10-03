#!/usr/bin/env python3
"""evidence.py - trial evidence tooling (EXECUTION.md S4, 2.3).

  add       copy or register a file into EV/<check>/ and its manifest.json
  phone     JPG <= 1600 px wide and <= 1 MB
  sheet     captioned contact sheet (JPG)
  scorecard the trial scorecard image from a JSON spec
  sent      stamp a manifest row with the time it was sent to Alec
  check     every manifest entry exists with a matching sha256; every file git tracks or would add under EV
            (tracked + untracked, not ignored) obeys the 2.3 size/type policy, is no (b) image, and is a
            committed manifest entry (EV root: README.md, trial-scorecard.jpg, lock-usage.csv only); every file on
            disk under EV/<check>, git-ignored or not, is referenced by a manifest row (an unlisted (b) image fails,
            anything else unlisted is an EVIDENCE NOTE). Committed rows store `path` relative to EV and are read from
            EV/<check>/<file>; registered rows keep the absolute working-folder path.
Common: --ev DIR (default: R/docs/unreal-move/trial-checks/evidence). Exit 0 ok, 1 failed check, 2 usage/io.
"""
import argparse
import datetime
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys

from PIL import Image, ImageDraw, ImageFont

R = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
DEFAULT_EV = os.path.join(R, "docs", "unreal-move", "trial-checks", "evidence")
TEXT_EXT = {".md", ".json", ".csv", ".txt"}
IMG_EXT = {".png", ".jpg", ".jpeg", ".gif"}
MB = 1024 * 1024
LIMITS = {"text": 1 * MB, "jpg": int(1.5 * MB), "png": 300 * 1024}
# Committed evidence per check (EXECUTION 2.3). Check c carries plan C plus the scatter sub-plan (S1-S9), so it gets 12 MB
# (main session, 2026-10-03, EXECUTION 8 'S6 ruling'); a and b keep 8 MB.
CHECK_CAP = {"a": 8 * MB, "b": 8 * MB, "c": 12 * MB}
NAME_RE = re.compile(r"^[abc]-[A-Za-z0-9]+-[a-z0-9]+(-[a-z0-9]+)*(-r\d+)?(-phone)?\.[a-z0-9]+$")


class EvError(Exception):
    pass


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def pixel_size(path):
    if os.path.splitext(path)[1].lower() in IMG_EXT:
        try:
            with Image.open(path) as im:
                return [im.width, im.height]
        except Exception:
            return None
    return None


def policy_ok(check, path, size):
    """Git policy 2.3: may this file be committed to R?"""
    ext = os.path.splitext(path)[1].lower()
    if check == "b" and ext in IMG_EXT | {".mp4"}:
        return False  # every (b) image is third-party pixels
    if ext in TEXT_EXT:
        return size <= LIMITS["text"]
    if ext in (".jpg", ".jpeg"):
        return size <= LIMITS["jpg"]
    if ext == ".png":
        return size <= LIMITS["png"]
    return False


def git_head(cwd):
    try:
        return subprocess.check_output(["git", "-C", cwd, "rev-parse", "--short", "HEAD"],
                                       stderr=subprocess.DEVNULL, text=True).strip()
    except Exception:
        return ""


def load_manifest(ev, check):
    p = os.path.join(ev, check, "manifest.json")
    if os.path.exists(p):
        with open(p, "r", encoding="utf-8") as f:
            return json.load(f)
    return []


def save_manifest(ev, check, rows):
    d = os.path.join(ev, check)
    os.makedirs(d, exist_ok=True)
    with open(os.path.join(d, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(rows, f, indent=2)
        f.write("\n")


def add(ev, check, src, name=None, task="", register=False, build_id="", commit=None):
    """Copy (default) or register src into the check's manifest. Returns the manifest row."""
    if check not in ("a", "b", "c"):
        raise EvError("check must be a, b or c")
    if not os.path.isfile(src):
        raise EvError("no such file: %s" % src)
    name = name or os.path.basename(src)
    if not NAME_RE.match(name):
        raise EvError("name %r does not match <check>-<task>-<subject>[-<variant>][-r<rep>].<ext>" % name)
    if name[0] != check:
        raise EvError("name %r does not start with check %s-" % (name, check))
    size = os.path.getsize(src)
    if register:
        path = os.path.abspath(src)
        committed = False
    else:
        if not policy_ok(check, name, size):
            raise EvError("%s (%d bytes) cannot be committed under the git policy; use --register" % (name, size))
        dest = os.path.join(ev, check, name)
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        if os.path.abspath(dest) != os.path.abspath(src):
            shutil.copyfile(src, dest)
        path = dest
        committed = True
    row = {
        # committed rows: path relative to EV (portable across clones, no D:/ in a pushed manifest);
        # registered rows: absolute path of the working-folder file (2.3)
        "file": name, "path": (path if register else "%s/%s" % (check, name)).replace("\\", "/"),
        "sha256": sha256(path), "bytes": os.path.getsize(path),
        "w_h": pixel_size(path), "task": task, "source": os.path.abspath(src).replace("\\", "/"),
        "r_commit": commit if commit is not None else git_head(R), "unreal_build_id": build_id,
        "committed": committed, "sent_to_alec": None,
    }
    rows = [r for r in load_manifest(ev, check) if r["file"] != name]
    rows.append(row)
    save_manifest(ev, check, rows)
    return row


def mark_sent(ev, check, name):
    rows = load_manifest(ev, check)
    hit = [r for r in rows if r["file"] == name]
    if not hit:
        raise EvError("not in manifest: %s" % name)
    hit[0]["sent_to_alec"] = datetime.datetime.now().astimezone().isoformat(timespec="seconds")
    save_manifest(ev, check, rows)


def phone(src, dest=None, max_w=1600, max_bytes=1 * MB):
    """Write a phone JPG (<=1600 px wide, <=1 MB). Returns its path."""
    base = os.path.splitext(src)[0]
    dest = dest or (base[:-6] if base.endswith("-phone") else base) + "-phone.jpg"
    with Image.open(src) as im:
        im = im.convert("RGB")
        if im.width > max_w:
            im = im.resize((max_w, max(1, round(im.height * max_w / im.width))), Image.LANCZOS)
        q = 90
        while True:
            im.save(dest, "JPEG", quality=q, optimize=True)
            if os.path.getsize(dest) <= max_bytes or q <= 30:
                break
            q -= 8
        while os.path.getsize(dest) > max_bytes:  # still too big at the quality floor: shrink
            im = im.resize((int(im.width * 0.85), int(im.height * 0.85)), Image.LANCZOS)
            im.save(dest, "JPEG", quality=q, optimize=True)
    return dest


def font(size, bold=False):
    for n in (("arialbd.ttf", "DejaVuSans-Bold.ttf") if bold else ("arial.ttf", "DejaVuSans.ttf")):
        try:
            return ImageFont.truetype(n, size)
        except OSError:
            pass
    return ImageFont.load_default(size)


def sheet(images, dest, captions=None, cols=3, thumb_w=480, title=None):
    """Captioned contact sheet; a caption defaults to the file name."""
    if not images:
        raise EvError("no images")
    captions = list(captions or [])
    captions += [os.path.basename(p) for p in images[len(captions):]]
    f = font(16)
    pad, cap_h = 12, 28
    th = []
    for p in images:
        with Image.open(p) as im:
            im = im.convert("RGB")
            h = max(1, round(im.height * thumb_w / im.width))
            th.append(im.resize((thumb_w, h), Image.LANCZOS))
    rows = [th[i:i + cols] for i in range(0, len(th), cols)]
    head = 40 if title else 0
    row_h = [max(t.height for t in r) + cap_h + pad for r in rows]
    W = cols * (thumb_w + pad) + pad
    H = head + sum(row_h) + pad
    out = Image.new("RGB", (W, H), (20, 22, 26))
    d = ImageDraw.Draw(out)
    if title:
        d.text((pad, 8), title, fill=(227, 200, 135), font=font(22, True))
    y = head + pad
    for ri, r in enumerate(rows):
        for ci, t in enumerate(r):
            x = pad + ci * (thumb_w + pad)
            out.paste(t, (x, y))
            d.text((x, y + t.height + 4), captions[ri * cols + ci][:int(thumb_w / 8.5)], fill=(220, 220, 220), font=f)
        y += row_h[ri]
    out.save(dest, "JPEG", quality=88, optimize=True)
    return dest


VERDICT_COL = {"PASS": (70, 190, 110), "FAIL": (230, 80, 80), "PARTIAL": (235, 170, 60), "INCOMPLETE": (235, 170, 60)}


def overall_verdict(checks, verdicts):
    """Trial verdict (EXECUTION 4): PASS only when exactly one entry each for checks a, b and c all PASS.
    Each entry names its check in "check" ("a"/"b"/"c") or as the "(a)" prefix of its name."""
    ids = []
    for c in checks:
        cid = str(c.get("check", "")).strip().lower()
        if not cid:
            m = re.match(r"^\(([abc])\)", str(c.get("name", "")).strip(), re.I)
            cid = m.group(1).lower() if m else ""
        ids.append(cid)
    if "FAIL" in verdicts:
        return "FAIL"
    if sorted(ids) != ["a", "b", "c"]:
        return "INCOMPLETE"
    return "PASS" if all(v == "PASS" for v in verdicts) else "PARTIAL"


def scorecard(spec, dest, width=1080):
    """spec: {"title": str, "verdict": str?, "checks": [{"name","verdict","numbers":[..],"file"}]}"""
    checks = spec["checks"]
    verdicts = [c.get("verdict", "PARTIAL").upper() for c in checks]
    overall = overall_verdict(checks, verdicts)
    pad = 28
    blocks = [110 + 34 * min(len(c.get("numbers", [])), 4) for c in checks]
    H = 130 + sum(blocks) + pad * (len(checks) + 1)
    img = Image.new("RGB", (width, H), (20, 22, 26))
    d = ImageDraw.Draw(img)
    d.text((pad, 24), spec.get("title", "Chimera Unreal trial"), fill=(235, 235, 235), font=font(40, True))
    d.text((pad, 80), "TRIAL " + overall.upper(), fill=VERDICT_COL.get(overall.upper(), (200, 200, 200)), font=font(34, True))
    y = 130 + pad
    for c, v, bh in zip(checks, verdicts, blocks):
        d.rounded_rectangle((pad, y, width - pad, y + bh), radius=10, fill=(32, 35, 41),
                            outline=VERDICT_COL.get(v, (120, 120, 120)), width=3)
        d.text((pad + 20, y + 12), c["name"], fill=(235, 235, 235), font=font(30, True))
        vw = d.textlength(v, font=font(30, True))
        d.text((width - pad - 20 - vw, y + 12), v, fill=VERDICT_COL.get(v, (200, 200, 200)), font=font(30, True))
        yy = y + 58
        for t in c.get("numbers", [])[:4]:
            d.text((pad + 20, yy), str(t)[:70], fill=(210, 210, 210), font=font(24))
            yy += 34
        d.text((pad + 20, yy + 2), "open: " + str(c.get("file", ""))[:80], fill=(150, 160, 175), font=font(20))
        y += bh + pad
    img.save(dest, "JPEG", quality=90, optimize=True)
    return dest


ROOT_FILES = {"README.md", "trial-scorecard.jpg", "lock-usage.csv"}


def git_candidates(ev):
    """Files under EV that git tracks or would add (tracked + untracked, not ignored), as absolute paths.
    Returns (paths, note): paths is None when EV is not inside a git work tree. Raises EvError on a git failure."""
    try:
        inside = subprocess.run(["git", "-C", ev, "rev-parse", "--is-inside-work-tree"], capture_output=True, text=True)
    except OSError as e:
        raise EvError("git not runnable: %s" % e)
    if inside.returncode != 0 or inside.stdout.strip() != "true":
        return None, "EV is not inside a git work tree; checked every file on disk instead"
    top = subprocess.run(["git", "-C", ev, "rev-parse", "--show-toplevel"], capture_output=True, text=True)
    ls = subprocess.run(["git", "-C", ev, "ls-files", "-z", "--full-name", "--cached", "--others", "--exclude-standard", "--", "."],
                        capture_output=True, text=True)
    if top.returncode != 0 or ls.returncode != 0:
        raise EvError("git failed: %s%s" % (top.stderr.strip(), ls.stderr.strip()))
    t = top.stdout.strip()
    return [os.path.normpath(os.path.join(t, rel)) for rel in sorted(set(filter(None, ls.stdout.split("\0"))))], None


def disk_files(ev):
    out = []
    for root, dirs, files in os.walk(ev):
        dirs[:] = [d for d in dirs if d not in (".git", "__pycache__")]
        out += [os.path.normpath(os.path.join(root, f)) for f in files]
    return sorted(out)


def check_ev(ev, notes=None):
    """Return a list of problems (empty = ok). Notes (not failures) are appended to `notes` when given."""
    problems = []
    manifests = {}
    for check in ("a", "b", "c"):
        rows = load_manifest(ev, check)
        manifests[check] = rows
        for r in rows:
            p = os.path.join(ev, check, r["file"]) if r.get("committed") else (r.get("path") or os.path.join(ev, check, r["file"]))
            if not os.path.isfile(p):
                problems.append("%s: missing %s" % (check, p))
                continue
            if sha256(p) != r["sha256"]:
                problems.append("%s: sha256 mismatch %s" % (check, r["file"]))
            if r.get("committed") and not policy_ok(check, r["file"], os.path.getsize(p)):
                problems.append("%s: %s violates the commit policy (type or size)" % (check, r["file"]))
    # every file that is (or would be) committed under EV must obey the git policy 2.3
    cands, note = git_candidates(ev) if os.path.isdir(ev) else ([], None)
    if cands is None:
        cands = disk_files(ev)
        if notes is not None:
            notes.append(note)
    totals = {"a": 0, "b": 0, "c": 0}
    for full in cands:
        rel = os.path.relpath(full, ev).replace("\\", "/")
        if not os.path.isfile(full):
            continue  # tracked but deleted on disk: git status shows it; nothing to size
        parts = rel.split("/")
        size = os.path.getsize(full)
        ext = os.path.splitext(full)[1].lower()
        if len(parts) == 1:
            if parts[0] not in ROOT_FILES:
                problems.append("unexpected file at EV root: %s (allowed: %s)" % (rel, ", ".join(sorted(ROOT_FILES))))
            elif not policy_ok(None, rel, size):
                problems.append("%s (%d bytes) beyond the size/type policy" % (rel, size))
            continue
        chk = parts[0] if parts[0] in totals else None
        if chk is None:
            problems.append("file outside EV/a, EV/b, EV/c: %s" % rel)
            continue
        totals[chk] += size
        if parts[-1] == "manifest.json":
            if size > LIMITS["text"]:
                problems.append("%s (%d bytes) beyond the size/type policy" % (rel, size))
            continue
        if chk == "b" and ext in IMG_EXT | {".mp4"}:
            problems.append("git would commit (b) image %s" % rel)
            continue
        if not policy_ok(chk, rel, size):
            problems.append("git would commit %s (%d bytes) beyond the size/type policy" % (rel, size))
        listed = [r for r in manifests[chk] if r.get("committed") and r["file"] == parts[-1] and len(parts) == 2]
        if not listed:
            problems.append("%s is not in %s/manifest.json as a committed entry (use evidence.py add)" % (rel, chk))
    for chk, total in totals.items():
        if total > CHECK_CAP[chk]:
            problems.append("%s: committed evidence %d bytes exceeds %d" % (chk, total, CHECK_CAP[chk]))
    # every file on disk under EV, ignored by git or not, must be referenced by a manifest row (2.3: every (b) image
    # is listed with absolute path and sha256). Unlisted (b) images are problems; other unlisted files are notes.
    listed_paths, listed_names = set(), {"a": set(), "b": set(), "c": set()}
    for chk, rows in manifests.items():
        for r in rows:
            listed_names[chk].add(r.get("file"))
            if r.get("path"):
                p = r["path"] if os.path.isabs(r["path"]) else os.path.join(ev, r["path"])
                listed_paths.add(os.path.normcase(os.path.abspath(p)))
    for full in (disk_files(ev) if os.path.isdir(ev) else []):
        rel = os.path.relpath(full, ev).replace("\\", "/")
        parts = rel.split("/")
        if len(parts) == 1 or parts[-1] == "manifest.json":
            continue  # the root rule and the manifests themselves are handled above
        chk = parts[0] if parts[0] in listed_names else None
        if chk is None:
            continue  # already reported (if git would commit it) as outside EV/a, EV/b, EV/c
        if os.path.normcase(os.path.abspath(full)) in listed_paths or (len(parts) == 2 and parts[-1] in listed_names[chk]):
            continue
        if chk == "b" and os.path.splitext(full)[1].lower() in IMG_EXT | {".mp4"}:
            problems.append("(b) image %s is on disk but in no b/manifest.json row (use evidence.py add --register)" % rel)
        elif notes is not None:
            notes.append("%s is on disk but in no %s/manifest.json row" % (rel, chk))
    return problems


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--ev", default=DEFAULT_EV)
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("add")
    s.add_argument("--check", required=True); s.add_argument("file"); s.add_argument("--name")
    s.add_argument("--task", default=""); s.add_argument("--register", action="store_true"); s.add_argument("--build-id", default="")
    s = sub.add_parser("phone"); s.add_argument("file"); s.add_argument("--out")
    s = sub.add_parser("sheet")
    s.add_argument("images", nargs="+"); s.add_argument("--out", required=True)
    s.add_argument("--caption", action="append", default=[]); s.add_argument("--cols", type=int, default=3); s.add_argument("--title")
    s = sub.add_parser("scorecard"); s.add_argument("spec"); s.add_argument("--out", required=True)
    s = sub.add_parser("sent"); s.add_argument("--check", required=True); s.add_argument("name")
    sub.add_parser("check")
    a = ap.parse_args(argv)
    try:
        if a.cmd == "add":
            r = add(a.ev, a.check, a.file, a.name, a.task, a.register, a.build_id)
            print("ADDED %s sha256=%s bytes=%d committed=%s" % (r["file"], r["sha256"][:12], r["bytes"], r["committed"]))
        elif a.cmd == "phone":
            p = phone(a.file, a.out)
            print("PHONE %s %d bytes" % (p, os.path.getsize(p)))
        elif a.cmd == "sheet":
            print("SHEET", sheet(a.images, a.out, a.caption, a.cols, title=a.title))
        elif a.cmd == "scorecard":
            with open(a.spec, "r", encoding="utf-8") as f:
                print("SCORECARD", scorecard(json.load(f), a.out))
        elif a.cmd == "sent":
            mark_sent(a.ev, a.check, a.name)
            print("SENT", a.name)
        elif a.cmd == "check":
            notes = []
            pr = check_ev(a.ev, notes)
            for n in notes:
                print("EVIDENCE NOTE:", n)
            for p in pr:
                print("EVIDENCE PROBLEM:", p)
            print("EVIDENCE CHECK %s" % ("FAIL" if pr else "OK"))
            return 1 if pr else 0
    except (EvError, OSError, KeyError, json.JSONDecodeError) as e:
        print("evidence.py: %s" % e, file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
