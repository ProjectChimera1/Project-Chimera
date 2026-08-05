#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;       // TriggerGraph — the scenario run_effect embed channel
using ProjectChimera.Effects;
using ProjectChimera.Navigation; // SpatialHash — the cost the cap actually bounds
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-534 regression guard — the authored SearchArea RADIUS ceiling
    /// (<see cref="EffectCaps.MaxSearchRadius"/>), enforced at load by <see cref="EffectBounds"/>.
    ///
    /// <para><b>The defect.</b> <c>SpatialHash.QueryRadiusLowestIds</c> deliberately drops the
    /// <c>count &lt; maxResults</c> scan bound its unfiltered sibling keeps: a full result buffer must NOT end the
    /// scan, because a later candidate may still carry a lower id, and that exit-free walk is exactly what makes
    /// the selection GLOBALLY lowest-id rather than dependent on grid geometry. That contract is correct and is not
    /// what this guard touches — the gap was that the dropped bound had also been the only thing limiting the
    /// query's COST, and no radius bound existed anywhere else: not in <c>AbilityValidator</c>, not in
    /// <c>EffectBounds</c>, and not in the JSON converter (<c>FixedJsonConverter</c> admits the whole 16.16 range).
    /// With <c>GRID_DIM=32</c> cells of <c>CELL_SIZE=10</c>, an authored radius of 320 visited all 1024 cells and
    /// ran the target predicate over every alive entity, once per outer target when nested — on the 30 Hz lockstep
    /// tick path, where an overrun stalls every peer instead of degrading locally.
    ///
    /// <b>The fix.</b> A load-time cap, because the scan cannot bound itself without giving up its selection
    /// contract. It lives in <see cref="EffectBounds"/> rather than in one validator so that all three authored
    /// effect sources inherit it from a single check — abilities, items, and a scenario's <c>run_effect</c> embeds
    /// — which the tests below assert one by one rather than assume.</para>
    ///
    /// <para>Godot-free and Fixed-only. Nothing here moves a sim value: the cap is a LOAD-time reject, so no
    /// <c>SimChecksum</c> input, <c>CanonicalModelHash</c> input or golden is touched. It does move
    /// <c>RulesetHash</c> (a handshake fingerprint) by design — a build that bounds the radius and one that does
    /// not must not share a match — which <c>RulesetHashTests</c> and <c>VersionStampConsistencyTests</c> pin.</para>
    /// </summary>
    public class EffectRadiusCapTests
    {
        private static readonly AbilityValidator Validator = new();

        private static Fixed Cap => Fixed.FromInt(EffectCaps.MaxSearchRadius);

        /// <summary>One raw 16.16 tick above the cap — the smallest authorable value the gate must reject.</summary>
        private static Fixed JustOverCap => Fixed.FromRaw(Cap.Raw + 1);

        private static EffectNode Leaf() => new HealEffect(Fixed.FromInt(1));

        private static SearchAreaEffect Search(Fixed radius, EffectNode? child = null) =>
            new SearchAreaEffect(radius, TargetFilter.Enemy, child ?? Leaf());

        private static AbilityDefinition Ability(EffectNode graph, string id = "radius_probe") =>
            new AbilityDefinition { Id = id, Targeting = "Self", EffectGraph = graph };

        // ── 1. The gate itself ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void EffectBounds_RejectsASearchAreaWiderThanTheCap()
        {
            EffectBoundsResult r = EffectBounds.Validate(Search(Fixed.FromInt(EffectCaps.MaxSearchRadius + 1)));

            Assert.False(r.IsValid);
            Assert.Contains("MaxSearchRadius", r.Error!);
            Assert.Contains("SearchAreaEffect", r.Error!);
        }

        [Fact]
        public void EffectBounds_AcceptsASearchAreaAtExactlyTheCap_AndRejectsOneRawTickAbove()
        {
            // The boundary is INCLUSIVE. Pinned in both directions from the same fixture so a future `>=` typo —
            // which would silently narrow every authored area effect by one raw tick — cannot pass.
            Assert.True(EffectBounds.Validate(Search(Cap)).IsValid);

            EffectBoundsResult over = EffectBounds.Validate(Search(JustOverCap));
            Assert.False(over.IsValid);
            Assert.Contains("MaxSearchRadius", over.Error!);
        }

        [Fact]
        public void EffectBounds_RejectsAWideRadiusNestedBelowAnAcceptableSearch()
        {
            // The root is within the cap and only the INNER search is over it — so this fails only if the walk
            // checks every SearchArea it visits rather than just the graph root. Nesting depth 2 is itself legal
            // (MaxSearchAreaDepth), which keeps the depth rule from being what rejects this.
            EffectNode graph = Search(Fixed.FromInt(5), Search(Fixed.FromInt(EffectCaps.MaxSearchRadius + 1)));

            EffectBoundsResult r = EffectBounds.Validate(graph);

            Assert.False(r.IsValid);
            Assert.Contains("MaxSearchRadius", r.Error!);
        }

        [Fact]
        public void EffectBounds_RejectsAWideRadiusInsideASequence()
        {
            EffectNode graph = new SequenceEffect(
                Leaf(),
                Search(Fixed.FromInt(EffectCaps.MaxSearchRadius * 2)));

            EffectBoundsResult r = EffectBounds.Validate(graph);

            Assert.False(r.IsValid);
            Assert.Contains("MaxSearchRadius", r.Error!);
        }

        // ── 2. Every authored surface behind that gate inherits it ─────────────────────────────────────

        [Fact]
        public void AbilityValidator_RejectsAWideRadiusAbility_WithALocatedError()
        {
            AbilityValidationResult r =
                Validator.Validate(Ability(Search(Fixed.FromInt(EffectCaps.MaxSearchRadius + 1))));

            Assert.False(r.Ok);
            Assert.Contains("radius_probe", r.Error!);   // the ability id
            Assert.Contains("effect", r.Error!);         // the located path
            Assert.Contains("MaxSearchRadius", r.Error!);

            // Positive control on the same shape: at the cap the ability validates.
            Assert.True(Validator.Validate(Ability(Search(Cap))).Ok);
        }

        [Fact]
        public void AbilityLoader_RejectsAWideRadiusAbilityJson_EndToEnd()
        {
            // The authoring path a creator actually uses. FixedJsonConverter accepts any in-range 16.16 value, so
            // before the cap this file parsed AND validated: the reject can only come from the load-time bound.
            string Json(int radius) => $@"{{
              ""id"": ""wide_nuke"",
              ""display_name"": ""Wide Nuke"",
              ""targeting"": ""TargetUnit"",
              ""cooldown"": 5,
              ""effect"": {{
                ""kind"": ""search_area"",
                ""radius"": {radius},
                ""filter"": ""Enemy"",
                ""child"": {{ ""kind"": ""damage"", ""amount"": 10, ""damage_type"": ""Magic"" }}
              }}
            }}";

            AbilityValidationResult over = AbilityLoader.Load(Json(EffectCaps.MaxSearchRadius + 1), "wide_nuke.json");
            Assert.False(over.Ok);
            Assert.Contains("MaxSearchRadius", over.Error!);

            AbilityValidationResult atCap = AbilityLoader.Load(Json(EffectCaps.MaxSearchRadius), "wide_nuke.json");
            Assert.True(atCap.Ok, atCap.Error);
        }

        [Fact]
        public void ItemDefinitionValidator_RejectsAWideRadiusConsumable()
        {
            // Items run the SAME EffectBounds gate. Without the check in EffectBounds this would have needed a
            // second, independently-drifting copy in ItemDefinitionValidator — which is why the cap lives there.
            var validator = new ItemDefinitionValidator();

            ItemValidationResult over = validator.Validate(new ItemDefinition
            {
                Id = "wide_bomb",
                Charges = 1,
                EffectGraph = Search(Fixed.FromInt(EffectCaps.MaxSearchRadius + 1)),
            });
            Assert.False(over.Ok);
            Assert.Contains("MaxSearchRadius", over.Error!);

            ItemValidationResult atCap = validator.Validate(new ItemDefinition
            {
                Id = "wide_bomb",
                Charges = 1,
                EffectGraph = Search(Cap),
            });
            Assert.True(atCap.Ok, atCap.Error);
        }

        [Fact]
        public void ScenarioValidator_RejectsAWideRadiusRunEffectEmbed()
        {
            // The third authored surface: a trigger graph's run_effect payload, gated pre-tick by ScenarioValidator.
            var validator = new ScenarioValidator();

            ScenarioData over = MinimalScenario();
            over.TriggerGraphJson = TriggerGraph
                .BuildRunEffectTrigger("t", "match_start", Search(Fixed.FromInt(EffectCaps.MaxSearchRadius + 1)))
                .ToCanonicalJson();
            ValidationResult rOver = validator.Validate(over);
            Assert.False(rOver.Ok);
            Assert.Contains("trigger_graph", rOver.Error!);
            Assert.Contains("MaxSearchRadius", rOver.Error!);

            ScenarioData atCap = MinimalScenario();
            atCap.TriggerGraphJson = TriggerGraph
                .BuildRunEffectTrigger("t", "match_start", Search(Cap))
                .ToCanonicalJson();
            ValidationResult rAtCap = validator.Validate(atCap);
            Assert.True(rAtCap.Ok, rAtCap.Error);
        }

        // ── 3. The cost ceiling, made executable ───────────────────────────────────────────────────────

        [Fact]
        public void TheCap_BoundsTheSpatialHashCellWalk_ToAFractionOfTheGrid()
        {
            // The claim MaxSearchRadius' doc makes, checked against the spatial hash's real geometry rather than
            // restated in prose: the cap holds cellRadius at 7, so the query's cell walk spans at most 15x15 = 225
            // of the grid's 1024 cells, whatever an author writes. Recomputed from the live constants so a grid
            // re-parameterization (a smaller GRID_DIM, a different CELL_SIZE) turns this red instead of quietly
            // restoring the whole-grid scan.
            int cellRadius = CellRadiusFor(Cap);
            int span = 2 * cellRadius + 1;
            int cellsVisited = span * span;
            int totalCells = SpatialHash.GRID_DIM * SpatialHash.GRID_DIM;

            Assert.Equal(7, cellRadius);
            Assert.Equal(225, cellsVisited);
            Assert.True(cellsVisited * 4 <= totalCells,
                $"MaxSearchRadius={EffectCaps.MaxSearchRadius} spans {cellsVisited} of {totalCells} spatial-hash " +
                "cells — no longer a meaningful ceiling on QueryRadiusLowestIds' exit-free scan (DW-534).");

            // And the counterfactual the ledger measured: an unbounded radius covers the ENTIRE grid, which is the
            // cost the cap exists to prevent. 320 = GRID_DIM * CELL_SIZE.
            int uncapped = CellRadiusFor(Fixed.FromInt(SpatialHash.GRID_DIM * (int)SpatialHash.CELL_SIZE_F));
            int uncappedSpan = 2 * uncapped + 1;
            Assert.True(uncappedSpan * uncappedSpan > totalCells,
                "the pre-cap worst case should cover the whole grid — the premise behind the cap.");
        }

        [Fact]
        public void MaxSearchRadius_IsActuallyFoldedIntoTheRulesetHash()
        {
            // Teeth for the fold (RulesetHashTests pins the whole stream; this isolates the one term DW-534 added).
            // Recompute the documented byte stream with MaxSearchRadius OMITTED: dropping the fold line in
            // RulesetHash.Compute would make these equal, so two builds disagreeing on the cap could still shake
            // hands and then run different per-cast work.
            const ulong offset = 14695981039346656037UL;
            ulong h = offset;
            h = Mix(h, RulesetHash.AlgoVersion);
            h = Mix(h, EffectCaps.MaxEffectDepth);
            h = Mix(h, EffectCaps.MaxSequenceChildren);
            h = Mix(h, EffectCaps.MaxSearchTargets);
            h = Mix(h, EffectCaps.MaxHitsPerSearch);
            h = Mix(h, EffectCaps.MaxEffectFrames);
            h = Mix(h, EffectCaps.MaxSpawnCount);
            h = Mix(h, EffectCaps.MaxPersistentPeriods);
            h = Mix(h, EffectCaps.MaxModifiersPerEntity);
            h = Mix(h, EffectCaps.MaxSearchAreaDepth);
            h = Mix(h, EffectCaps.MaxTotalEffectNodes);
            ulong withoutTheCap = h == 0UL ? 1UL : h;

            Assert.NotEqual(withoutTheCap, RulesetHash.Compute());
        }

        // ── 4. Shipped content tripwire ────────────────────────────────────────────────────────────────

        [Fact]
        public void EveryShippedAbility_DeclaresARadiusWithinTheCap()
        {
            // A load-time cap is only free while no shipped file crosses it. This scans the real content two ways:
            // through the loader (so a future over-cap ability file fails as a LOAD reject, naming itself), and
            // over the raw JSON (so the assertion still has teeth if the loader ever stops running the gate).
            string dir = ResolveDataDir("abilities");
            string[] files = Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(files);

            int radiiSeen = 0;
            foreach (string file in files)
            {
                AbilityValidationResult r = AbilityLoader.LoadFromFile(file);
                Assert.True(r.Ok, $"{Path.GetFileName(file)}: {r.Error}");

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (double radius in AuthoredRadii(doc.RootElement))
                {
                    radiiSeen++;
                    Assert.True(radius <= EffectCaps.MaxSearchRadius,
                        $"{Path.GetFileName(file)} authors a search_area radius of {radius}, above " +
                        $"MaxSearchRadius={EffectCaps.MaxSearchRadius} (DW-534).");
                }
            }

            // Vacuous-pass defense: the shipped roster really does contain search_area nodes to check.
            Assert.True(radiiSeen > 0,
                $"No 'radius' property found under any ability in '{dir}' — the scan drifted, not a clean roster.");
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The exact <c>cellRadius</c> <c>SpatialHash.QueryRadiusLowestIds</c> derives from a radius.</summary>
        private static int CellRadiusFor(Fixed radius) =>
            (radius / Fixed.FromFloat(SpatialHash.CELL_SIZE_F)).ToInt() + 1;

        /// <summary>FNV-64 fold of a 32-bit int as 4 LE bytes (the documented <c>RulesetHash.MixInt</c>).</summary>
        private static ulong Mix(ulong h, int value)
        {
            const ulong prime = 1099511628211UL;
            uint v = (uint)value;
            h ^= v & 0xFF;         h *= prime;
            h ^= (v >> 8) & 0xFF;  h *= prime;
            h ^= (v >> 16) & 0xFF; h *= prime;
            h ^= (v >> 24) & 0xFF; h *= prime;
            return h;
        }

        /// <summary>Every <c>radius</c> number anywhere in an ability document (the search_area nodes).</summary>
        private static System.Collections.Generic.IEnumerable<double> AuthoredRadii(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty p in el.EnumerateObject())
                    {
                        if (p.NameEquals("radius") && p.Value.ValueKind == JsonValueKind.Number)
                            yield return p.Value.GetDouble();
                        foreach (double r in AuthoredRadii(p.Value)) yield return r;
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement child in el.EnumerateArray())
                        foreach (double r in AuthoredRadii(child)) yield return r;
                    break;
            }
        }

        /// <summary>Walk up from the test binary to the repo's <c>resources/data/&lt;sub&gt;</c>.</summary>
        private static string ResolveDataDir(string sub)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", sub);
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException($"Could not locate resources/data/{sub} above {AppContext.BaseDirectory}");
        }

        /// <summary>A minimal VALID scenario (mirrors <c>NegativeValidationTests.ValidModel</c>).</summary>
        private static ScenarioData MinimalScenario() => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 200f, BaseX =  45f, BaseZ = 0f },
            },
            ResourceNodes = new[] { new ScenarioResourceNode { X = 10f, Z = 10f, Supply = 400f, Rate = 5f, MaxGatherers = 4 } },
            Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -45f, Z = 0f, PreBuilt = true } },
            Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 1, X = 42f, Z = 3f } },
        };
    }
}
