#nullable enable
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The per-scenario supply / population-cap config (Story 4.4) — an authored starting cap, an optional hard
    /// ceiling, and a master enable toggle. A nullable net-new block on <see cref="ScenarioData.Supply"/> (JSON
    /// <c>supply</c>): <c>null</c> ⇒ use today's hardcoded default exactly (<see cref="ResourceStore.STARTING_SUPPLY_CAP"/>,
    /// no ceiling, gating enabled), and the block is OMITTED from serialization when null
    /// (<see cref="JsonIgnoreCondition.WhenWritingNull"/>, the <see cref="RevivalRule"/>/<see cref="PersistenceManifest"/>
    /// omit-when-null precedent) — so a scenario without one serializes byte-for-byte identically, moving no golden.
    ///
    /// <para><b>Authoring-only shape, resolved once.</b> The sim never reads this class directly — it is resolved ONCE
    /// (via <see cref="ResourceStore.ConfigureSupply"/>) at scenario-apply into plain instance state on
    /// <see cref="ResourceStore"/>, never re-read inside a tick. Because the RESOLVED values feed the already-folded
    /// <c>SupplyCap</c>/<c>SupplyUsed</c> arrays and gate <c>TrainUnit</c>, they are also folded (as their resolved
    /// values) into <see cref="CanonicalModelHash"/> so a mismatched config is rejected at the lobby handshake instead
    /// of desyncing in-sim.</para>
    ///
    /// <para><b>Determinism.</b> Godot-free (<c>src/Core/Definitions</c>), plain <c>int</c>/<c>bool</c> authoring
    /// numbers. <see cref="ScenarioValidator"/> range-checks it fail-closed at the pre-tick gate so a hand-edited/cheat
    /// config is rejected.</para>
    /// </summary>
    public sealed class SupplyConfig
    {
        /// <summary>Starting supply cap per faction before building bonuses. Mirrors
        /// <see cref="ResourceStore.STARTING_SUPPLY_CAP"/> (10).</summary>
        [JsonPropertyName("starting_cap")]
        public int StartingCap { get; set; } = ResourceStore.STARTING_SUPPLY_CAP;

        /// <summary>Optional hard ceiling <c>SupplyCap</c> is clamped to after building bonuses are applied.
        /// <c>null</c> ⇒ uncapped (today's behavior).</summary>
        [JsonPropertyName("hard_ceiling")]
        public int? HardCeiling { get; set; } = null;

        /// <summary>Master gate. <c>true</c> (default) ⇒ <c>TrainUnit</c> is blocked once <c>SupplyUsed</c> reaches
        /// <c>SupplyCap</c> (today's behavior). <c>false</c> ⇒ training is never supply-blocked, though
        /// <c>SupplyCap</c>/<c>SupplyUsed</c> are still computed, displayed, and folded identically.</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// The single null-means-default AND defensive-clamp resolution boundary (Story 4.4 review-pass-2 patch).
        /// <see cref="ScenarioValidator"/> rejects a negative <see cref="StartingCap"/>/<see cref="HardCeiling"/> at
        /// import (and since Story 7.7 the load gate is fail-closed everywhere), but a DIRECT caller can still
        /// hand this resolver an unvalidated config. Clamping here — mirroring
        /// <c>RevivalRuleRuntime.LinearSat</c>'s own documented defense-in-depth precedent ("the primary gate is
        /// ScenarioValidator... this is defense-in-depth") — guarantees a negative authored value can never produce
        /// a negative runtime <c>SupplyCap</c> (which would permanently soft-lock training).
        /// <see cref="ResourceStore.ConfigureSupply"/> (the runtime resolution) and <see cref="CanonicalModelHash.Compute"/>
        /// (the lobby-handshake hash) BOTH call this SAME method, so hash-equality ⇔ post-resolution
        /// runtime-equality holds in both directions — an invalid value can neither collide with a distinct valid
        /// resolution (the bug this patch fixes: raw <c>HardCeiling ?? -1</c> made an authored <c>-1</c>
        /// indistinguishable from "uncapped") nor silently diverge from what the hash actually represents.
        /// </summary>
        public static (int startingCap, int? hardCeiling, bool enabled) Resolve(SupplyConfig? config)
        {
            int startingCap = System.Math.Max(0, config?.StartingCap ?? ResourceStore.STARTING_SUPPLY_CAP);
            int? hardCeiling = config?.HardCeiling is int hc ? System.Math.Max(0, hc) : (int?)null;
            bool enabled = config?.Enabled ?? true;
            return (startingCap, hardCeiling, enabled);
        }
    }
}
