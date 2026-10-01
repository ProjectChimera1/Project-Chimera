# R1: System.Text.Json and NativeAOT in the sim source set — inventory and migration recipe

Date 2026-10-01. Scope: UNDERSTAND phase for trial check (a) (sim as a NativeAOT library, same checksums as Godot). Nothing in the
repo was edited; all experiments ran on scratch copies under the session scratchpad (paths in Appendix B). Toolchain measured:
SDK 8.0.419 (`global.json:3`), runtime 8.0.25, ILCompiler 8.0.25 (NuGet cache), win-x64, STJ source generator from
`C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\8.0.25\analyzers\dotnet\cs\System.Text.Json.SourceGeneration.dll`.
Marks: **RAN** = executed here and observed; **UNVERIFIED** = not executed here.

## 0. Bottom line

1. The only thing in the sim set that blocks NativeAOT is reflection-based System.Text.Json: 65 call sites in 23 files (127 Roslyn
   diagnostics: 62 x IL2026 + 65 x IL3050). Zero other IL2xxx/IL3xxx from the Roslyn gate. **RAN** (`dotnet build ... --no-incremental`).
2. A second, silent AOT hazard the Roslyn gate does NOT see: `CanonicalFold.cs:178-179` (reflection member walk for unknown effect
   kinds). The real ILC compiler reports it (IL2075 x2), and under NativeAOT `GetFields()` on an instance type returned 0 members
   (JIT: 3), so that hash fallback would silently diverge from Godot. Dead for shipped content today. **RAN**.
3. I prototyped the whole migration on a scratch copy: one source-generated context + a 3-method facade (`AotJson`) + a closed
   strict-enum converter. Result: Roslyn IL warnings 127 -> 0; Tier-1 7,136 tests, no new failure (the same 5 environment failures
   as the unmodified copy plus 2 timing flakes that pass in isolation); `godot.csproj` still compiles. **RAN**.
4. Identity evidence: 265 reflection-vs-source-gen comparisons identical (values, exception types and messages, serialized bytes);
   `ProceduralMapGeneratorTests` golden `3387691366` reproduced under source-gen AND under NativeAOT; the migrated sim published with
   `PublishAot` (reflection disabled) printed output byte-identical to the unmodified JIT build for 22 golden scenario checksum
   sequences, 10 shipped scenarios (CanonicalModelHash + 200-tick checksum sequence + re-serialized bytes), `ContentHash`, loaders,
   item writer, trigger-graph JSON, profile and settings bytes. **RAN**.
5. No hash `AlgoVersion` bump is needed: every hash input I compared is identical. Recommendation: execute this recipe (section 7)
   before the native library project; it is roughly one focused session (25 existing files edited, 3 new source files, 4 test assertions updated, 1 new test file).
6. Baseline correction: HANDOFF.md:51 says "6392 / 0 / 1 skipped". Measured in the real repo today: 7,124 tests, 7,122 pass,
   1 fail, 1 skipped; the one failure is `ToolingGateGuardTests.BurnDownDispatcher_HasNoHardcodedTier1Baseline`
   (`.claude/workflows/dw-burndown.workflow.js` no longer exists). **RAN**.

## 1. The compiled sim source set (`godot/SimSources.props`, read in full)

33 `<Compile Include>` + 3 `<Compile Remove>`; 313 files, 78,702 physical lines (Core 42,364; Dsl 9,316; Multiplayer 8,381;
AI 4,696; Combat 4,041; Effects 4,016; Economy 3,442; Navigation 2,048; the rest UI/CreationSuite single files).

| Kind | Lines in props | Content |
|---|---|---|
| 8 folder globs | 22-25, 30, 36, 41, 146 | `src\Core`, `Combat`, `Economy`, `Navigation`, `AI`, `Effects`, `Dsl`, `Multiplayer\Server` (all `**\*.cs`) |
| 25 single files | 47-141 | Multiplayer: ReplayRecorder, ReplayPlayer, ReplayHeader, ReplayFormat, ReplayPerspectiveState, NetworkCommand, DelayMath, MergedArrivalRing, RejoinClient, LockstepPacer, LoopbackPeerSim, HandshakeGate, ReadyPacketRouting, LobbyVersionGate, LobbyHelloPolicy, ConcedeBuffer, Matchmaking\MatchmakerConfig, PlayerCountPolicy, LobbyReadyModel, Party\PartyState; UI: FactionPalette, MatchChatFormat, MatchChatCommands, TeamTintPolicy; CreationSuite: EditorHistory |
| 3 removals | 151, 155, 161 | `Core\MainScene.cs`, `Core\MainSceneDebugSeam.cs`, `Core\Bootstrap\Phases\**` (Godot-touching) |

Of the 313 files, 58 contain `using System.Text.Json` (23,667 lines). The set is already Godot-free; the analysis project proves it
compiles without Godot (`ProjectChimera.Sim.Analysis.csproj:22`).

AI cluster is separable (**RAN**): removing `AI/LLMService.cs`, `AI/BalanceSuggestionApplier.cs`, `AI/Providers/**` and
`AI/ScenarioTypeRegistry.cs` (`ScenarioTypeRegistry.cs:268,288` use `MapGeneratorContext`, declared at `LLMService.cs:49`) leaves a set that compiles with 0 errors
(scratch `exclai/ExclAi.csproj`). That drops 10 of the 65 STJ sites and the LLM `HttpClient` code. No other sim file references the
LLM types in code (the hits in `ContentJson`, `ScenarioSerializer`, `ScenarioValidator`, `UnitDefinitionValidator.cs:328`, `ProceduralMapGenerator` are comments).

## 2. System.Text.Json inventory

### 2.1 Every API in use, by AOT behaviour

