#!/usr/bin/env python
"""Pin the Google web fonts the Match mockup loads (plan B section 3, task T0).

Match.dc.html:13 loads Cinzel, Inter and JetBrains Mono from Google Fonts with display=swap. Google serves variable
woff2 files that can change at any time, so the reference render must not depend on the live service. This script
fetches the CSS (with the same user agent the headless Chromium sends, so the answer is the woff2 one) and every
distinct woff2 file ONCE, stores them under H/HudRef/webfonts/, and writes webfonts.lock.json (sha256 of the CSS and of
every file). On every later run it only verifies the files against the lock and never touches the network.
The same files are mirrored into T/webfonts/ (versioned in R; H/HudRef is git-ignored in U, which has no remote): when
the working copy in H is missing or changed, it is restored from the mirror, file by file, only if the mirror file
matches the lock.

  python fetch_webfonts.py            # fetch if no lock, otherwise verify
  python fetch_webfonts.py --verify   # verify only, never fetch (exit 1 on any mismatch or missing file)
  python fetch_webfonts.py --refresh  # re-fetch and overwrite the lock (changes every reference: re-run T0 + recalibrate)
"""
import argparse, hashlib, json, re, sys, urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
H = Path("D:/Projects/Chimera-Unreal/ChimeraHud")
DEST = H / "HudRef" / "webfonts"
LOCK = HERE / "webfonts.lock.json"
MIRROR = HERE / "webfonts"  # versioned copy in R (OFL 1.1, see MIRROR/LICENSES.txt)
CSS_URL = ("https://fonts.googleapis.com/css2?family=Cinzel:wght@500;600;700&family=Inter:wght@400;500;600;700"
           "&family=JetBrains+Mono:wght@400;500;600&display=swap")  # verbatim from Match.dc.html:13
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
      "HeadlessChrome/154.0.0.0 Safari/537.36")  # the playwright-cli Chromium UA (r4 meta.userAgent)


def sha(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def get(url: str) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read()


def parse_faces(css: str):
    """[(family, weight, subset-comment, url)] for every @font-face block."""
    out = []
    for m in re.finditer(r"/\*\s*([^*]+?)\s*\*/\s*@font-face\s*\{(.*?)\}", css, re.S):
        body = m.group(2)
        fam = re.search(r"font-family:\s*'([^']+)'", body).group(1)
        wgt = re.search(r"font-weight:\s*([0-9 ]+);", body).group(1).strip()
        url = re.search(r"url\((https://[^)]+)\)", body).group(1)
        out.append((fam, wgt, m.group(1), url))
    return out


def fetch():
    DEST.mkdir(parents=True, exist_ok=True)
    css_bytes = get(CSS_URL)
    css = css_bytes.decode("utf-8")
    faces = parse_faces(css)
    if len(faces) != css.count("@font-face"):
        sys.exit("FAIL: could not parse every @font-face block")
    (DEST / "google.css").write_bytes(css_bytes)
    files = {}
    for fam, wgt, subset, url in faces:
        e = files.setdefault(url, {"url": url, "faces": []})
        e["faces"].append({"family": fam, "weight": wgt, "subset": subset})
    for url, e in files.items():
        data = get(url)
        name = url.rsplit("/", 1)[1]
        (DEST / name).write_bytes(data)
        e.update(file=name, sha256=sha(data), bytes=len(data))
    lock = {"css_url": CSS_URL, "user_agent": UA, "css_sha256": sha(css_bytes), "css_file": "google.css",
            "face_blocks": len(faces), "files": sorted(files.values(), key=lambda e: e["file"])}
    LOCK.write_text(json.dumps(lock, indent=1) + "\n")
    MIRROR.mkdir(exist_ok=True)
    for f in ["google.css"] + [e["file"] for e in lock["files"]]:
        (MIRROR / f).write_bytes((DEST / f).read_bytes())
    return lock


def restore_from_mirror(lock, bad):
    """Copy every missing or changed working file back from the R mirror when the mirror copy matches the lock."""
    want = {lock["css_file"]: lock["css_sha256"], **{e["file"]: e["sha256"] for e in lock["files"]}}
    DEST.mkdir(parents=True, exist_ok=True)
    n = 0
    for f in bad:
        m = MIRROR / f
        if m.exists() and sha(m.read_bytes()) == want[f]:
            (DEST / f).write_bytes(m.read_bytes())
            n += 1
    return n


def verify():
    if not LOCK.exists():
        return None, ["no lock file"]
    lock = json.loads(LOCK.read_text())
    bad = []
    css = DEST / lock["css_file"]
    if not css.exists() or sha(css.read_bytes()) != lock["css_sha256"]:
        bad.append("google.css")
    for e in lock["files"]:
        p = DEST / e["file"]
        if not p.exists() or sha(p.read_bytes()) != e["sha256"]:
            bad.append(e["file"])
    return lock, bad


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--verify", action="store_true")
    ap.add_argument("--refresh", action="store_true")
    a = ap.parse_args()
    lock, bad = verify()
    if a.refresh or (lock is None and not a.verify) or (bad and not a.verify and lock is None):
        lock = fetch()
        lock, bad = verify()
        print(f"fetched {len(lock['files'])} woff2 files for {lock['face_blocks']} font-face blocks")
    if bad and lock is not None and not a.refresh:
        n = restore_from_mirror(lock, bad)
        if n:
            print(f"restored {n} file(s) from the R mirror {MIRROR}")
            lock, bad = verify()
    mirror_bad = [f for f in ["google.css"] + [e["file"] for e in lock["files"]]
                  if not (MIRROR / f).exists() or sha((MIRROR / f).read_bytes()) != (lock["css_sha256"] if f == "google.css" else next(e["sha256"] for e in lock["files"] if e["file"] == f))]
    if mirror_bad:
        print("WEBFONTS FAIL (R mirror missing or changed): " + ", ".join(mirror_bad))
        sys.exit(1)
    if bad:
        print("WEBFONTS FAIL (missing or changed): " + ", ".join(bad))
        sys.exit(1)
    print(f"webfonts ok: {len(lock['files'])} woff2 files, css sha256 {lock['css_sha256'][:12]} (working copy and R mirror)")


if __name__ == "__main__":
    main()
