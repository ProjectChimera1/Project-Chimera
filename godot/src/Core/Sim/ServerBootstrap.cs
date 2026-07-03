#nullable enable
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
        public static SimulationHost? Build(
            ScenarioData model, FactionDefinition?[] slotFactionDefs, DamageTable? damageTable,
            ILogSink log, int activeFactionCount, AbilityRegistry? abilityRegistry = null)
        {
            AbilityRegistry registry = abilityRegistry ?? AbilityRegistry.Empty;

            // Story 2.4b (server parity, MP-critical): resolve each slot faction's ability ids → registry indices
            // BEFORE the applier spawns any unit (ApplyUnitDefinition reads UnitDefinition.AbilityIndices). The
            // server MUST resolve from the SAME ability files into the SAME ascending-Id indices every client uses
            // — the registry sorts by ability Id, so identical files yield identical indices — or its arbitrating
            // checksum diverges from the peers'. Idempotent; drops unknown ids; null defs (unresolved slots) skip.
            foreach (var def in slotFactionDefs)
            {
                if (def == null) continue;
                foreach (var u in def.Units)
                    u.ResolveAbilities(registry);
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
            ValidationResult r = new ScenarioValidator().Validate(model);
            if (!r.Ok)
            {
                // Server is authoritative ⇒ fail-closed: do not tick unvalidated start-state.
                log.Warn($"[ServerBootstrap] scenario REJECTED: {r.Error}");
                return null;
            }

            new ScenarioApplier(host, log, slotFactionDefs).Apply(r.Value);
            return host;
        }
    }
}
