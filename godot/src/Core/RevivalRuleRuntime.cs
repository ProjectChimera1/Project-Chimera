#nullable enable
using ProjectChimera.Core.Definitions; // RevivalRule (authored source)

namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 3.14 — the sim-facing, <see cref="Fixed"/>-only resolution of a <see cref="RevivalRule"/>. Holds the
    /// revival cost/time curve parameters QUANTIZED ONCE (float→<see cref="Fixed"/>) at the single load boundary — never
    /// inside a tick (the determinism rule the Story 3.13 <c>PlacedHero</c> curve-capture / <c>ApplyUnitDefinition</c>
    /// boundary mirrors). Injected by reference into <see cref="ProjectChimera.Economy.BuildingSystem"/> (the revive
    /// order's level-scaled cost) and <see cref="ProjectChimera.Combat.HeroXpSystem"/> (the countdown time + respawn HP
    /// fraction + the master enable branch).
    ///
    /// <para>NOT a <c>readonly struct</c> even though the Code Map suggested one: the sim systems are constructed ONCE
    /// (in <c>SimulationHost</c>) BEFORE any scenario is applied, so an authored <c>revival_rule</c> that arrives later
    /// must reach the already-wired systems. A shared mutable instance the systems hold by reference — reconfigured in
    /// place by <see cref="Configure"/> when a scenario is applied (mirroring <c>BuildingSystem.SetFactionDef</c>) — is
    /// the coherent way to do that. Cost/time scale LINEARLY with hero level: <c>base + perLevel × Level</c>.</para>
    ///
    /// <para>NOT folded into <see cref="SimChecksum"/>: authored/def-derived constants (the <c>Delivery</c>/curve-
    /// constant posture — a divergence surfaces transitively via <c>HeroStore.RevivalTimer</c>/<c>Level</c>/<c>Xp</c>).</para>
    /// </summary>
    public sealed class RevivalRuleRuntime
    {
        /// <summary>Whether revival is enabled for this scenario (the master toggle). When false a fallen hero leaves the
        /// field like any unit (no awaiting state); its <c>HeroStore</c> row stays Alive so persistence still finalizes.</summary>
        public bool Enabled { get; private set; }

        private Fixed _costOreBase;
        private Fixed _costOrePerLevel;
        private Fixed _costCrystalBase;
        private Fixed _costCrystalPerLevel;
        private Fixed _timeBaseSeconds;
        private Fixed _timePerLevelSeconds;

        /// <summary>Fraction of max HP the revived hero respawns with (validator-gated to (0, 1]).</summary>
        public Fixed HpFraction { get; private set; }

        /// <summary>Resolve <paramref name="rule"/> (or <see cref="RevivalRule.Default"/> when null) into Fixed at
        /// construction — the single float→Fixed boundary.</summary>
        public RevivalRuleRuntime(RevivalRule? rule = null) => Configure(rule);

        /// <summary>Re-resolve from <paramref name="rule"/> (null ⇒ <see cref="RevivalRule.Default"/>) IN PLACE, so the
        /// already-wired sim systems (which hold this instance by reference) see the applied scenario's rule. Called by
        /// the scenario-apply path. The float→Fixed quantize happens HERE, never inside a tick.</summary>
        public void Configure(RevivalRule? rule)
        {
            RevivalRule r = rule ?? RevivalRule.Default;
            Enabled              = r.Enabled;
            _costOreBase         = Fixed.FromFloat(r.CostOreBase);
            _costOrePerLevel     = Fixed.FromFloat(r.CostOrePerLevel);
            _costCrystalBase     = Fixed.FromFloat(r.CostCrystalBase);
            _costCrystalPerLevel = Fixed.FromFloat(r.CostCrystalPerLevel);
            _timeBaseSeconds     = Fixed.FromFloat(r.TimeBaseSeconds);
            _timePerLevelSeconds = Fixed.FromFloat(r.TimePerLevelSeconds);
            HpFraction           = Fixed.FromFloat(r.ReviveHpFraction);
        }

        /// <summary>Ore cost to revive a hero at <paramref name="level"/> = <c>base + perLevel × level</c>.</summary>
        public Fixed CostOre(int level)     => LinearSat(_costOreBase,     _costOrePerLevel,     level);
        /// <summary>Crystal cost to revive a hero at <paramref name="level"/> = <c>base + perLevel × level</c>.</summary>
        public Fixed CostCrystal(int level) => LinearSat(_costCrystalBase, _costCrystalPerLevel, level);
        /// <summary>Countdown seconds to revive a hero at <paramref name="level"/> = <c>base + perLevel × level</c>.</summary>
        public Fixed TimeSeconds(int level) => LinearSat(_timeBaseSeconds, _timePerLevelSeconds, level);

        /// <summary><c>base + perLevel × level</c>, computed in widened <c>long</c> raw units and SATURATED to
        /// <c>[0, ~32767)</c> so an unvalidated/hand-edited curve can never overflow 16.16 <see cref="Fixed"/> and wrap
        /// NEGATIVE (the Story 3.13 overflow-class defense — a negative cost would defeat <c>CanAfford</c> and ADD
        /// resources; a negative timer would revive instantly). <c>perLevel × level</c> is exact in raw because
        /// <c>(perLevel.Raw &lt;&lt; 16 → ×level)</c> stays a per-level integer scaling of the raw. The primary gate is
        /// <c>ScenarioValidator</c> (fail-closed at authoring); this is defense-in-depth.</summary>
        private static Fixed LinearSat(Fixed baseVal, Fixed perLevel, int level)
        {
            if (level < 0) level = 0;
            long raw = (long)baseVal.Raw + (long)perLevel.Raw * level;
            long maxRaw = (long)Fixed.FromInt(32767).Raw; // just under the 16.16 ceiling
            if (raw < 0) raw = 0;
            else if (raw > maxRaw) raw = maxRaw;
            return Fixed.FromRaw((int)raw);
        }
    }
}
