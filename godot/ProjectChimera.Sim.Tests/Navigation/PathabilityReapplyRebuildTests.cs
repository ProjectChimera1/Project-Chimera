#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Navigation;
using ProjectChimera.Sim.Tests.Golden;   // GoldenApplierScenario fixture (host + applier + alpha_map_01 mirror)
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// DW-157 (Story 14.8) — Tier-1 coverage of the Godot-free composition <c>MainScene.ResetToAuthoredStart</c>
    /// performs on Edit→Play (F5) re-apply: rebuild the static <see cref="PathabilityGrid"/> from the CURRENT
    /// <see cref="ScenarioData"/> via the ONE shared <see cref="ScenarioApplier.BuildPathabilityGrid"/> recipe, set it
    /// on the applier BEFORE <see cref="ScenarioApplier.Apply"/> (Apply threads it into <see cref="EntityWorld"/> before
    /// any spawn), and re-apply — so a blocking prop added / removed in Edit is honored on the next Play WITHOUT a full
    /// reload. Pre-fix the applier re-threaded its STALE cached boot grid, so an Edit-added obstacle was walked through.
    ///
    /// <para>Reuses the <c>SimResetTests</c> clear+re-apply composition (build host + applier over the golden fixture,
    /// <see cref="SimulationHost.ClearForReset"/> + re-apply the edited model) and the prop-footprint idiom (a blocking
    /// prop stamps one <see cref="FlowField.WorldToCell"/> cell). Cell C = world (1, 1) → cell (64, 64), which is clear
    /// of every start/base/node/building in the golden model (so a blocking prop there validates).</para>
    /// </summary>
    public class PathabilityReapplyRebuildTests
    {
        private const int GS = PathabilityGrid.GRID_SIZE;

        // Cell C: world (1, 1) → FlowField cell (64, 64). Clear in the golden model (no unit/base/node/building there),
        // so a blocking prop stamped here passes ScenarioValidator's not-blocked position checks.
        private static readonly Fixed Cx = Fixed.FromFloat(1f);
        private static readonly Fixed Cz = Fixed.FromFloat(1f);

        /// <summary>Build a wired host + applier over the golden fixture and apply <paramref name="model"/>, MIRRORING
        /// boot: the pathability grid is rebuilt from the model and injected on the applier BEFORE Apply, exactly as
        /// <c>ScenarioLoadPhase.BuildAndInjectPathabilityGrid</c> does.</summary>
        private static (SimulationHost host, ScenarioApplier applier) BuildApplied(ScenarioData model)
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);

            applier.SetPathabilityGrid(ScenarioApplier.BuildPathabilityGrid(model, applier.ElevationGrid));
            ApplyValidated(applier, model);
            return (host, applier);
        }

        /// <summary>Validate + apply a model through the applier (mirrors the boot gate + the reset re-apply).</summary>
        private static void ApplyValidated(ScenarioApplier applier, ScenarioData model)
        {
            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);
        }

        private static ScenarioProp BlockingPropAtC() =>
            new ScenarioProp { PropId = "rock", X = 1f, Z = 1f, BlocksPathing = true };

        // ── (a) Obstacle added in Edit → honored after re-apply (the primary DW-157 assertion, RED-provable) ──

        [Fact]
        public void AddedBlockingProp_HonoredAfterReapply()
        {
            ScenarioData model = GoldenApplierScenario.BuildModel(); // cell C passable (no props at boot)
            var (host, applier) = BuildApplied(model);

            // Boot state: nothing blocks C (flat map ⇒ null grid, or a grid that reports C passable).
            Assert.True(host.World.Pathability == null || !host.World.Pathability.IsBlocked(Cx, Cz));

            // Edit adds a blocking prop at C → model A'.
            model.Props = new[] { BlockingPropAtC() };

            // In-place reset + re-apply (the Godot-free half of MainScene.ResetToAuthoredStart): rebuild the grid from
            // the CURRENT model and set it on the applier BEFORE Apply threads it into EntityWorld. Skipping this
            // SetPathabilityGrid line reproduces the pre-fix stale-cached-grid reuse → the assertion below turns RED.
            host.ClearForReset();
            applier.SetPathabilityGrid(ScenarioApplier.BuildPathabilityGrid(model, applier.ElevationGrid));
            ApplyValidated(applier, model);

            Assert.NotNull(host.World.Pathability);
            Assert.True(host.World.Pathability!.IsBlocked(Cx, Cz)); // Edit-added obstacle honored this Play, no reload
        }

        // ── (b) Obstacle removed in Edit → passable after re-apply (rebuild un-stamps from source) ──

        [Fact]
        public void RemovedProp_UnblockedAfterReapply()
        {
            ScenarioData model = GoldenApplierScenario.BuildModel();
            model.Props = new[] { BlockingPropAtC() }; // A blocks C
            var (host, applier) = BuildApplied(model);

            Assert.NotNull(host.World.Pathability);
            Assert.True(host.World.Pathability!.IsBlocked(Cx, Cz));

            // Edit deletes the prop → model A'.
            model.Props = null;

            host.ClearForReset();
            applier.SetPathabilityGrid(ScenarioApplier.BuildPathabilityGrid(model, applier.ElevationGrid));
            ApplyValidated(applier, model);

            // The grid is rebuilt from source, so the removed prop un-stamps: nothing blocked ⇒ null grid ⇒ C passable.
            Assert.True(host.World.Pathability == null || !host.World.Pathability.IsBlocked(Cx, Cz));
        }

        // ── (c) Unchanged model → the rebuilt grid is byte-identical (no golden shift for an unchanged model) ──

        [Fact]
        public void UnchangedModel_ReapplyGridIdentical()
        {
            // A model with a blocking prop: two builds of the SAME model yield equal Blocked masks — so the boot build
            // and the Edit→Play re-apply can never diverge on the blocked-cell set for an unchanged model.
            ScenarioData blocked = GoldenApplierScenario.BuildModel();
            blocked.Props = new[] { BlockingPropAtC() };
            PathabilityGrid? g1 = ScenarioApplier.BuildPathabilityGrid(blocked, null);
            PathabilityGrid? g2 = ScenarioApplier.BuildPathabilityGrid(blocked, null);
            Assert.NotNull(g1);
            Assert.NotNull(g2);
            Assert.Equal(g1!.Blocked, g2!.Blocked);

            // And a flat model (nothing blocked) resolves to null on both builds — the byte-identical no-op path.
            ScenarioData flat = GoldenApplierScenario.BuildModel();
            Assert.Null(ScenarioApplier.BuildPathabilityGrid(flat, null));
            Assert.Null(ScenarioApplier.BuildPathabilityGrid(flat, null));
        }

        // ── (d) No scenario (fallback re-apply) → the derivation is a null no-op, matching the hasScenario guard ──

        [Fact]
        public void NoScenario_BuildsNullGrid()
        {
            // MainScene.ResetToAuthoredStart skips the rebuild+inject block when _ctx.Scenario is null (fallback maps
            // are flat, as at boot). This is the Godot-free proxy for that guard: a null scenario yields a null grid
            // (no blocked cells), so even the skipped path would leave every sink flat — the skip is safe.
            Assert.Null(ScenarioApplier.BuildPathabilityGrid(null, null));
        }

        // ── (e) Slope arm of the shared recipe — the elevation-grid input the ElevationGrid getter exists to feed ──

        /// <summary>A 256×256 / ±128 elevation grid with a hard cliff at world X=0 (0 west, 10 east) — mirrors the real
        /// Godot elevation grid layout (identical to SlopeAutoBlockTests.CliffAtX0).</summary>
        private static ElevationGrid CliffAtX0()
        {
            const int N = 256;
            var heights = new Fixed[N * N];
            for (int row = 0; row < N; row++)
                for (int col = 0; col < N; col++)
                    heights[row * N + col] = col >= 128 ? Fixed.FromInt(10) : Fixed.Zero;
            return new ElevationGrid(heights, N, N, Fixed.FromInt(-128), Fixed.FromInt(-128), Fixed.One);
        }

        [Fact]
        public void SlopeArm_DerivesCells_ThroughBuildPathabilityGrid()
        {
            // The whole reason ScenarioApplier.ElevationGrid was exposed: the Edit→Play re-apply feeds the already-
            // injected grid so slope-blocked cells re-derive. Pin that the slope arm of the shared recipe (the gate
            // s.SlopeAutoBlock && threshold > 0, the single Fixed.FromFloat boundary, and threading `elev` into Resolve)
            // is exercised — SlopeAutoBlockTests only covers PathabilityGrid.DeriveSlopeBlockedInto directly.
            ScenarioData slopeOn = GoldenApplierScenario.BuildModel();
            slopeOn.SlopeAutoBlock = true;
            slopeOn.SlopeBlockThreshold = 1f;

            PathabilityGrid? grid = ScenarioApplier.BuildPathabilityGrid(slopeOn, CliffAtX0());
            Assert.NotNull(grid);
            // The cliff-straddling flow column (col 63, per SlopeAutoBlockTests) is slope-blocked.
            Assert.True(grid!.Blocked[70 * GS + 63], "the cliff column should be slope-blocked via the shared recipe.");

            // A null elevation grid (no heightmap) derives NO slope cells — the recipe's slope arm degrades to nothing.
            Assert.Null(ScenarioApplier.BuildPathabilityGrid(slopeOn, null));

            // Slope-auto-block OFF derives nothing even WITH a cliff grid (the gate short-circuits Fixed.FromFloat).
            ScenarioData slopeOff = GoldenApplierScenario.BuildModel();
            slopeOff.SlopeAutoBlock = false;
            Assert.Null(ScenarioApplier.BuildPathabilityGrid(slopeOff, CliffAtX0()));
        }

        // ── (f) The FLOW-FIELD ROUTING half — the rebuilt grid's mask must exclude the Edit-added cell from the field ──

        [Fact]
        public void RebuiltGridMask_ExcludesCell_FromFlowField()
        {
            // The re-apply feeds grid.Blocked to FlowFieldSystem.SetStaticBlocked and forces RebuildObstacles so the
            // routing half honors the Edit-added obstacle. The MainScene wiring itself is Godot-bound, but the MECHANISM
            // — a grid built by the shared recipe routes the flow field away from its blocked cells — is Tier-1 drivable
            // (mirrors FlowFieldBlockingTests). Deleting either the SetStaticBlocked or RebuildObstacles line would
            // regress this in-engine with the other DW-157 tests still green; this pins the composition they rely on.
            ScenarioData edited = GoldenApplierScenario.BuildModel();
            edited.Props = new[] { BlockingPropAtC() };
            PathabilityGrid? grid = ScenarioApplier.BuildPathabilityGrid(edited, null);
            Assert.NotNull(grid);

            var sys = new FlowFieldSystem();
            sys.SetStaticBlocked(grid!.Blocked);
            sys.RebuildObstacles(new BuildingStore()); // no buildings; ORs the rebuilt static mask into the obstacle map
            FlowField field = sys.GetOrCompute(new FixedVec3(Fixed.FromInt(20), Fixed.Zero, Fixed.Zero));

            FlowField.WorldToCell(Cx, Cz, out int col, out int row);
            // The Edit-added blocked cell (64, 64) is excluded from the field — never assigned a steering direction.
            Assert.Equal(FixedVec3.Zero, field.Directions[row * GS + col]);
            // A nearby passable cell IS reachable (the BFS ran and routed around C) — proves the field actually computed.
            Assert.NotEqual(FixedVec3.Zero, field.Directions[(row + 3) * GS + (col + 3)]);
        }
    }
}
