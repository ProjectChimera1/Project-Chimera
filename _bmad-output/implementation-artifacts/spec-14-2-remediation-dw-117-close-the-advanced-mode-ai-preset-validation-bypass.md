---
title: 'DW-117 remediation: close the Advanced-mode raw-JSON ai_preset validation bypass'
type: 'bugfix'
created: '2026-07-15'
status: 'done'
baseline_revision: 'e54bda96dbe38f2ae82eb7fca0d783e06555d4e6'
final_revision: '4c216115df80d2ba53cd146f43bbc28094279a9e'
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: []
---

<intent-contract>

## Intent

**Problem:** In the Faction Definer's Advanced (raw-JSON) mode, `FactionDefinerWizardCore.TryFinishFromRawJson` deserializes the pasted document straight into a `FactionDefinition` via `JsonSerializer.Deserialize`. A document that **omits the `ai_preset` key entirely** leaves the C# class default (`AiPreset = "balanced"`) untouched, so validation passes and a faction is written with an *unauthored* preset — bypassing the "must explicitly author `ai_preset`" guarantee Simple mode enforces (Simple's `ResetWizard` forces `_draft.AiPreset = ""`, which the validator then rejects with "must be authored"). This is a real asymmetry between the two authoring modes for the same field.

**Approach:** Make the raw-JSON path distinguish "`ai_preset` key absent" from "`ai_preset` key present (even if empty)". After a successful deserialize, re-inspect the same JSON via `JsonNode`; if the root object does not contain an `ai_preset` key, force `parsed.AiPreset = ""` before delegating to `TryFinish`, so an omitted key flows through the exact same `FactionValidator.ValidateComplete` "must be authored" rejection Simple mode already produces. A present-but-empty (`""` / JSON `null`) key already routes to that same rejection and stays unchanged.

## Boundaries & Constraints

**Always:**
- Reuse the existing lenient parse semantics: the `JsonNode` re-inspection must tolerate comments and trailing commas the same way `FactionDefinition.JsonOptions` does (`CommentHandling = Skip`, `AllowTrailingCommas = true`), so any document the deserialize accepted is also re-parseable.
- Key-presence check is case-sensitive on the exact literal `ai_preset`, matching `FactionDefinition.JsonOptions` (which does not set `PropertyNameCaseInsensitive`) — the deserializer only maps the exact key, so an off-case key is correctly treated as "absent".
- `TryFinishFromRawJson` must still never throw: any failure to re-inspect must not turn a previously-accepted document into a crash.
- A present `ai_preset` key with any value (including `""`, JSON `null`, `"balanced"`, or an unknown string) keeps its current outcome — this change only affects the key-absent case.

**Block If:**
- (none — the closure approach is fully specified by DW-117; no unattended design decision is required.)

**Never:**
- Do not change Simple-mode enforcement, `FactionValidator`, `FactionDefinition.AiPreset`'s C# default, or `SerializeDraftClean`.
- Do not add a new error field/path — an omitted key must surface through the existing `ai_preset` validator error, identical to Simple mode.
- Do not expand the `KnownAiPresets` closed set or otherwise touch which preset values are valid.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Key absent (the bug) | Valid faction JSON with no `ai_preset` key at all | Blocked: located `ai_preset` "must be authored" error; no file written | Located `Failure`, never throws |
| Key present, empty | `"ai_preset": ""` | Blocked with the same `ai_preset` error (unchanged from today) | Located `Failure` |
| Key present, JSON null | `"ai_preset": null` | Blocked with the same `ai_preset` error (unchanged) | Located `Failure` |
| Key present, valid | `"ai_preset": "balanced"` | Accepted (unchanged) — file written | No error expected |
| Key present, unknown | `"ai_preset": "aggressive"` | Blocked: "not a recognized ai_preset" (unchanged) | Located `Failure` |
| Malformed / literal null doc | `{ not valid` / `null` | Blocked with `raw_json` error (unchanged) — re-inspection never reached | Located `Failure`, never throws |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/FactionDefinerWizardCore.cs` -- `TryFinishFromRawJson` (lines ~209-233) is the only production change: after the null-check, before `return TryFinish(parsed, …)`, re-inspect via `JsonNode` and force `AiPreset = ""` when the key is absent. `System.Text.Json.Nodes` is already imported (line 7).
- `godot/src/Core/Definitions/FactionValidator.cs` -- `ValidateComplete`'s ai_preset closed-set check (lines ~136-142) is the existing rejection an absent key must now reach; read-only reference, not changed.
- `godot/src/Core/Definitions/FactionDefinition.cs` -- `AiPreset` default `"balanced"` (line 45) and `JsonOptions` (lines 184-188); read-only reference for parse semantics.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionDefinerWizardTests.cs` -- add the key-absent test here. Note the existing `TryFinishFromRawJson_ValidJsonMissingAiPreset_...` (line 471) is misnamed: `SerializeDraftClean` always writes `ai_preset` (`root["ai_preset"] = def.AiPreset ?? ""`), so it exercises the present-but-empty case, NOT key absence.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/FactionDefinerWizardCore.cs` -- In `TryFinishFromRawJson`, after the `if (parsed == null)` block and before delegating to `TryFinish`, parse the same `json` with `JsonNode.Parse` using a `JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }`; if the parsed root is a `JsonObject` that does not contain the key `ai_preset`, set `parsed.AiPreset = ""`. Wrap the re-inspection so a re-parse failure leaves `parsed` unchanged rather than throwing (preserve the never-throws contract). Update the method's XML doc comment to record the omitted-key normalization. -- Closes the DW-117 bypass by making an omitted key flow through the same validator rejection Simple mode produces.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionDefinerWizardTests.cs` -- Add a test that builds a valid faction JSON with the `ai_preset` key literally absent (e.g. take `SerializeDraftClean` output and strip the `ai_preset` line, or hand-author a minimal object without it) and asserts `TryFinishFromRawJson` returns `!Ok`, contains an `ai_preset` error, and writes no `_faction.json` file. Give it an unambiguous name (e.g. `TryFinishFromRawJson_AiPresetKeyAbsent_BlockedSameAsSimpleMode_NoFileWritten`) and add a one-line comment on the existing line-471 test clarifying it covers the present-but-empty case, not key absence. -- Covers the previously-uncovered key-absent branch and prevents regression.

