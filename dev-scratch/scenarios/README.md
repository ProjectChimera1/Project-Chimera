# Dev scratch scenarios (not shipped)

Files relocated OUT of `godot/resources/data/scenarios/` by DW-461 (2026-08-04).

That directory is the SHIPPED skirmish map catalog: `SkirmishCatalog.ScanMaps` lists every
parseable `*.json` with at least one start position as a selectable, launchable map on the
skirmish setup screen — so dev/test scratch content saved there ships as a playable map.
`ShippedScenarioHygieneTests` (Tier-1) now pins the curated shipped-map id set; adding a new
shipped map is a deliberate act that updates both the directory and that allowlist.

Contents:

- `123.json` — "Alpha Skirmish" editor scratch save (2 slots)
- `my-new-map.json` — "My New Map" editor scratch save (2 slots)
- `123.chimera.zip` — ContentPackager export scratch of `123.json`

These stay in the repo (not deleted) in case any is worth promoting to a real map later —
to promote one, give it a proper id/display name, move it back, and add it to the allowlist
in `ShippedScenarioHygieneTests`.
