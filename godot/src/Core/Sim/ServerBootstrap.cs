#nullable enable
using System.Globalization;             // Story 7.1: process-wide InvariantCulture pin
using ProjectChimera.Combat;            // DamageTable
using ProjectChimera.Core;              // Faction, FactionRegistry
using ProjectChimera.Core.Definitions;  // ScenarioData, ScenarioValidator, ValidationResult, Validated<>, FactionDefinition

namespace ProjectChimera.Core.Sim
{
    /// <summary>
    /// Headless peer composition root (AR-38, Story 1.9a — determinism strangler Step 6). Builds the EXACT 1.8 sim
    /// spine — <see cref="SimulationHost"/> + <see cref="ScenarioValidator"/> + <see cref="ScenarioApplier"/> — with
    /// NO presentation and NO Godot Node tree, reused VERBATIM by the dedicated server so the server's sim path is
    /// byte-identical to the client's (the AC1 golden-determinism guarantee: server start-state checksum == client
    /// offline start-state).
    ///
    /// <para>Godot-free: the caller (the thin Godot edge — the re-pointed MainScene headless branch) resolves all
    /// <c>res://</c> paths and loads the model / faction defs / damage table FIRST, then passes already-resolved
    /// inputs down — mirroring how the client's presentation pre-pass fills <c>slotFactionDefs</c> in place before
    /// <see cref="ScenarioApplier.Apply"/>. This type never re-implements apply/validate/spawn and CANNOT mint a
    /// <see cref="Validated{T}"/> — it goes THROUGH the validator (the only mint path) and hands the proof token to
    /// the applier. On the server the validator is FAIL-CLOSED: a server with no valid start-state cannot arbitrate,
    /// so an invalid scenario logs and returns null rather than ticking unvalidated state. (D2)</para>
    /// </summary>
    public static class ServerBootstrap
    {
        /// <summary>
        /// Build a validated, applied, Godot-free sim host for the server, or <c>null</c> if the scenario fails
        /// validation (fail-closed). Composes the same three types the client uses — no second applier/validator/
        /// spawn path. <paramref name="slotFactionDefs"/> is the pre-resolved per-slot faction array (index by
        /// <see cref="Faction"/>); <paramref name="activeFactionCount"/> seeds the checksum's faction registry
        /// (e.g. 2 for a 1v1, matching <c>new FactionRegistry(2)</c>).
        /// </summary>
        /// <param name="elevationGrid">DW-508: the finalized-terrain <see cref="ElevationGrid"/>, when the Godot edge
        /// has one. Terrain sampling is Godot-side (this type stays Godot-free), so the caller hands the already-baked
        /// Godot-free grid down exactly as the client's <c>ScenarioLoadPhase.BuildAndInjectElevationGrid</c> hands it
        /// to the applier. <c>null</c> ⇒ flat: spawns sit at <see cref="Fixed.Zero"/> elevation and the pathability
        /// union's SLOPE arm derives nothing (painted ∪ prop/water only) — byte-identical to the prior behaviour.</param>
        public static SimulationHost? Build(
            ScenarioData model, FactionDefinition?[] slotFactionDefs, DamageTable? damageTable,
            ILogSink log, int activeFactionCount, AbilityRegistry? abilityRegistry = null,
            ElevationGrid? elevationGrid = null)
        {
            // Story 7.1: pin InvariantCulture process-wide at the headless server's composition root — the same
            // hardening net the client applies in MainScene._EnterTree, so the server's number formatting/parsing
            // is locale-independent and cannot diverge from a client on a comma-decimal locale.
            CultureInfo.DefaultThreadCurrentCulture   = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            // DefaultThread* only governs threads that have not already materialized a culture; pin the running
            // thread explicitly so the net covers the server's own execution thread too.
            CultureInfo.CurrentCulture   = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

            AbilityRegistry registry = abilityRegistry ?? AbilityRegistry.Empty;

            // Story 2.4b (server parity, MP-critical): resolve each slot faction's ability ids → registry indices
            // BEFORE the applier spawns any unit (ApplyUnitDefinition reads UnitDefinition.AbilityIndices). The
            // server MUST resolve from the SAME ability files into the SAME ascending-Id indices every client uses
            // — the registry sorts by ability Id, so identical files yield identical indices — or its arbitrating
            // checksum diverges from the peers'. Idempotent; drops unknown ids; null defs (unresolved slots) skip.
            foreach (var def in slotFactionDefs)
            {
                if (def == null) continue;
                // DW-285: thread the server's ILogSink so an unknown / over-cap / shadowed-passive ability id WARNS
                // instead of silently shrinking the roster's powers. The drop itself is unchanged (fail-open), so the
                // server↔client resolve stays byte-identical — only the diagnostics are new.
                foreach (var u in def.Units)
                    u.ResolveAbilities(registry, log);
                // Story 2.11 (AC2): closed-set tag validation — drop unknown-tag units (fail-closed, located error).
                // Runs the IDENTICAL drop as the client pre-pass (ScenarioLoadPhase) so the arbitrating server stays
                // in parity — a divergent roster between server + client would desync from tick 1.
                foreach (string err in UnitTagValidator.ValidateAndDropUnits(def))
                    log.Warn($"[UnitTagValidator] {err} (unit dropped)");
            }

            // Same Create the client calls — null damageTable resolves to DamageTable.Default inside combat ctors.
            // registry passed BY NAME so aiLevel keeps its default (the server's prior 5-arg behavior is preserved).
            var host = SimulationHost.Create(
                log, new FactionRegistry(activeFactionCount),
                slotFactionDefs[(int)Faction.Player1], slotFactionDefs[(int)Faction.Player2],
                damageTable, registry: registry);

            // The ONLY way to obtain a Validated<ScenarioData> (the Proof ctor is internal + source-scanned).
            ValidationResult r = new ScenarioValidator().Validate(model, slotFactionDefs); // Story 6.8: authored-building-id gate
            if (!r.Ok)
            {
                // Server is authoritative ⇒ fail-closed: do not tick unvalidated start-state.
                log.Warn($"[ServerBootstrap] scenario REJECTED: {r.Error}");
                return null;
            }

            var applier = new ScenarioApplier(host, log, slotFactionDefs);

            // ── DW-508: build + inject the STATIC blocked-cell grid BEFORE Apply, exactly as the client's
            //    ScenarioLoadPhase (BuildAndInjectElevationGrid → BuildAndInjectPathabilityGrid → Apply) does.
            //
            //    Pre-fix the server called NEITHER setter, so on the headless leg world.Pathability stayed null:
            //    MovementSystem's blocked-cell rejection was a server-side NO-OP while every client enforced it. On any
            //    map with painted, blocking-prop, water or slope-derived blocked cells the arbitrating server's unit
            //    positions — and therefore its SimChecksum — diverged from its peers' from the first tick a unit
            //    touched a wall, i.e. the arbiter itself was the desync source.
            //
            //    Both legs route through the ONE shared Godot-free ScenarioApplier.BuildPathabilityGrid recipe, so
            //    server and client can never disagree on the blocked-cell set for the same model + elevation grid
            //    (the same structural argument that keeps boot and Edit→Play re-apply in step — DW-157). The grid is
            //    derived from the VALIDATED model (r.Value.Value), never the raw argument, so nothing un-gated can
            //    reach a sim store.
            //
            //    DETERMINISM: Resolve returns null when NOTHING is blocked, and a null grid makes both setters exact
            //    no-ops (EntityWorld's elevation/pathability default to null already). Every golden scenario — the
            //    in-code GoldenApplierScenario mirror and the shipped map_02_iron_crossing / alpha_map_01 JSON — carries
            //    no painted layer, no props, no water and no slope config, so this is byte-identical there and moves no
            //    golden, no CanonicalModelHash and no StartStateHash.
            applier.SetElevationGrid(elevationGrid);
            applier.SetPathabilityGrid(ScenarioApplier.BuildPathabilityGrid(r.Value.Value, elevationGrid));

            applier.Apply(r.Value);
            return host;
        }
    }
}
