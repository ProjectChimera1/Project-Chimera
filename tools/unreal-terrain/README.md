# tools/unreal-terrain

Text mirror of `D:/Projects/Chimera-Unreal/ChimeraTerrain` (trial check c, runtime terrain editing with RealtimeMeshComponent).
The Unreal project is the working copy; this folder is its off-machine copy, written by `ChimeraTerrain/Tools/sync_to_repo.sh`
(runs `tools/unreal-trial/secret_scan.py` first). Do not edit here.

Mirrored: `Source/`, `Config/`, `Tools/`, `Scripts/`, `ChimeraTerrain.uproject`, `VENDOR.md`.
Never mirrored: `Binaries`, `Intermediate`, `Saved`, `Content`, `Plugins` (the RMC clone), `Out`, `Textures`.

RMC is vendored at `b8669a0891b728d8fa8a31f43f76d3853c10f743` with two patches; `VENDOR.md` carries the full diff.
Plan: `docs/unreal-move/trial-checks/plan-c-runtime-terrain.md`.
