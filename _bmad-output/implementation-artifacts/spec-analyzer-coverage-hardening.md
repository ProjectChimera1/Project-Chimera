---
title: 'Analyzer coverage hardening (BannedSimApiAnalyzer CHM0001–CHM0004 + tests + CI guard)'
type: 'refactor'
created: '2026-07-19'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '7fc6cad9725afc7ca5cf11d350de5088733d3b29'
final_revision: '325c3722a1c67f37e71f5bef8668c6a2e6e2eef0'
context:
  - '{project-root}/godot/CLAUDE.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** The advisory `BannedSimApiAnalyzer` (Story 1.10b) has known coverage gaps and false-positives flagged in the 1.10b review (DW-3..DW-8): CHM0002 only sees `foreach`; CHM0001 misses `System.Single`/`Double` and `var`-inferred float; CHM0003 over-flags the two real total-order-comparer sorts (`ScenarioDirector.cs:483`, `LocalProfileSource.cs:121`) and misses `Span<T>.Sort`; CHM0004 false-positives on loop bounds and is blind to `static readonly` caps and negated bounds; the test suite has a vacuous CHM0003 negative; and the CI release-gate string compare has no guard against a `== true` regression.

**Approach:** Extend the four rules and their tests within `BannedSimApiAnalyzer.cs` + `BannedSimApiAnalyzerTests.cs`, and add a guard comment in `determinism-gate.yml`. All rules stay advisory (Warning); no severity or release-gating changes.

## Boundaries & Constraints

**Always:**
- Keep all rules at `DiagnosticSeverity.Warning` (advisory); do not touch the release-gate ratchet or RS0030 baseline.
- Keep every existing passing test green — extensions are additive; only the one vacuous test is replaced.
- New syntactic detection must prefilter cheaply (text/kind check) before any semantic-model call.
- CHM0003 must treat a Sort that carries an `IComparer`/`IComparer<T>`/`Comparison<T>` argument as developer-controlled ordering and NOT flag it — this is how the two real sites clear without a suppression.
- CHM0004 remains a heuristic/advisory rule; changes only shift its precision, not its severity.

**Block If:**
- The intent would require changing a rule's severity, the release gate, or the zero-baseline RS0030 set (out of scope — would need human sign-off).

**Never:**
- Do not edit the deferred-work ledger (`{implementation_artifacts}/deferred-work.md`) — the orchestrator records resolution.
- Do not add new diagnostic IDs (CHM0007+) or re-baseline goldens.
- Do not add per-entity Godot Node usage or touch sim gameplay code — this is analyzer + test + CI only.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| CHM0002 `.Keys`/`.Values` foreach | `foreach (var k in dict.Keys)` | CHM0002 fires (KeyCollection/ValueCollection is unordered) | — |
| CHM0002 LINQ on dict | `dict.Select(...)`, `.First()`, `.Aggregate(...)` on Dictionary/HashSet | CHM0002 fires on the unordered receiver | — |
| CHM0002 ordering LINQ | `dict.OrderBy(...)` / `.OrderByDescending(...)` | CHM0002 does NOT fire (imposes deterministic order) | — |
| CHM0002 `.GetEnumerator()` | explicit `dict.GetEnumerator()` | CHM0002 fires | — |
| CHM0002 LINQ on List | `list.Select(...)` | CHM0002 does NOT fire (ordered source) | — |
| CHM0001 fully-qualified | `System.Single x;` / `System.Double y;` | CHM0001 fires once per type reference | — |
| CHM0001 var-inferred | `var x = 1f;` / `var d = 1.0;` | CHM0001 fires (inferred float/double) | inference failure/error type → skip |
| CHM0001 member access | `System.Single.Parse(s)` / `Single.MaxValue` | CHM0001 does NOT fire (RS0030/CHM0006 own member access) | — |
| CHM0003 total-order comparer | `list.Sort((a,b)=>a.Id.CompareTo(b.Id))`, `Array.Sort(a, cmp)` | CHM0003 does NOT fire (comparer/comparison arg present) | — |
| CHM0003 comparerless | `list.Sort()`, `Array.Sort(a)`, `span.Sort()` | CHM0003 fires | — |
| CHM0004 loop bound | `for (int i=0; i<100; i++)` / `while (i < 100)` | CHM0004 does NOT fire (loop-condition bound) | — |
| CHM0004 negated bound | `if (x < -64)` | CHM0004 fires with value `-64` | — |
| CHM0004 static readonly cap | `static readonly int Max = 64;` | CHM0004 fires (un-named-const structural cap) | — |
| CHM0004 if-threshold | `if (hp >= 50)` | CHM0004 fires (unchanged; advisory, cleanup story triages) | — |

