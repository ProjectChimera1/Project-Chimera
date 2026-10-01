# proto-a: preserved prototypes for trial check (a)

Copied 2026-10-01 from the session scratchpad (session-scoped, deleted when the session ends) so plan A's
evidence survives. **Reference copies only**: the `.csproj` files import `SimSources.props` and test sources
through absolute scratchpad paths (`...\scratchpad\trial2\godot\...`) and will not build from here as is.
Generated sources (`spike/gen`), `bin/`, `obj/` and publish outputs were not copied.

| Path | What it is |
|---|---|
| `r1_trial_migration_src.diff`, `r1_trial_migration_tests.diff` | R1's STJ source-generation migration (`diff -ruN --strip-trailing-cr`, no index lines; `git apply --check` passes at 5e6bc03d) |
| `proto/` | R2's `trial_1000` prototype: `Program.cs`, `gen_scenario.py`, `trial_1000.json`, traces (`full_every1.txt` = F19 values; `jit_lite_*` vs `aot_lite_*` byte-identical) |
| `spikeaot2/` | R1's AOT-vs-JIT golden-builder spike (22 sequences, compared by fnv/last, not against committed goldens) |
| `spike/` | R1's reflection-vs-source-gen differential oracle harness (sources only) |
| `aotsmoke/` | first `NativeLib=Shared` proof (`smoke_add`) |
| `cppload/` | cl.exe C++ console that loads `aotsmoke.dll` |
| `pe_exports_review.py` | stdlib-only PE export-table reader (basis for `tools/sim-trial/check_exports.py`) |