**Acceptance Criteria:**
- Given a syntactically valid faction JSON (comments/trailing commas allowed) that contains no `ai_preset` key, when `TryFinishFromRawJson` runs, then it returns a located `Failure` with an `ai_preset` error and writes no faction file — byte-for-byte the same outcome Simple mode gives for an unauthored preset.
- Given a faction JSON whose `ai_preset` key is present with a valid value (`"balanced"`), when `TryFinishFromRawJson` runs, then the faction is written exactly as before this change.
- Given a malformed document or the literal `null`, when `TryFinishFromRawJson` runs, then it still returns the existing `raw_json` located failure and never throws.

## Review Triage Log

### 2026-07-15 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 5: (high 0, medium 1, low 4)
- defer: 0
- reject: 7: (high 0, medium 0, low 7)
- addressed_findings:
  - `[medium]` `[patch]` Duplicate-key bypass (Blind Hunter + Edge Case Hunter; confirmed empirically). `JsonNode.Parse` throws on duplicate property names where `JsonSerializer.Deserialize` tolerates them (last-wins), so a raw-JSON doc that omits `ai_preset` AND duplicates another top-level key would throw inside the re-inspection, hit the best-effort `catch`, skip normalization, and reopen the exact DW-117 bypass (unauthored `"balanced"` written). This violated the spec's own "Always: any document the deserialize accepted is also re-parseable" invariant. Fixed by swapping the re-inspection to `JsonDocument.Parse` (lenience-equivalent, incl. duplicate-key tolerance) via `RootElement.ValueKind == Object && !TryGetProperty("ai_preset", …)`; updated the code doc-comment/inline comment to record why `JsonDocument` over `JsonNode`; added `TryFinishFromRawJson_AiPresetKeyAbsentWithDuplicateOtherKey_...` regression test (fails against the old `JsonNode.Parse` code).
  - `[low]` `[patch]` Off-case `ai_preset` key behavior was asserted only in a comment (Blind Hunter, Intent Alignment). Added `TryFinishFromRawJson_AiPresetKeyOffCase_TreatedAsAbsent_...` locking the load-bearing case-sensitivity (off-case key ignored by deserialize AND absent to the case-sensitive `JsonDocument` check → forced `""` → blocked).
  - `[low]` `[patch]` Brittle test fixtures (Blind Hunter). Absent-key test built its input by line-stripping serialized output; null/unknown tests used unqualified `string.Replace("\"balanced\"", …)` that would corrupt any incidental `"balanced"` substring. Replaced with `BuildValidRawFactionJson`/`RewriteAiPresetLine`/`DuplicateTopLevelIdLine` helpers that target only the single top-level `ai_preset` line and assert exactly-one-occurrence so they fail loudly on serializer-format drift.
  - `[low]` `[patch]` `..._KeyPresentButNull_..._KeyPresentUnchanged` name over-claimed relative to its assertions (Blind Hunter). Renamed to `..._Blocked_NormalizationDoesNotFire` and clarified the comment.
  - `[low]` `[patch]` No test asserted the located error routes to the AI Preset wizard step (Blind Hunter). Added `Assert.Equal(FactionDefinerStep.AiPreset, result.Step)` to the key-absent test.