</intent-contract>

## Code Map

- `godot/analyzers/ProjectChimera.Analyzers/BannedSimApiAnalyzer.cs` -- the analyzer; all four rule extensions land here.
- `godot/analyzers/ProjectChimera.Analyzers.Tests/BannedSimApiAnalyzerTests.cs` -- TDD suite; replace the vacuous CHM0003 negative, add positive/negative cases for every new form.
- `godot/analyzers/ProjectChimera.Analyzers.Tests/AnalyzerTestHarness.cs` -- in-process driver (references full framework via TPA; `System.Linq`/`Span`/`MemoryExtensions` resolve). No change needed.
- `.github/workflows/determinism-gate.yml` -- add the DW-8 guard comment at the `run_release_gate == 'true'` line.
- `godot/src/Core/ScenarioDirector.cs:483`, `godot/src/Core/Definitions/LocalProfileSource.cs:121` -- the two real total-order `List.Sort(Comparison)` sites that must stop firing CHM0003 (reference only; do not edit).

## Tasks & Acceptance

**Execution:**
- `BannedSimApiAnalyzer.cs` (CHM0002/DW-3) -- Recognize `Dictionary<,>.KeyCollection`/`ValueCollection` as unordered in `IsUnorderedCollection`; in `AnalyzeInvocation` add: (a) `GetEnumerator` and (b) `System.Linq.Enumerable` extension methods invoked on an unordered receiver → report CHM0002 on the receiver, excluding ordering operators (`OrderBy`, `OrderByDescending`, `Order`, `OrderDescending`). -- extend enumeration coverage beyond foreach.
- `BannedSimApiAnalyzer.cs` (CHM0001/DW-4) -- Register a `SyntaxKind.IdentifierName` handler with a cheap text prefilter (`"var"`/`"Single"`/`"Double"`): for contextual `var`, report if the inferred type is `System.Single`/`Double`; for `Single`/`Double` resolving to `System.Single`/`System.Double` as a type reference, report — skip when the identifier sits in a `MemberAccessExpressionSyntax` (RS0030/CHM0006 own member access). Update the CHM0001 XML-doc to state it now also covers `System.Single`/`Double` and `var`-inferred float. -- close the qualified/var gap the doc overclaimed.
- `BannedSimApiAnalyzer.cs` (CHM0003/DW-5) -- Add `System.MemoryExtensions` (Span sort) to the recognized Sort owners; before reporting, skip when the resolved `IMethodSymbol` has a parameter typed `IComparer`, `IComparer<T>`, or `Comparison<T>` (developer-controlled total order). -- cover Span sorts; stop over-flagging total-order-comparer sorts.
- `BannedSimApiAnalyzer.cs` (CHM0004/DW-6) -- In `AnalyzeNumericLiteral`/`IsCapContext`: (a) skip literals whose enclosing relational comparison is the controlling condition of a `for`/`while` loop; (b) handle a `PrefixUnaryExpressionSyntax` minus wrapping the literal so negated relational bounds fire with the negative value; (c) treat a numeric-literal initializer of a `static readonly` field as a cap. -- reduce loop-bound false-positives; cover static-readonly and negated caps.
- `BannedSimApiAnalyzerTests.cs` (DW-7) -- Replace `OrderBy_does_not_report_CHM0003` (vacuous) with meaningful cases: `Sort_with_comparer_does_not_report_CHM0003`, `SpanSort_reports_CHM0003`; add CHM0001 positives (`float?`, `List<float>`, tuple element, lambda param, `System.Single` field, `var` float) and the CHM0002/CHM0004 new-form cases from the matrix. -- pin every new/changed behavior.
- `determinism-gate.yml` (DW-8) -- Add a comment at the `run_release_gate == 'true'` step condition explaining that `workflow_dispatch` inputs are serialized as strings so the `'true'` string compare is correct and a `== true` "cleanup" would make the on-demand release proof permanently false/unreachable. -- guard the on-demand release gate.