| API | Sites | AOT status |
|---|---|---|
| `JsonSerializer.Deserialize<T>(string, options)` | 24 | IL2026+IL3050 (reflection overload) |
| `JsonSerializer.Serialize<T>(value[, options])` | 15 (3 are anonymous-type provider bodies; `FactionWriter.cs:329,339` pass no options) | IL2026+IL3050; anonymous types cannot be source-gen roots |
| `JsonSerializer.Serialize<T>(Utf8JsonWriter, value, options)` | 6 (re-entrant, inside converters) | IL2026+IL3050 |
| `JsonElement.Deserialize<T>(options)` (extension) | 6 (re-entrant, inside converters) | IL2026+IL3050 |
| `JsonArray.Add<T>(T)` | 11, all `FactionWriter.cs` | IL2026+IL3050; with a non-JsonNode T it builds a `JsonValueCustomized` that throws at write time without a resolver (**RAN**, below) |
| `new JsonStringEnumConverter(...)` | 3 sites (`ContentJson.cs:161,175`, `DamageTable.cs:120`) | IL3050 only; factory uses `MakeGenericType` |
| `[JsonConverter(typeof(JsonStringEnumConverter))]` on enums | 3 (`ScenarioData.cs:10,26,100`) | generator warning SYSLIB1034 (**RAN**) |
| `JsonDocument.Parse` / `ParseValue` | 10 / 5 | AOT-safe |
| `JsonNode.Parse`, `JsonObject`, `JsonArray` | 18 / 15 / 15 (FactionWriter, FactionDefinerWizardCore, NodeEditorAnnotation) | safe when values are primitives or JsonNode (casts), see 5.5 |
| `Utf8JsonReader` / `Utf8JsonWriter` in 7 custom converters | 7 / 20 | AOT-safe |
| `JsonElement`-typed DTO members | `CustomUiTree.cs:123`, `NodeBase.cs:32` (`JsonElement? Editor`) | supported natively by the generator (**RAN**) |

No `JsonSerializerContext`, `[JsonSerializable]`, `JsonTypeInfo`, `TypeInfoResolver` or `JsonSerializerIsReflectionEnabledByDefault`
exists anywhere under `godot/` (grep incl. tests and tools). The only hit is a comment, `FactionWriter.cs:788`. `[JsonPolymorphic]` and
`[JsonDerivedType]` are not used and are banned by RS0030 (`ProjectChimera.Sim.Analysis/BannedSymbols.txt:35`). Not used anywhere:
`JsonNamingPolicy`, `NumberHandling`, `IncludeFields`, custom encoders, `ReferenceHandler`, `MaxDepth`, `[JsonInclude]`, `[JsonRequired]`,
`[JsonExtensionData]`, `[JsonPropertyOrder]`. Attribute use: `[JsonPropertyName]` in 27 files, `[JsonIgnore]` in 14 (incl.
`Condition=WhenWritingNull/Default` on ScenarioData, ContentPackageManifest.cs:170, BuildingDefinition.cs:103), `[JsonConstructor]` on
`ExecEdge`/`DataEdge` (`GraphEdge.cs:99,162`, inert because converters are registered), `[JsonUnmappedMemberHandling(Disallow)]` on
`CustomUiTree.cs:40`.

### 2.2 The option sets (what the source-gen options must reproduce)

| Name (declared at) | Settings | Converters, in order | Read/written by |
|---|---|---|---|
| `ContentJson.Options` STRICT (`ContentJson.cs:84`, `BuildStrict` 154-165) | Skip comments, trailing commas, `UnmappedMemberHandling.Disallow` | name-only enum (161), `FixedJsonConverter` (162), `EffectNodeJsonConverter` (163) | `AbilityLoader.cs:32`, `ItemLoader.cs:27`, `LLMService.cs:1658` (`List<EffectNode>`) |
| `ContentJson.ScenarioOptions` (102, `BuildScenario` 171-179) | base + `WriteIndented` | name-only enum, Fixed, `WidgetBaseJsonConverter` | `ScenarioSerializer.cs:115` (read), `:238` (write; `.Replace("\r\n","\n")`) |
| `ContentJson.LenientOptions` = `FactionDefinition.JsonOptions` (118; `FactionDefinition.cs:278`) | base only | none | `FactionDefinition.cs:346,463`, `SkirmishCatalog.cs:195`, `FactionDefinerWizardCore.cs:157,158,332`, `BalanceSuggestionApplier.cs:81`, `LLMService.cs:1685,1721` |
| `ContentJson.ModelOutputOptions` (150, `BuildModelOutput` 184-191) | strict + `PropertyNameCaseInsensitive` (187) + `UnmappedMemberHandling.Skip` (189) | same as strict | `LLMService.cs:487,873,1503` |
| `ItemWriter.Options` (`ItemWriter.cs:30-39`) | strict + `WriteIndented` + `DefaultIgnoreCondition=WhenWritingDefault` | same as strict | `ItemWriter.cs:42` |
| `DslJson.Options` (`DslJson.cs:38-55`) | strict + `WriteIndented` | strict three + `NodeBaseJsonConverter`, `DataEdgeJsonConverter`, `ExecEdgeJsonConverter` appended | `TriggerGraph.cs:1062,1072` |
| `SettingsJson.Options` (`SettingsJson.cs:44-49`) | indented, Skip, trailing commas | none | `SettingsData.FromJson` (`SettingsData.cs:334`) |
| `ContentPackager._jsonOpts` (`ContentPackager.cs:30-35`) | indented, Skip, trailing commas | none | `ContentPackager.cs:324,439,593,668,729` |
| `LocalProfileSource.JsonOptions` (`LocalProfileSource.cs:28-33`) | indented, Skip, trailing commas | none | `LocalProfileSource.cs:55,123` |
| `DamageTable._opts` (`DamageTable.cs:116-121`) | Skip, trailing commas | non-generic `JsonStringEnumConverter()` (integers ALLOWED), Fixed | `DamageTable.cs:146`; `_docOpts` for `JsonDocument` (:126-130, :188) |
| `FactionWriter.HeroSerializeOptions` (`FactionWriter.cs:110-111`) | `DefaultIgnoreCondition=WhenWritingNull` | none | `FactionWriter.cs:301,318,412` |
| `FactionWriter`/Wizard `IndentedOptions` (`FactionWriter.cs:105`, `FactionDefinerWizardCore.cs:93`) | `WriteIndented` | none | only `JsonNode.ToJsonString` (AOT-safe) |
| default options (no argument) | defaults | none | `BehaviorRegistry.cs:89`, `FactionWriter.cs:329,338,339`, the 3 LLM providers |

