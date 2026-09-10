"""Mirror the irreplaceable asset-pipeline SOURCES off-drive, with sha256 sidecars.

Why this exists
---------------
Every later stage of the asset pipeline re-derives from two input sets that live
OUTSIDE the repository and exist in exactly one copy:

  * the raw high-poly meshes   ``D:\\tools\\asset-gen-work\\*_raw.glb``
  * the concept plates         ``<ComfyUI>/input/cc_*.png``

The shipped ``godot/assets/**.glb`` are lossy derivatives of the raws (decimated,
welded, material-stripped). If a raw is lost, the only way back is to re-run
generation, which is non-deterministic -- a different mesh, not the same one.
So the raws are treated as masters and mirrored before any stage is allowed to
touch them.

Enumeration is by DIRECTORY GLOB, never by manifest id. A raw exists on disk that
no manifest entry names (``cinderhand_thrall_raw.glb``), and a manifest-driven
mirror would silently skip it.

Off-drive
---------
The default destination is on a different physical drive from the sources, so a
single drive failure cannot take both. Pass ``--dest`` to override.

Usage
-----
    python vault_sources.py backup            # mirror + write sidecars + verify the copies
    python vault_sources.py verify            # re-hash every destination file against its sidecar
    python vault_sources.py status            # what is vaulted, what drifted, what is new

Exit codes: 0 = clean, 1 = mismatch / missing / drift, 2 = usage or environment error.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
from pathlib import Path

# --- source and destination defaults -------------------------------------------------

DEFAULT_SOURCES = [
    # (label, directory, glob)
    ("raws", Path(r"D:\tools\asset-gen-work"), "*_raw.glb"),
    ("concepts", Path(r"D:\tools\ComfyUI_windows_portable\ComfyUI\input"), "cc_*.png"),
]

DEFAULT_DEST = Path(r"C:\Chimera_AssetVault")

MANIFEST_NAME = "vault_manifest.json"
SIDECAR_SUFFIX = ".sha256"
CHUNK = 1024 * 1024


def sha256_file(path: Path) -> str:
    """Stream a file through sha256. Chunked so a 200 MB glb does not land in RAM."""
    h = hashlib.sha256()
    with path.open("rb") as fh:
        while True:
            block = fh.read(CHUNK)
            if not block:
                break
            h.update(block)
    return h.hexdigest()


def enumerate_sources(sources) -> list[tuple[str, Path]]:
    """Expand every (label, dir, glob) into concrete files, sorted for stable output.

    Returns a list of (label, path). A missing source DIRECTORY is fatal -- it means
    the caller is pointed at the wrong rig, and silently vaulting nothing is the worst
    possible outcome for a backup tool.
    """
    found: list[tuple[str, Path]] = []
    for label, directory, pattern in sources:
        if not directory.is_dir():
            print(f"FATAL: source directory does not exist: {directory}", file=sys.stderr)
            sys.exit(2)
        matches = sorted(directory.glob(pattern))
        if not matches:
            print(f"FATAL: source glob matched nothing: {directory}\\{pattern}", file=sys.stderr)
            sys.exit(2)
        found.extend((label, p) for p in matches)
    return found


def sidecar_for(dest_file: Path) -> Path:
    return dest_file.with_name(dest_file.name + SIDECAR_SUFFIX)


def cmd_backup(dest: Path, sources, force: bool) -> int:
    """Copy each source to <dest>/<label>/, then PROVE the copy by re-hashing the
    destination and comparing to the source hash. A copy that silently truncated is
    exactly the failure a backup must not have."""
    entries = enumerate_sources(sources)
    dest.mkdir(parents=True, exist_ok=True)

    manifest = {"entries": {}}
    copied = skipped = 0
    failures: list[str] = []

    for label, src in entries:
        out_dir = dest / label
        out_dir.mkdir(parents=True, exist_ok=True)
        dst = out_dir / src.name
        key = f"{label}/{src.name}"

        src_hash = sha256_file(src)

        # Skip an identical file that is already vaulted -- but only after hashing the
        # destination, never on size/mtime, which is what makes a corrupt vault look fine.
        if dst.exists() and not force:
            if sha256_file(dst) == src_hash:
                skipped += 1
                sidecar_for(dst).write_text(src_hash + "\n", encoding="ascii")
                manifest["entries"][key] = {
                    "sha256": src_hash,
                    "bytes": src.stat().st_size,
                    "source": str(src),
                }
                continue

        shutil.copy2(src, dst)
        dst_hash = sha256_file(dst)
        if dst_hash != src_hash:
            failures.append(f"{key}: copy did not verify (src {src_hash[:12]} != dst {dst_hash[:12]})")
            continue

        sidecar_for(dst).write_text(src_hash + "\n", encoding="ascii")
        manifest["entries"][key] = {
            "sha256": src_hash,
            "bytes": src.stat().st_size,
            "source": str(src),
        }
        copied += 1

    (dest / MANIFEST_NAME).write_text(json.dumps(manifest, indent=2, sort_keys=True), encoding="utf-8")

    total_bytes = sum(e["bytes"] for e in manifest["entries"].values())
    print(f"vault: {dest}")
    print(f"  copied      {copied}")
    print(f"  already ok  {skipped}")
    print(f"  entries     {len(manifest['entries'])}  ({total_bytes / 1e6:.1f} MB)")
    for label, _, pattern in sources:
        n = sum(1 for k in manifest["entries"] if k.startswith(label + "/"))
        print(f"  {label:<10} {n} files  ({pattern})")

    if failures:
        print(f"\nFAILED ({len(failures)}):", file=sys.stderr)
        for f in failures:
            print(f"  {f}", file=sys.stderr)
        return 1

    print("\nbackup OK -- every destination file re-hashed and matched its source")
    return 0


def cmd_verify(dest: Path) -> int:
    """Re-hash every destination file against its sidecar. This is the independent
    pass: it reads nothing from the sources, so it still works when D: is gone."""
    manifest_path = dest / MANIFEST_NAME
    if not manifest_path.is_file():
        print(f"FATAL: no vault manifest at {manifest_path} -- run 'backup' first", file=sys.stderr)
        return 2

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    entries = manifest.get("entries", {})
    if not entries:
        print("FATAL: vault manifest is empty", file=sys.stderr)
        return 2

    mismatches: list[str] = []
    missing: list[str] = []
    no_sidecar: list[str] = []
    ok = 0

    for key, meta in sorted(entries.items()):
        path = dest / key
        if not path.is_file():
            missing.append(key)
            continue

        side = sidecar_for(path)
        if not side.is_file():
            no_sidecar.append(key)
            continue

        expected = side.read_text(encoding="ascii").strip()
        actual = sha256_file(path)
        if actual != expected:
            mismatches.append(f"{key}: sidecar {expected[:12]} != file {actual[:12]}")
        elif expected != meta["sha256"]:
            mismatches.append(f"{key}: sidecar {expected[:12]} != manifest {meta['sha256'][:12]}")
        else:
            ok += 1

    print(f"vault: {dest}")
    print(f"  verified    {ok} / {len(entries)}")
    if missing:
        print(f"  MISSING     {len(missing)}", file=sys.stderr)
        for k in missing:
            print(f"    {k}", file=sys.stderr)
    if no_sidecar:
        print(f"  NO SIDECAR  {len(no_sidecar)}", file=sys.stderr)
        for k in no_sidecar:
            print(f"    {k}", file=sys.stderr)
    if mismatches:
        print(f"  MISMATCH    {len(mismatches)}", file=sys.stderr)
        for m in mismatches:
            print(f"    {m}", file=sys.stderr)

    if missing or no_sidecar or mismatches:
        return 1
    print(f"\nverify OK -- {ok} files re-hashed against their sidecars, zero mismatches")
    return 0


def cmd_status(dest: Path, sources) -> int:
    """Compare live sources against the vault: what drifted, what is new, what is gone.

    Source drift is not automatically an error -- a regenerated raw is a legitimate
    change -- but it must be visible, because every later slice re-derives from these.
    """
    manifest_path = dest / MANIFEST_NAME
    manifest = (
        json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.is_file() else {"entries": {}}
    )
    entries = manifest.get("entries", {})

    live = {f"{label}/{p.name}": p for label, p in enumerate_sources(sources)}

    new = sorted(set(live) - set(entries))
    gone = sorted(set(entries) - set(live))
    drifted = []
    same = 0
    for key, path in sorted(live.items()):
        if key in entries:
            if sha256_file(path) != entries[key]["sha256"]:
                drifted.append(key)
            else:
                same += 1

    print(f"vault: {dest}   (entries: {len(entries)})")
    print(f"  unchanged   {same}")
    print(f"  NEW         {len(new)}")
    for k in new:
        print(f"    + {k}")
    print(f"  DRIFTED     {len(drifted)}")
    for k in drifted:
        print(f"    ~ {k}")
    print(f"  GONE        {len(gone)}")
    for k in gone:
        print(f"    - {k}")

    return 1 if (new or drifted or gone) else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["backup", "verify", "status"])
    ap.add_argument("--dest", type=Path, default=DEFAULT_DEST, help=f"vault root (default: {DEFAULT_DEST})")
    ap.add_argument("--force", action="store_true", help="re-copy even when the destination already matches")
    args = ap.parse_args()

    if args.command == "backup":
        return cmd_backup(args.dest, DEFAULT_SOURCES, args.force)
    if args.command == "verify":
        return cmd_verify(args.dest)
    return cmd_status(args.dest, DEFAULT_SOURCES)


if __name__ == "__main__":
    sys.exit(main())