- rejected (not this story's problem, on the authority noted):
  - Change `FactionDefinition.AiPreset`'s C# default from `"balanced"` to `""` so omission fails uniformly — the intent's **Never** explicitly forbids touching the default; cross-cutting redesign, out of scope.
  - `FactionDefinition.LoadFromFile` shares the key-omission gap — different surface (loading already-authored files), outside DW-117's wizard Simple-vs-Advanced asymmetry; load-time defaulting to a valid preset is a defensible separate contract.
  - JSON parsed twice per raw-JSON finish (perf) — conceded acceptable for an editor path by the reporter.
  - Redundant `json ?? ""` null-guard — harmless; `json` is already non-null here, guard documents intent at zero cost.
  - Comment references a test "below" (ordering coupling); `_knownAiPresets` case-insensitive vs case-sensitive key check asymmetry undocumented; no positive test that a successful finish re-emits `ai_preset` — cosmetic or already covered (the valid-json test round-trips and asserts `reloaded.AiPreset == "balanced"`).

## Design Notes

Order matters: the `JsonNode` re-inspection runs only AFTER the POCO deserialize succeeds and `parsed != null`, so malformed/`null` documents keep their existing `raw_json` failure and never reach the re-parse. Because the deserialize already succeeded with `FactionDefinition.JsonOptions`, an equivalently-lenient `JsonNode.Parse` will parse the same text; the try/guard is belt-and-suspenders for the never-throws contract, not an expected path. Checking key *presence* (not value) is the whole point — it is the only way to tell "creator omitted the key, inheriting the silent `balanced` default" from "creator wrote the key", mirroring how Simple mode's forced `""` makes omission impossible there.

Sketch (inside `TryFinishFromRawJson`, after the null-check):

```csharp
try
{
    if (JsonNode.Parse(json ?? "", null,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
        is JsonObject root && !root.ContainsKey("ai_preset"))
    {
        parsed.AiPreset = "";   // omitted key == Simple mode's forced "" -> validator "must be authored"
    }
}
catch { /* re-inspection is best-effort; never turn an accepted doc into a throw */ }

return TryFinish(parsed, factionsDirAbsolute);
```

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: build succeeds, no new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~FactionDefinerWizardTests"` -- expected: all tests pass, including the new key-absent test and the unchanged existing raw-JSON tests.

## Auto Run Result

Status: done

### Summary
Closed the DW-117 Advanced-mode raw-JSON `ai_preset` validation bypass. `TryFinishFromRawJson` now re-inspects the pasted document after deserialize and, when the `ai_preset` key is entirely absent, forces `AiPreset = ""` so the omitted key flows through the same `FactionValidator.ValidateComplete` "must be authored" rejection Simple mode enforces — eliminating the asymmetry where an omitted key silently inherited the C# `"balanced"` default. A key that is present (empty, null, valid, or unknown) keeps its prior outcome.

### Files changed
- `godot/src/Core/Definitions/FactionDefinerWizardCore.cs` — In `TryFinishFromRawJson`, after the null-check, re-inspect the same JSON via `JsonDocument.Parse` (lenient options) and force `AiPreset = ""` when the root object lacks the case-sensitive `ai_preset` key; guarded so it never throws. Extended the XML doc-comment to record the normalization and the `JsonDocument`-over-`JsonNode` rationale.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionDefinerWizardTests.cs` — Added raw-JSON tests: key-absent (+ asserts routing to the AI Preset step), key-absent-with-duplicate-other-key (duplicate-key regression guard), off-case key, present-null, present-unknown; added `BuildValidRawFactionJson`/`RewriteAiPresetLine`/`DuplicateTopLevelIdLine` helpers; added a clarifying NOTE to the pre-existing present-but-empty test.

### Review findings
- Reviewers: Blind Hunter, Edge Case Hunter, Verification-Gap (no gaps), Intent Alignment (descriptive; divergences enforcement-equivalent).
- Patches applied (5): 1 medium — duplicate-key bypass (`JsonNode.Parse` throws on duplicate keys where the deserialize tolerates them; swapped to `JsonDocument.Parse` + regression test, empirically confirmed to fail against the old code); 4 low — off-case-key test, test-fixture robustness, test rename/clarify, wizard-step assertion.
- Deferred: none.
- Rejected (7): change the C# `AiPreset` default (forbidden by intent's Never), `LoadFromFile` same-gap (different surface, out of scope), double-parse perf (editor path), redundant null-guard, and three cosmetic/already-covered nits. See Review Triage Log.

### Verification
- `dotnet build godot/godot.sln` — succeeded, 0 errors (11 warnings, all pre-existing in untouched files).
- `dotnet test …FactionDefinerWizardTests` — 41/41 passed.
- `dotnet test` (full Sim.Tests suite) — 1750 passed, 1 skipped (pre-existing), 0 failed.

### Residual risks
None material. The re-inspection runs only after a successful deserialize, so malformed/`null` documents keep their existing `raw_json` failure; `JsonDocument.Parse` is lenience-equivalent to the deserialize (including duplicate-key tolerance, empirically verified), so the best-effort `catch` is now unreachable in practice. The bypass is closed at the wizard raw-JSON surface only, as scoped by DW-117.