**Acceptance Criteria:**
- Given the analyzer unit-test project, when `dotnet test` runs, then all tests pass including every new matrix case and no previously-passing test regresses.
- Given the two real `List.Sort(Comparison)` sites, when the analyzer runs over them, then CHM0003 no longer fires (verified by the `Sort_with_comparer_does_not_report_CHM0003` test standing in for them).
- Given a `== true` edit to the release-gate condition, when a reviewer reads the workflow, then the adjacent guard comment explains why it must remain the `'true'` string compare.

## Design Notes

- **Unordered receiver helper (CHM0002):** for an `InvocationExpressionSyntax` whose `Expression` is a `MemberAccessExpressionSyntax`, the receiver type is the semantic type of `memberAccess.Expression`. Report the diagnostic on that receiver node so the location points at the collection, matching the existing foreach report. KeyCollection/ValueCollection detection: `type.OriginalDefinition.MetadataName` is `"KeyCollection"`/`"ValueCollection"` and its `ContainingType.MetadataName` is `"Dictionary`2"`.
- **var handling (CHM0001):** an `IdentifierNameSyntax` with `IsVar == true`; use `GetTypeInfo(node).Type` for the inferred type; guard against null/error types. `float x = 1f;` continues to fire via the existing `PredefinedType` path — no double report because that path handles the keyword, not `var`.
- **CHM0003 comparer check:** inspect `method.Parameters` for any parameter whose `Type.OriginalDefinition` is `System.Collections.Generic.IComparer<T>` / `System.Collections.IComparer` / `System.Comparison<T>`. Both real sites are `List<T>.Sort(Comparison<T>)`, so they clear.

## Verification

**Commands:**
- `dotnet test godot/analyzers/ProjectChimera.Analyzers.Tests/ProjectChimera.Analyzers.Tests.csproj -c Release` -- expected: all tests pass (new + existing).
- `dotnet build godot/analyzers/ProjectChimera.Analyzers/ProjectChimera.Analyzers.csproj -c Release` -- expected: analyzer compiles clean.

**Manual checks:**
- `determinism-gate.yml` shows the DW-8 guard comment adjacent to the `run_release_gate == 'true'` condition.

## Review Triage Log

### 2026-07-19 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 9: (high 0, medium 0, low 9)
- defer: 0
- reject: 17: (high 0, medium 0, low 17)
- addressed_findings:
  - `[low]` `[patch]` CHM0002: order-insensitive LINQ reducers (`Count`/`LongCount`/`Any`/`All`/`Contains`/`Sum`/`Min`/`Max`/`Average`/`ToDictionary`/`ToHashSet`) no longer over-flag on Dictionary/HashSet — their result is order-independent (sim is int/Fixed). Added `IsOrderInsensitiveReducer` exclusion + `Linq_count_on_dictionary_does_not_report_CHM0002`.
  - `[low]` `[patch]` CHM0002: `KeyCollection`/`ValueCollection` detection now verifies the containing type's namespace is `System.Collections.Generic`, so a user type named `Dictionary\`2` (or the deterministic sorted/immutable dictionaries) cannot be mistaken for the BCL one.
  - `[low]` `[patch]` CHM0004: `do…while` loop-condition bounds are now exempt (parity with `for`/`while`) + `Do_while_loop_bound_does_not_report_CHM0004`.
  - `[low]` `[patch]` CHM0004: a negated cap is now reported at the `boundNode` location so the leading `-` is underlined and the squiggle matches the reported value.
  - `[low]` `[patch]` CHM0001: `nameof(Single)`/`nameof(Double)` no longer false-positive (nameof computes no float value) — added `IsInsideNameOf` guard + `Nameof_single_does_not_report_CHM0001`.
  - `[low]` `[patch]` DW-8: corrected the guard comment's mechanism — a `== true` mismatch is coerced to numbers by GitHub Actions (`"true"`→NaN, `true`→1), not a string-vs-boolean compare, so the conclusion (never satisfied) now rests on the accurate mechanism.
  - `[low]` `[patch]` Test: added `Analyzer_never_reports_AD0001` Theory over odd/error-typed inputs so a future crash in a new semantic path can't ship green.
  - `[low]` `[patch]` Test: `Var_inferred_int_does_not_report_CHM0001` pins the var path as float-only.
  - `[low]` `[patch]` Test: `Plain_static_field_...` and `Instance_readonly_field_...` negatives pin the static-readonly modifier conjunction.
  - Rejected (17, all low): reviewer claims that were false given the full file (CHM0001 keyword path is intact and still tested; `System.Single x;` is a `QualifiedName`, not member access, and has a passing test; `owner` is null-guarded; `dict.Where().First()` fires at `.Where`; `SortedDictionary.KeyCollection` is nested in `SortedDictionary\`2` ≠ `Dictionary\`2`); items excluded by the intent (the `Comparison<T>` total-order exemption is the DW-5-required behavior, instance-`readonly` caps, advisory severity, DW-8 input-rename durability); and obscure safe-direction advisory edges (cast-through `((IEnumerable)dict)`, `-(long)N` cast-wrapped negation, non-reduced/null-conditional LINQ, fully-inferred tuple-`var`, nullable-`var` float, ternary/`!` loop conditions).