Constraint carried over from memory note `chimera-dual-path-content-dto-constraint`: `UnitDefinition` rides the lenient posture (no enum
converter, no Fixed converter) and the strict ability posture; so DTOs keep string categories + `[JsonIgnore]`/getter-only `Parsed*`
accessors, `float` not `Fixed`, settable auto-props. This is a property of the OPTIONS, not of reflection, so it is unchanged by source
generation (section 5). Getter-only enum props such as `UnitDefinition.ParsedDamageType` (`UnitDefinition.cs:549-596,626`) are still
serialized (numbers under the lenient posture); the Wizard clone path (`FactionDefinerWizardCore.cs:157-158`) depends on that round trip.

### 2.3 Roots and the DTO graph each reaches (reflection walk of public properties, **RAN**; spike `Walker.cs`)

| Root | DTO types | enums | Converter-bound leaves |
|---|---|---|---|
| `UnitDefinition` | 8 (+CombatFeedbackProfile, FlashSpec, ShakeSpec, HeroDefinition, HeroAttributesDefinition, VeterancyDefinition, VeterancyRank) | 6 (via getter-only `Parsed*`) | none |
| `BuildingDefinition : UnitDefinition` (`BuildingDefinition.cs:16`) | 8 | 6 | none |
| `FactionDefinition` | 16 (Units, Buildings, Research...) | 6 | none |
| `ResearchDefinition` / `AttributeModelDefinition` | 3 / 3 | 0 | none |
| `AbilityDefinition` | 4 | 0 | `EffectNode` (11 kinds), `Fixed` |
| `ItemDefinition` | 1 | 0 | `EffectNode`, `Fixed`; `Dictionary<string,Fixed>` |
| `TriggerDefinition` | 4 | 0 | `Fixed` |
| `ScenarioData` | 26 | 7 (WinCondition, WinPresetKind, ObjectiveState, DslValueType, VarScope, + WidgetBase's AnchorPoint, WidgetKind) | `Fixed`, `WidgetBase` (9 kinds), `JsonElement` |
| `TriggerGraph.GraphJsonShape` (private nested, `TriggerGraph.cs:1086`) | 1 | 0 | `NodeBase` (28 kinds), `ExecEdge`, `DataEdge` |
| `DamageTable.Dto` (private nested, `DamageTable.cs:107`) | 1 (`Dictionary<DamageType,Dictionary<ArmorType,Fixed>>`) | 2 | `Fixed` |
| `PlayerProfile` | 2 | 0 | `ProfileInventoryItem` (`PlayerProfile.cs:30`); `ProfileAttributeValue` is a `readonly record struct` with `init` props |
| `ContentPackageManifest` | 2 (incl. `ProofOfPlayToken`) | 0 | none |
| `SettingsData`, `BehaviorDefinition`, `CombatFeedbackProfile`, `BalanceReport` | 1, 1, 3, 2 | 0 | none |

Union: 52 DTO classes (51 plus `WidgetBase`) and 13 enums from property types (incl. `AnchorPoint`, `WidgetKind` through `WidgetBase`), plus the enums that
the effect converter re-enters through the serializer (`PeriodicStackMode`, `StatusFlags`, `StackRule`, `TargetFilter`; `UnitTag` and `DamageType` are already counted). The generator emitted 140
type-info files for the final prototype context (~0.9 MB of generated C#), including primitives, arrays, lists and dictionaries.
Setters: all DTO props are public get/set except the `init` record structs and `[JsonIgnore]` private-set resolved indexes
(`UnitDefinition.cs:393-408`); no DTO uses fields, `object`, or `Dictionary<string,object>`.

### 2.4 Custom converters (hand-written closed registries; all AOT-clean except their re-entry into the serializer)

| Converter | Declared | Re-entrant reflection-API calls |
|---|---|---|
| `FixedJsonConverter` | `FixedJsonConverter.cs:24`; Write `:71-72` = `writer.WriteNumberValue(value.ToFloat())`; `ReadElement` `:46` | none |
| `EffectNodeJsonConverter` (11 kinds) | `EffectNodeJsonConverter.cs:38`; Read `:55-60` | Serialize `:245,251,258`; `JsonElement.Deserialize` `:461,489,496,516,537` (Fixed x4, enum, CombatFeedbackProfile) |
| `WidgetBaseJsonConverter` (9 kinds) | `WidgetBaseJsonConverter.cs:30` | Serialize `:117` (recursive child) |
| `NodeBaseJsonConverter` (28 kinds) | `NodeBaseJsonConverter.cs:37` | `:148` (embedded EffectNode), `:390`, `:964` (Fixed); `ReadFixed` `:852-858` already calls `FixedJsonConverter.ReadElement` directly |
| `DataEdgeJsonConverter`, `ExecEdgeJsonConverter` | `GraphEdge.cs:220,311` | none |
| `ProfileInventoryItemJsonConverter` | `PlayerProfile.cs:40-77` (internal) | none |

## 3. Other reflection, dynamic code and AOT-sensitive behaviour in the set

| Item | Where | Verdict |
|---|---|---|
| Reflection member walk | `CanonicalFold.cs:3,175-203` (`GetFields`, `GetProperties`, `FieldInfo.GetValue`) in `MixUnknownEffect` (DW-449 fail-closed fold for a kind with no explicit arm) | **Real behaviour change under AOT**: ILC IL2075 x2 (`:178`,`:179`); **RAN**: `GetType().GetFields()` on a `DamageEffect` instance returned 0 members under NativeAOT (3 under JIT), so the fold would hash `n=0` and not throw; a statically named `typeof(DamageEffect).GetFields()` returned 3, so the result depends on what the compiler happens to root. The Roslyn gate does not report it. Dead for the 11 shipped kinds; guarded only by JIT tests (`EffectFoldCompletenessTests.cs:182-236` probe classes). |
| `GetType().Name` | `CanonicalModelHash.cs:837,928` (unreachable hash default arms), `EffectNodeJsonConverter.cs:186`, `WidgetBaseJsonConverter.cs:106`, `NodeBaseJsonConverter.cs:340`, `NodeBase.cs:786`, `AbilityDraft.cs:362` | Works under AOT for constructed types (**RAN**: `DirectHpDeltaEffect`). |
| Enum text | `.ToString()` in hash folds (CanonicalFold, CanonicalModelHash, ContentHash); `Enum.TryParse<T>`/`Enum.IsDefined`/`Enum.Parse<T>` (`ScenarioApplier.cs:299`, `WidgetBaseJsonConverter.cs:298-415`, `GraphEdge.cs:250`, `CustomEventAuthoringGate.cs:86`); `Enum.GetNames(typeof(...))` (`ScenarioValidator.cs:63`, `ScenarioDirector.cs:301`, `UnitCategory.cs:48`, `DamageTable.cs:213,225`) | Identical under AOT incl. `[Flags]` combos ("Stunned, Rooted"), undefined values ("3"), ignoreCase parse (**RAN**). |
| `Activator`, `Type.GetType`, `MakeGenericType`, `Expression.*`, `dynamic`, `Reflection.Emit`, assembly scanning, attributes read by reflection | none (grep, incl. `-a` for the one file grep calls binary) | n/a |
| `typeof(...)` data table | `SkirmishSetupToScenario.cs:573-577` | list only, read by a test |
| `Regex` with `RegexOptions.Compiled` | `FileSecretStore.cs:31`; `ContentPackager.cs:902` | works under AOT (interpreted) (**RAN**) |
| `ZipFile`, `HttpClient`, `stackalloc`, `[ThreadStatic]` | `ContentPackager.cs`, LLM + `NakamaTokenVerifier.cs`, `EffectNodeJsonConverter.cs:586`, `ContentPackager.cs:88,98` | work (**RAN** for Zip/Http ctor/stackalloc) |
| Culture | `ServerBootstrap.cs:46-51` sets invariant; no culture-sensitive comparers found in sim logic | AOT build with `InvariantGlobalization` equals JIT with ICU (**RAN**, same outputs) |
| Float | `Math.Sqrt`/`Cos`/`Sin` only in `LLMService.cs` (excluded in native); `MathF.Sqrt` `FogOfWarSystem.cs:201` (IEEE exact) | 22 golden sequences identical under AOT (**RAN**) |

## 4. Analyzer results (**RAN**)

`dotnet build godot/ProjectChimera.Sim.Analysis/ProjectChimera.Sim.Analysis.csproj --no-incremental`: 859 unique warnings, 0 errors.

| Rule | Count | Cause |
|---|---|---|
| IL2026 | 62 | all STJ (`Deserialize` 24+6, `Serialize` 15+6, `JsonArray.Add<T>` 11) |
| IL3050 | 65 | the same 62 plus 3 `JsonStringEnumConverter` constructors |
| other IL2xxx/IL3xxx | 0 | none |
| CHM0001 394, CHM0005 280, CHM0004 35, CHM0002 10, CHM0006 3, CHM0003 1 | 723 | custom determinism analyzer, advisory (float debt etc.); unrelated to AOT |
| CS8604 4, CS8618 1, CS8625 2, CS8765 2 | 9 | nullable |

Counts by area (65 sites): Core/Definitions 47 (FactionWriter 17, EffectNodeJsonConverter 8, ContentPackager 5, FactionDefinerWizardCore 3, ...),
AI 10, Dsl 5, Combat 2, Core/Skirmish 1. Sites on a native sim host's runtime path (scenario, faction, ability, item, behavior,
damage-table loads, trigger-graph parse, the three converters, strict-enum constructors): about 26; the other ~39 are authoring, UGC,
LLM and settings code that still must be migrated to keep the gate at zero.

Per-site list (file: line, shape of call; every line is IL2026+IL3050 unless marked "3050"):
- AI: BalanceSuggestionApplier 81 `Deserialize<UnitDefinition>`; LLMService 487 `<TriggerDefinition>`, 873 `<ScenarioData>`, 1503 `<BalanceReport>`, 1658 `<List<EffectNode>>`, 1685 `<FactionDefinition>`, 1721 `<UnitDefinition>`; Anthropic 50, Ollama 47, OpenRouter 49 `Serialize(anonymous)`.
- Combat/DamageTable 120 (3050, enum ctor), 146 `<Dto>`.
- Core/Definitions: AbilityLoader 32; BehaviorRegistry 89; ContentJson 161, 175 (3050); ContentPackager 324, 439, 593, 668, 729; EffectNodeJsonConverter 245, 251, 258, 461, 489, 496, 516, 537; FactionDefinerWizardCore 157, 158, 332; FactionDefinition 346, 463; FactionWriter 137, 172, 301, 318, 329, 338, 339, 381, 387, 412, 442, 477, 581, 587, 636, 642, 718; ItemLoader 27; ItemWriter 42; LocalProfileSource 55, 123; ScenarioSerializer 115, 238; SettingsData 334; WidgetBaseJsonConverter 117.
- Core/Skirmish/SkirmishCatalog 195; Dsl/NodeBaseJsonConverter 148, 390, 964; Dsl/TriggerGraph 1062, 1072.

**The Roslyn gate is not the whole gate.** `dotnet publish -p:PublishAot=true` (ILC, **RAN**) adds IL2075 at `CanonicalFold.cs:178,179`, which Roslyn
never emitted. Before the migration, ILC on a driver reaching the converters reported 16 IL3050 + 12 IL2026 + the 2 IL2075; after the
migration ILC on a driver that exercises every loader reports only the 2 IL2075.

## 5. What changes under source generation

All claims below are from the prototype (section 8) unless stated.

1. **Postures stay as options; the context only supplies metadata.** A generated `JsonSerializerContext` used as
   `options.TypeInfoResolver` serves every posture: `Disallow`, case-insensitivity, comment/trailing-comma handling, `WriteIndented`,
   `DefaultIgnoreCondition` and registered converters all flow from the options object at type-info creation (diff cases for
   `cooldwn`, `ParsedTargeting`, `ID`, `"damage_type": 3`, `WhenWritingDefault` all identical). Runtime custom converters win over
   generated object metadata (every generated `Create_<T>(JsonSerializerOptions options)` first calls `TryGetTypeInfoForRuntimeCustomConverter`, which is also why
   one context can serve many options objects), which is what lets `Fixed`, `EffectNode`,
   `WidgetBase`, `NodeBase` stay hand-written.
2. **Use metadata mode** (`GenerationMode = Metadata`). The generated fast-path serializer is designed around the context's own attribute options
   (documented STJ behaviour, not exercised here: I used metadata mode throughout); metadata mode makes behaviour a function of the posture options exactly like reflection.
3. **Never call the typed shortcuts** (`ChimeraJsonContext.Default.UnitDefinition`): they use the context's default options, not the
   posture. All calls go through the facade (7.3).
4. **Enum converter swap.** `JsonStringEnumConverter(null, allowIntegerValues:false)` becomes per-enum `JsonStringEnumConverter<T>`
   subclasses (same underlying `EnumConverter<T>`). Differential on name case, numeric, numeric string, flags, bad name, and
   enum-keyed dictionary keys ("light", "1", "Normal, Pierce") all identical. A missing enum would silently become a lenient
   numeric enum on a strict posture, so use one factory with a CLOSED registry that throws for an unlisted enum (7.4). Type-level
   `[JsonConverter(typeof(JsonStringEnumConverter))]` is replaced by `JsonStringEnumConverter<T>` (same allow-integers behaviour);
   do NOT put a strict converter on enum types: by STJ's converter precedence (inference, not run) it would apply on the lenient posture and change its `Parsed*` getter-only output from numbers to names.
5. **JsonNode DOM** (FactionWriter, wizard, NodeEditorAnnotation). Primitives and `JsonNode`/`JsonElement`-backed nodes serialize with
   resolver-less indented options and reflection disabled (**RAN**). `JsonArray.Add(obj)` with a `JsonObject`-typed argument binds the
   generic `Add<T>` (analyzer warning only, works), but `Add<string>("s")` fails without a resolver (`NotSupportedException`).
   Fix = cast to `JsonNode` (the code comment at `FactionWriter.cs:785-791` already states this rule for strings). `JsonValue.Create<float[]>`
   likewise fails; the sim code does not use it.
6. **Default-options and anonymous bodies.** With reflection disabled, any `JsonSerializer.Serialize(x)` with no options throws
   (`InvalidOperationException: Reflection-based serialization has been disabled`, **RAN**). Affects `BehaviorRegistry.cs:89`,
   `FactionWriter.cs:329,338,339`, and the three LLM providers (anonymous types). Fix = named DTOs / a default options bound to the
   context. The named LLM bodies reproduce the legacy anonymous-type bytes exactly, including escaping of `& < > ' "` and non-ASCII
   (test in the prototype).
7. **Accessibility.** The generator cannot reference private nested types: `TriggerGraph.GraphJsonShape` (`:1086`) and `DamageTable.Dto` (`:107`)
   must become `internal`. Context is `internal partial`; every assembly that compiles the set (godot.csproj, Tier-1, analysis, native)
   gets its own copy.
8. **Constructors, setters, defaults.** The generator accepted every root with no diagnostics (DTOs are classes with public parameterless constructors, spot-checked; the two record structs have an implicit default constructor); property initializers still apply; `init`
   record structs, `[JsonConstructor]` edges and private-set `[JsonIgnore]` members compiled with no generator diagnostics. Missing-member
   handling is identical (the "extra", "null array", "dup key", "wrong type", "NaN" cases all matched). Inheritance property order
   (`BuildingDefinition : UnitDefinition`) matches reflection byte for byte.
9. **Options without a resolver.** `JsonSerializerOptions.GetTypeInfo(Type)` on a plain `new JsonSerializerOptions()` throws even under
   JIT (**RAN**: `FogSharedVisionTests.SettingsPreference_PushedOntoLiveHostFog_TogglesAlliedUnion` passes its own options to
   `SettingsData.FromJson`, `FogSharedVisionTests.cs:113,134`; `DedicatedServer.cs:341-346` does the same in Godot). The facade needs a
   reflection fallback gated on `JsonSerializer.IsReflectionEnabledByDefault` (dead under AOT).
10. **Error behaviour.** Located `JsonException` messages are identical (same "could not be mapped to any .NET member", same
    "Path: $ | LineNumber: 0 | BytePositionInLine: 1" tails), so `AbilityLoader.cs:43`'s tail-stripping and every located-error test are unaffected. New failure mode:
    a type missing from the context throws `NotSupportedException`; several callers catch broadly and would swallow it
    (`FactionDefinition.cs:465` catch-all -> "excluded faction"; `ScenarioValidator.cs:747,765` catches `NotSupportedException`).
    A coverage test is mandatory (7.7).

### What could change OUTPUT, and what I measured

| Output | Pinned by | Result |
|---|---|---|
| `ScenarioSerializer.Serialize` bytes (indented, LF-normalized, stamped) | `ProceduralMapGeneratorTests.cs:56` `GoldenHash = 3387691366u`; `ScenarioSerializer.cs:238` | identical under source-gen and under NativeAOT (hash 3387691366) |
| `ItemWriter`, `FactionWriter`, `SettingsData`, `ContentPackageManifest`, `PlayerProfile` list, `HeroDefinition`/`VeterancyDefinition`/attribute-model (`WhenWritingNull`), DSL canonical graph JSON (`TriggerGraph.cs:1062`, not a hash source per DW-337) | `ItemWriterBytes_StayIndented...` (`ContentJsonDerivedPostureTests.cs:78`), `FactionWriteRoundTripTests` (50 refs), `ContentPackager*Tests` | identical bytes (31 + 4 comparisons) |
| `CanonicalModelHash`, `ContentHash`, `StartStateHash`, `SimChecksum` | typed folds over `Fixed.Raw`/ints/enum names, not serializer bytes | identical, JIT vs AOT, for 10 shipped scenarios, 13 abilities, factions, 22 golden scenarios |
| Deserialized object graphs | corpus below | identical (dump compares every public property incl. getter-only, floats by bit pattern, Fixed by Raw) |

The only differences found were the intended ones: converter-list assertions in 4 tests, and the CanonicalFold reflection fallback under AOT.

## 6. Non-sim Godot code that touches the same DTOs or serializer (kept compiling and behaving)

`godot.csproj` compiles the whole `src/` tree (it removes only tests/analyzers, `godot.csproj:16,24-25`). Non-sim callers:

| File:line | Call | After the recipe |
|---|---|---|
| `CreationSuite/AbilityEditorPanel.cs:114` | `new JsonSerializerOptions(ContentJson.Options){WriteIndented=true}`; `Serialize` at `:424,757,851,924` | copy ctor keeps the resolver and converters (**RAN**); `AbilityDefinition` is a context root; unchanged source |
| `CreationSuite/{Building,Unit,Research,Item}Card*.cs` (`BuildingCardPanel.Edit.cs:961`, `UnitCardPanel.Edit.cs:910,1386`, `ResearchCardPanel.cs:1012`, `ItemCardPanel.cs:184`, `ItemCardPanel.Edit.cs:179,199,309`) | `Deserialize<T>(…, FactionDefinition.JsonOptions / ContentJson.Options)` | resolver-bound options; every T is a context root; unchanged source |
| `CreationSuite/TriggerEditorPanel.cs:619-620` | inline `new JsonSerializerOptions{WriteIndented, Converters={Fixed}}` | no resolver: reflection (JIT) as today; works because Godot is JIT |
| `CreationSuite/TriggerEditorPanel.cs:949` | `Deserialize<EffectNode>(…, DslJson.Options)` | `EffectNode` is a root; unchanged |
| `Core/MainSceneDebugSeam.cs:528-531` | `JsonDocument` + `Deserialize<AttributeModelDefinition>(…, JsonOptions)` | unchanged |
| `UI/SettingsManager.cs:42,93` | `Serialize(Current, SettingsJson.Options)` | `SettingsData` is a root; unchanged |
| `Multiplayer/DedicatedServer.cs:341-346` | `SettingsData.FromJson(text, ownOptions)` | facade reflection fallback (item 9 above) |
| `Multiplayer/NakamaService.cs:280,304,332` | default-options `Serialize/Deserialize<PlayerProfile>` and a string dictionary | untouched reflection |
| `UGC/ModIoService.cs`, `UGC/ProofOfPlayStore.cs:26-58` | own options, own DTOs (`ModIoListResponse`, `ProofOfPlayToken`) | untouched reflection |

Compile check **RAN**: `dotnet build godot/godot.csproj --no-incremental` on the prototype copy: 0 errors (Godot.NET.Sdk 4.6.3,
GodotSharp from the NuGet cache). The in-engine behaviour was NOT run (UNVERIFIED); nothing in Godot needs a code change, so the
risk is the shared option objects, covered by the Tier-1 suite. `DependencyHygieneTests` (godot.csproj carries exactly one package,
`godot.csproj:11`) stays green because the generator ships in the SDK reference pack, not a package.

## 7. The recipe

### 7.1 One context, many postures (recommended) vs alternatives
- **One** `internal partial class ChimeraJsonContext : JsonSerializerContext` (new `src/Core/Definitions/ChimeraJsonContext.cs`),
  `[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]`, used as `TypeInfoResolver` of every posture.
- Rejected: one context per posture (metadata duplicated 7x; `[JsonSourceGenerationOptions]` cannot express `JsonStringEnumConverter(null,false)`
  or case-insensitivity per use, and each posture already differs only by options); reflection-with-fallback `Combine(ctx, Default)`
  as the shipped behaviour (hides a missing root in Godot; keep reflection only in the facade fallback and in the test oracle).

### 7.2 `[JsonSerializable]` roots (the prototype's list; 38 attributes)
Content: `UnitDefinition`, `BuildingDefinition`, `FactionDefinition`, `ResearchDefinition`, `HeroDefinition`, `VeterancyDefinition`,
`AttributeModelDefinition`, `CombatFeedbackProfile`, `AbilityDefinition`, `ItemDefinition`, `TriggerDefinition`, `ScenarioData`,
`BehaviorDefinition`, `ContentPackageManifest`, `PlayerProfile`, `List<PlayerProfile>`, `SettingsData`, `ProofOfPlayToken`, `CustomUiTree`,
`DamageTable.Dto` (make internal), `TriggerGraph.GraphJsonShape` (make internal). Converter-handled: `EffectNode`, `List<EffectNode>`, `NodeBase`,
`DataEdge`, `ExecEdge`, `WidgetBase`, `Fixed`. Enums the converters re-enter for: `UnitTag`, `PeriodicStackMode`, `StatusFlags`, `StackRule`,
`TargetFilter`, `DamageType`. LLM: `BalanceReport`, `AnthropicRequestBody`, `ChatRequestBody`, `OllamaRequestBody` (new internal DTOs, 7.5).
Full text: `scratchpad/trial2/godot/src/Core/Definitions/ChimeraJsonContext.cs`.

### 7.3 Options and the facade (new `AotJson.cs`, public static, namespace `ProjectChimera.Core.Definitions`)
```csharp
public static class AotJson {
  public static readonly JsonSerializerOptions DefaultOptions = new() { TypeInfoResolver = ChimeraJsonContext.Default };
  static JsonTypeInfo<T> Info<T>(JsonSerializerOptions o) => (JsonTypeInfo<T>)o.GetTypeInfo(typeof(T));
  public static T?     Deserialize<T>(string json)                        => JsonSerializer.Deserialize(json, Info<T>(DefaultOptions));
  public static T?     Deserialize<T>(string json, JsonSerializerOptions o) => o.TypeInfoResolver is not null ? JsonSerializer.Deserialize(json, Info<T>(o)) : Reflection.Deserialize<T>(json, o);
  public static T?     Deserialize<T>(JsonElement e, JsonSerializerOptions o)   // replaces el.Deserialize<T>(options)
  public static string Serialize<T>(T v) / Serialize<T>(T v, JsonSerializerOptions o)
  public static void   Serialize<T>(Utf8JsonWriter w, T v, JsonSerializerOptions o)
  // Reflection.* = the old overloads with [UnconditionalSuppressMessage IL2026/IL3050], guarded by JsonSerializer.IsReflectionEnabledByDefault
}
```
Posture changes in `ContentJson.cs`: add `TypeInfoResolver = ChimeraJsonContext.Default` in `Base()` (`:59-63`, so strict, scenario, lenient,
model-output, `NewBase/NewStrict` derivations, ItemWriter and DslJson inherit it); replace both enum lines (161, 175) with
`AotJson.AddStrictEnums(o)`; leave `Fixed`/`EffectNode`/`Widget`/`Node`/`Edge` converters exactly where they are (order is behaviour).
Standalone option objects: add `TypeInfoResolver = ChimeraJsonContext.Default` to `SettingsJson.Options`, `ContentPackager._jsonOpts`,
`LocalProfileSource.JsonOptions`, `FactionWriter.HeroSerializeOptions`; `DamageTable._opts` gets `JsonStringEnumConverter<DamageType>`,
`<ArmorType>` (default, integers allowed, as today) + the resolver. `IndentedOptions` (JsonNode only) stay as they are.

### 7.4 Strict enum factory
`AotJson.StrictEnumConverterFactory : JsonConverterFactory`: `CanConvert = IsEnum`; a static `Dictionary<Type, JsonConverterFactory>` of
`new StrictEnum<T>()` where `StrictEnum<T> : JsonStringEnumConverter<T>` with `base(null, allowIntegerValues:false)`; `CreateConverter`
delegates to the closed instance (so STJ builds its per-options `EnumConverter<T>`; returning the factory object itself is invalid) and
throws `NotSupportedException` for an unlisted enum. Registry in the prototype (19): WinCondition, WinPresetKind, ObjectiveState,
DslValueType, VarScope, UnitTag, PeriodicStackMode, StatusFlags, StackRule, TargetFilter, DamageType, ArmorType, AttackDomain,
SeparationPriority, UnitCategory, AnchorPoint, WidgetKind, LocalUiAction, DataWireType. (Starting with 15 enums, 74 LLM tests newly failed, loudly:
`AnchorPoint`/`WidgetKind` are reachable from `ScenarioData` through `WidgetBase` metadata under the model-output posture, which has no widget converter.)

### 7.5 Call-site changes (all mechanical; script `trial2/patch_trial.py`)
| Old | New |
|---|---|
| `JsonSerializer.Deserialize<T>(s, o)` (24 sites) | `AotJson.Deserialize<T>(s, o)` |
| `JsonSerializer.Serialize(v, o)` (15) / `(writer, v, o)` (6) | `AotJson.Serialize(v, o)` / `(writer, v, o)` |
| `el.Deserialize<T>(options)` (6) | `AotJson.Deserialize<T>(el, options)` (or call `FixedJsonConverter.ReadElement(el)` directly for Fixed, as `NodeBaseJsonConverter.cs:852` already does) |
| no-options calls (`BehaviorRegistry.cs:89`, `FactionWriter.cs:329,338,339`) | `AotJson.Deserialize<T>(s)` / `Serialize(v)` |
| 3 provider anonymous bodies (`AnthropicProvider.cs:39-45`, `OpenRouterProvider.cs:39-48`, `OllamaProvider.cs:35-45`) | internal DTOs `AnthropicRequestBody`/`ChatRequestBody`/`OllamaRequestBody` + `LlmMessageBody` with `[JsonPropertyName]` in declaration order (the `System` property needs `global::System.Array.Empty`) |
| `JsonArray.Add(x)` x11 in `FactionWriter.cs` | `Add((JsonNode)x)` |
| `private sealed class Dto` / `GraphJsonShape` | `internal sealed class` |
| `[JsonConverter(typeof(JsonStringEnumConverter))]` x3 (`ScenarioData.cs:10,26,100`) | `[JsonConverter(typeof(JsonStringEnumConverter<WinCondition>))]` etc. |

Preserve CRLF and BOM when editing (every file I checked is CRLF; the patch script round-trips both).

### 7.6 Tests to change
`ContentJsonDerivedPostureTests.cs:98,169` and `ContentJsonUnificationTests.cs:198,211` assert `typeof(JsonStringEnumConverter)` in the
converter list; change to `typeof(AotJson.StrictEnumConverterFactory)` (4 assertions, shown failing then passing in the prototype).

### 7.7 Permanent guards (new `AotJsonMigrationGuardTests.cs`, 13 tests, all passing in the prototype)
1. strict-enum registry covers every enum reachable from the context roots (reflection walk, incl. widget subtypes);
2. every posture (7) resolves every root (`GetTypeInfo`), so a missing `[JsonSerializable]` fails here, not as a swallowed `NotSupportedException`;
3. no `JsonSerializer.(De)Serialize` outside `AotJson.cs` in the sim folders (Godot-only folders exempt);
4. the **reflection oracle**: the same postures rebuilt by reflection (no resolver) vs the shipped ones, over every shipped ability/item/faction/scenario,
   7 generated maps, 46 hostile/edge inputs (about 106 comparisons), the DSL graph, and the LLM body bytes: values, exception text, bytes.

### 7.8 Gates after the migration
Keep the Roslyn analyzer (now 0 IL warnings; make IL2026/IL3050 release-gated in `ProjectChimera.Sim.Analysis.csproj`, whose comment at `:15-17` still calls them
advisory) AND add an ILC gate: `dotnet publish -p:PublishAot=true` of the native smoke project (section 8, item 5) with IL warnings as errors; Roslyn cannot see IL2075.
Operational note (**RAN**): the link step failed (`vswhere.exe is not recognized`, link exit 123) until
`C:\Program Files (x86)\Microsoft Visual Studio\Installer` was on PATH; ILCompiler 8.0.25 was already in the NuGet cache. R2 reports the same.

### 7.9 Non-STJ follow-up: `CanonicalFold.MixUnknownEffect`
Pick one: (a) make the `default` arm of `MixEffect` throw `NotSupportedException` (fail closed, no reflection; the DW-449 test probes then assert the
throw), or (b) give `EffectNode` an explicit fold member and delete the reflection. Do not leave it: under NativeAOT it hashes zero members instead of failing.

## 8. How identity was proven (everything **RAN**; evidence in Appendix B)

1. **Differential spike** (`scratchpad/spike`, modes `diff`, `edge2`, `controls`, `norefl`, `walk`): shipped content through reflection options vs context-resolved options, comparing
   a full reflective dump + serialized bytes: 234 comparisons (111 shipped-content: 13 abilities x3, 2 items x2, 4 faction files x2, 10 scenarios + 7 generated maps x3, 7 attribute models, 2 behaviors; plus 123 hostile/edge cases)
   + 31 more (DSL graph with all three converters, profiles, settings, manifest, hero/veterancy/attribute-model, default-options feedback, LLM bodies) = 265, 0 differences.
   Controls: a deliberately lenient enum converter produced the expected diff (harness has teeth); an unregistered type threw
   `NotSupportedException` (no hidden reflection fallback); `typeinfoKind=Object props=52` for `UnitDefinition` (source-gen metadata is live).
2. **Reflection disabled** (`AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false)`): SG paths work incl. converter re-entry;
   `JsonArray.Add<string>`, anonymous `Serialize`, `Serialize(5)` and `Deserialize<UnitDefinition>(lenient)` throw, as predicted.
3. **Tier-1 on the migrated copy**: last full run 7,136 tests (7,124 + 12 new guard tests; the 13th guard, LLM body bytes, passed in a filtered run): no new failure vs the unmodified copy. The unmodified copy fails 7 (the copy lacks the `.glb` meshes, the Terrain3D native libraries and `.git`; the workflow file is gone; plus 2 LLM timing tests). Real repo, unmodified: 1 failure (the removed workflow). The 2 flakes (`LlmServiceLifecycleTests`, `LlmSupersededDraftCallbackTests`,
   `LlmServiceRepointTests`, `BalanceAnalysisGenerationTests`: "generation callback did not fire within the timeout", a different pair fails on each full run, also in the unmodified copy) pass in every isolated rerun (5 reruns on the migrated copy).
4. **Roslyn gate** on the migrated copy: 859 -> 732 warnings; IL2026/IL3050 62/65 -> 0/0; CHM counts unchanged (394/280/35/10/3/1).
5. **NativeAOT smoke** (`scratchpad/spikeaot2`, win-x64, `PublishAot`, reflection off, 6.35 MB): Output compared with the UNMODIFIED sources built as a
   JIT exe (`orig/`, `ORIG` define using reflection): 60 lines identical (only the `runtime:` banner differs): 22 golden scenario checksum sequences
   (300 ticks each), `ContentHash(all)` = 0x55BB83D5F0A14C4B, 13 ability loads, 10 shipped scenarios (canonical hash, re-serialize hash, 200-tick sequence),
   `ProceduralMapGenerator` golden, trigger-graph canonical JSON, faction writer bytes, profile and settings bytes, error messages for bad enum / unknown key.
   Also identical vs JIT with ICU (`InvariantGlobalization=false`). A first AOT build on the UNMIGRATED sources, with the JSON wired by hand through
   the context, was identical too (22 sequences, 10 scenario hashes, 13 ability hashes) apart from the CanonicalFold reflection probe; I did not publish the
   unmigrated loaders themselves (R2 reports they die at runtime, `r2-sim-entry-checksum-scenario.md:31-34`).

## 9. Risks, UNVERIFIED, open questions

Risks
- Context drift: a new DTO or enum not added to the context/registry. Mitigation: guards 7.7 (1)-(2); the factory throws on first use.
- Posture bypass via `ChimeraJsonContext.Default.X`: add a grep guard or keep the context `internal`.
- Broad catches (`FactionDefinition.cs:465`, `ScenarioValidator.cs:747,765`, `LLMService`) would convert a missing-metadata `NotSupportedException` into a content error; guard 7.7 (2) is the control.
- `JsonSerializerOptions` becomes read-only on first `GetTypeInfo`; `NewStrict()` callers mutate before use today, keep that order.
- Generator/runtime pairing: the generator comes from the 8.0.25 reference pack, the runtime is 8.0.25 here; a Godot machine with an older 8.0.x runtime is UNVERIFIED.
- The two silent-divergence hazards remain until 7.9: `MixUnknownEffect` under AOT, and any future `GetType()` reflection in hash code.

UNVERIFIED
- A NativeAOT SHARED LIBRARY (`NativeLib=Shared`, `UnmanagedCallersOnly` exports) and the 1,000-unit run inside Unreal (R2/R3 scope); my AOT runs were console exes.
- In-engine Godot run with the migrated options (compile and Tier-1 only).
- `GetType().Name` on a type never statically referenced under AOT; reflection on `Enum` of types the compiler never saw.
- Whether Alec's CI restores in locked mode without change (no package was added, so it should).

Open questions (decisions for the design phase)
1. Exclude the AI/LLM cluster from the native library (recommended: it compiles away cleanly and removes `HttpClient`) or migrate it too and keep one set (the prototype migrated it).
2. `MixUnknownEffect`: fail-closed throw (a) or explicit fold member (b).
3. Keep the reflection oracle options in the Tier-1 project permanently (recommended) or retire it once the AOT smoke gate exists.
4. Update HANDOFF.md:51 baseline to 7,124 tests (and decide whether to repair or delete `ToolingGateGuardTests.BurnDownDispatcher_HasNoHardcodedTier1Baseline`).

## Appendix A. Reproduction commands

- Roslyn: `dotnet build godot/ProjectChimera.Sim.Analysis/ProjectChimera.Sim.Analysis.csproj --no-incremental` (parse with `scratchpad/parse_warn.py`).
- Differential: `dotnet bin/Debug/net8.0/Spike.dll diff | controls | norefl | edge2 | walk` in `scratchpad/spike`.
- AOT: add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to PATH, then `dotnet publish -c Release -r win-x64 -p:AotMode=true -o publish_aot` in `scratchpad/spikeaot2`; run `publish_aot/SpikeAot2.exe`, compare with `orig_out.txt`.
- Tier-1: `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` inside `scratchpad/trial2` (about 2 minutes).

## Appendix B. Evidence artefacts (scratch, not in the repo)

Base `C:\Users\MD_Ki\AppData\Local\Temp\claude\D--Projects-Project-Chimera\6de76155-94ab-440a-84fd-59aa5580f954\scratchpad\`:
`trial2\` (prototype copy, patched), `r1_trial_migration_src.diff` (1,002 lines, 28 files incl. 3 new) and `r1_trial_migration_tests.diff` (355 lines),
`trial2\patch_trial.py`, `spike\` (differential + walker), `spikeaot2\` (+`orig\`) and `spikeaot\` (AOT smoke), `exclai\` (AI exclusion),
`analysis_build.txt` / `warn_out.txt` / `il_table.txt` (Roslyn run and 65-site table), `trial_baseline.txt`, `trial2_tests_*.txt`, `real_baseline.txt`.
