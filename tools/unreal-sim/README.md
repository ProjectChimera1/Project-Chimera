# tools/unreal-sim — mirror of check (a)'s Unreal-side text

Source of truth: `D:/Projects/Chimera-Unreal/ProjectChimera` (local git, no remote). This folder is the off-machine copy,
refreshed at every checkpoint (EXECUTION.md §1.4, decision D5): the `ChimeraSimHost` module, the Target files, the
`.uproject`, and `SimTrial/` (unit mesh import tooling and `unit_meshes.json`). Binaries, Content and run output are not mirrored.

Rebuilding the unit content from a clean checkout (Content is git-ignored): run `SimTrial/tools/import_units.sh` under the
lock. Since A11 it chains `fix_unit_usage.sh` (the team material instances need the "Used with Instanced Static Meshes"
usage, or `-game` draws the renderer's ISMs with the default material). `SimTrial/tools/test_check_shots.py` is the
synthetic self-test of `check_shots.py` (no engine needed).
