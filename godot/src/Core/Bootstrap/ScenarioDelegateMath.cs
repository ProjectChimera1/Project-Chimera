#nullable enable
namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// DW-333 — the Godot-free arithmetic behind <c>ScenarioDelegateBinder</c>'s trigger delegates, extracted so
    /// the Tier-1 sim suite can assert it (the binder itself needs a Godot <c>SceneContext</c> and lives under
    /// <c>Bootstrap/Phases/</c>, outside the Godot-free assembly — the <see cref="SlotFactionReset"/> precedent).
    ///
    /// <para><b>Why this matters for determinism.</b> <see cref="FanOutX"/> computes the multi-unit
    /// <c>spawn_unit</c> trigger fan-out coordinate that feeds <c>ScenarioApplier.SpawnUnitAt</c> — sim truth,
    /// covered by SimChecksum. It must stay Fixed-only (no in-tick float): a wrong
    /// <see cref="SpawnLateralOffset"/> raw value (163840 ≠ 2.5) or broken accumulation would desync spawns with
    /// no failing test, because <c>ScenarioDirectorSpawnActionTests</c> captures <c>OnSpawnUnit</c> and bypasses
    /// the binder arithmetic. <see cref="ToastDurationSeconds"/> is the OTHER side of the C3 rule: the Fixed→float
    /// conversion for the HUD toast timer happens at THIS presentation boundary, never in the tick.</para>
    /// </summary>
    public static class ScenarioDelegateMath
    {
        /// <summary>The per-unit lateral spawn offset, 2.5 world units = 163840 raw in 16.16 (Story 7.1). A named
        /// <see cref="Fixed"/> constant so the trigger spawn fan-out stays Fixed-only — no in-tick float offset.</summary>
        public static readonly Fixed SpawnLateralOffset = Fixed.FromRaw(163840); // 2.5

        /// <summary>
        /// The x coordinate of the <paramref name="index"/>-th unit of a multi-unit <c>spawn_unit</c> fan-out:
        /// <c>x + index·2.5</c>, computed entirely in <see cref="Fixed"/> (the exact expression the binder ships —
        /// unit 0 spawns at the authored anchor, each subsequent unit exactly one
        /// <see cref="SpawnLateralOffset"/> further along +x).
        /// </summary>
        public static Fixed FanOutX(Fixed x, int index) => x + Fixed.FromInt(index) * SpawnLateralOffset;

        /// <summary>
        /// The <c>display_message</c> duration for the HUD toast timer — the sanctioned Fixed→float conversion at
        /// the presentation boundary (presentation-output only; the sim tick never consumes this value).
        /// </summary>
        public static float ToastDurationSeconds(Fixed duration) => duration.ToFloat();
    }
}