## Auto Run Result

Status: done

**Summary:** Hardened the advisory `BannedSimApiAnalyzer` (Story 1.10b) coverage per DW-3..DW-8 — extended CHM0001/0002/0003/0004 and their tests, and added a DW-8 CI guard comment. All rules remain `DiagnosticSeverity.Warning`; no severity, release-gate, or RS0030-baseline change. Four adversarial review layers ran; findings triaged to 9 low-severity patches (applied) and 17 rejects.

**Files changed:**
- `godot/analyzers/ProjectChimera.Analyzers/BannedSimApiAnalyzer.cs` — CHM0001 (System.Single/Double + var-inferred float, member-access & nameof skipped), CHM0002 (KeyCollection/ValueCollection + GetEnumerator/LINQ on unordered receivers, ordering & order-insensitive operators exempt, namespace-verified), CHM0003 (Span sort + comparer-parameter total-order exemption), CHM0004 (for/while/do-while bound exemption, negated bounds, static-readonly caps).
- `godot/analyzers/ProjectChimera.Analyzers.Tests/BannedSimApiAnalyzerTests.cs` — 42→52 tests: new CHM0001/0002/0003/0004 forms, order-insensitive-LINQ & do-while & modifier-boundary negatives, and an AD0001 crash-guard Theory.
- `.github/workflows/determinism-gate.yml` — DW-8 guard comment (corrected coercion mechanism) at the `run_release_gate == 'true'` release-gate condition.

**Review findings breakdown:** 9 patches applied (all low), 0 deferred, 17 rejected (false-given-full-context, out-of-scope-per-intent, or obscure safe-direction advisory edges). No intent gaps, no bad-spec loopbacks.

**Verification performed:**
- `dotnet test .../ProjectChimera.Analyzers.Tests.csproj -c Release` → Passed 52/52.
- `dotnet build .../ProjectChimera.Analyzers.csproj -c Release` → clean.
- `dotnet build .../ProjectChimera.Sim.Analysis.csproj -c Release` (real sim codebase) → Build succeeded, no AD0001 analyzer crash; the two total-order sort sites (`ScenarioDirector.cs:483`, `LocalProfileSource.cs:121`) no longer fire CHM0003. Only pre-existing CS-nullable advisory warnings remain.
- Every I/O matrix row is covered by a test that ran and passed.

**Residual risks (low, advisory-only):** CHM0004 remains a heuristic (obscure condition shapes — ternary/`!`-wrapped loop conditions, cast-wrapped negation — can still mis-classify, unchanged severity). CHM0003 trusts any explicit comparer as total-order (the documented DW-5 tradeoff; a tie-less `Comparison<T>` is not proven deterministic but the golden replay backstops actual desync). A handful of obscure float-entry forms (fully-inferred tuple-`var`, nullable-`var`) remain uncaught false-negatives in the safe direction.
