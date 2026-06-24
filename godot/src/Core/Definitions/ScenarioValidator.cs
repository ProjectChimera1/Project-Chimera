#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core; // Faction, FactionRegistry, BuildingType

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The single fail-closed pre-tick gate (Story 1.7, AR-39). Every scenario entry path funnels a
    /// <see cref="ScenarioData"/> through <see cref="Validate"/> before it is applied to the simulation. On
    /// success it mints a <see cref="Validated{T}"/> (the proof-of-validation token); on the FIRST failed check
    /// it returns a located <see cref="ValidationResult"/> error (field path + offending value). It is pure: it
    /// NEVER throws and NEVER logs — the presentation call site decides shadow vs fail-closed policy
    /// (<see cref="ScenarioGate"/>). Godot-free (src/Core/Definitions), so it compiles into the Tier-1 test
    /// assembly and the AOT-eligible sim layer.
    ///
    /// The model is the as-built <see cref="ScenarioData"/> (still <c>float</c>-based in 1.7). The validator
    /// replicates the finiteness/range checks the <see cref="FixedJsonConverter"/> would do (the model does not
    /// route through that converter today); when D3 later converts the model to <see cref="Fixed"/>, those
    /// become redundant. A distinct canonical <c>ScenarioModel</c> is NOT introduced here (D2).
    /// </summary>
    public sealed class ScenarioValidator
    {
        /// <summary>
        /// Mint token for <see cref="Validated{T}"/>. Public TYPE (so <see cref="Validated{T}"/> can name it as
        /// a ctor param) with an <c>internal</c> CONSTRUCTOR (so only this sim assembly can construct one).
        ///
        /// NOTE (D1 correction): the story's D1 proposed a PRIVATE Proof ctor on the premise that "the enclosing
        /// type can call its nested type's private constructor" — that is FALSE in C# (it raises CS0122; the
        /// access rule is one-directional: a nested type sees the enclosing type's privates, not the reverse).
        /// The equivalent guarantee is <c>internal</c> ctor + the belt-and-suspenders source scan
        /// (ValidatedSoleMinterTest) that fails the build if any <c>new Validated&lt;</c> appears outside this
        /// file. Together: nothing outside the assembly can mint, and inside the assembly only this validator does.
        /// </summary>
        public sealed class Proof { internal Proof() { } }

        // The validator's single proof token, reused on every successful Validate. The ONLY `new Validated<` in
        // the codebase is below in Validate (guarded by ValidatedSoleMinterTest).
        private static readonly Proof _proof = new Proof();

        /// <summary>
        /// The 16.16 representable range limit (mirrors <see cref="FixedJsonConverter"/>'s FixedRangeLimit).
        /// Valid values satisfy [-Range, Range): -32768 is exactly representable (raw int.MinValue); +32768 and
        /// beyond overflow <c>Fixed.FromFloat</c>'s (int)(value*65536) cast and wrap.
        /// </summary>
        private const float Range = 32768f;

        // Exact set of BuildingType NAMES the applier (MainScene.ParseBuildingType) recognizes. Cached so the
        // building-type check allocates nothing. Validating by name (not Enum.TryParse, which also accepts
        // numeric strings like "5") matches how scenario JSON is authored and rejects unknown names instead of
        // silently defaulting them to CommandCenter the way the applier does (D4).
        private static readonly string[] _buildingTypeNames = Enum.GetNames(typeof(BuildingType));

        /// <summary>
        /// Validate a scenario model. Returns <see cref="ValidationResult.Pass"/> with a minted
        /// <see cref="Validated{T}"/> on success, or <see cref="ValidationResult.Fail"/> with a located error on
        /// the first failed check. Pure — never throws, never logs.
        /// </summary>
        public ValidationResult Validate(ScenarioData m)
        {
            if (m is null) return ValidationResult.Fail("scenario is null.");

            // ── Map bounds: finite, > 0, and inside the Fixed range (it is a coordinate ceiling) ──
            if (!Finite(m.MapBounds) || m.MapBounds <= 0f)
                return ValidationResult.Fail($"scenario.map_bounds={m.MapBounds} must be finite and > 0.");
            if (m.MapBounds >= Range)
                return ValidationResult.Fail(
                    $"scenario.map_bounds={m.MapBounds} exceeds the 16.16 range [0, {Range}).");

            float bounds = m.MapBounds;

            // ── Collections must be present. A null array is malformed input the applier would NRE on, so the
            // validator rejects it (located) rather than silently treating it as empty via the `?? Array.Empty`
            // guards below — those are then belt-and-suspenders. [Story 1.7 review patch] ──
            if (m.PlayerSlots is null)   return ValidationResult.Fail("scenario.player_slots is null.");
            if (m.ResourceNodes is null) return ValidationResult.Fail("scenario.resource_nodes is null.");
            if (m.Buildings is null)     return ValidationResult.Fail("scenario.buildings is null.");
            if (m.Units is null)         return ValidationResult.Fail("scenario.units is null.");

            // ── Player slots: range / non-negative ore / in-bounds base / engine ceiling / uniqueness ──
            // declared = the set of slots a PlayerSlot actually declares; buildings/units must reference one of
            // these or they are dangling.
            ScenarioPlayerSlot[] slots = m.PlayerSlots ?? Array.Empty<ScenarioPlayerSlot>();
            var declared = new HashSet<int>();
            for (int i = 0; i < slots.Length; i++)
            {
                ScenarioPlayerSlot s = slots[i];

                if (s.Slot < 0 || s.Slot >= FactionRegistry.PLAYER_COUNT)
                    return ValidationResult.Fail(
                        $"scenario.player_slots[{i}].slot={s.Slot} is out of [0,{FactionRegistry.PLAYER_COUNT}).");

                // The AR-39 length-5 overflow guard: the as-built Faction enum tops at Player4, so FactionRegistry
                // .ToFaction(slot) is only defined for slot <= 3. A slot in [4,8) is < PLAYER_COUNT but overflows
                // the [5] per-faction arrays. This relaxes automatically when Story 9.2 extends Faction to Player8.
                if (s.Slot + 1 > (int)Faction.Player4)
                    return ValidationResult.Fail(
                        $"scenario.player_slots[{i}].slot={s.Slot} maps to an undefined Faction " +
                        $"(engine ceiling: slot <= {(int)Faction.Player4 - 1}).");

                if (!declared.Add(s.Slot))
                    return ValidationResult.Fail(
                        $"scenario.player_slots[{i}].slot={s.Slot} is a duplicate.");

                string? e = CheckNonNeg($"scenario.player_slots[{i}].start_ore", s.StartOre)
                         ?? CheckCoord($"scenario.player_slots[{i}].base_x", s.BaseX, bounds)
                         ?? CheckCoord($"scenario.player_slots[{i}].base_z", s.BaseZ, bounds);
                if (e != null) return ValidationResult.Fail(e);
            }

            // ── Resource nodes: in-bounds position, non-negative supply/rate, non-negative gatherer cap ──
            ScenarioResourceNode[] nodes = m.ResourceNodes ?? Array.Empty<ScenarioResourceNode>();
            for (int i = 0; i < nodes.Length; i++)
            {
                ScenarioResourceNode n = nodes[i];
                string? e = CheckCoord($"scenario.resource_nodes[{i}].x", n.X, bounds)
                         ?? CheckCoord($"scenario.resource_nodes[{i}].z", n.Z, bounds)
                         ?? CheckNonNeg($"scenario.resource_nodes[{i}].supply", n.Supply)
                         ?? CheckNonNeg($"scenario.resource_nodes[{i}].rate", n.Rate);
                if (e != null) return ValidationResult.Fail(e);
                if (n.MaxGatherers < 0)
                    return ValidationResult.Fail(
                        $"scenario.resource_nodes[{i}].max_gatherers={n.MaxGatherers} must be >= 0.");
            }

            // ── Buildings: in-bounds position, slot references a declared PlayerSlot, known building type ──
            ScenarioBuilding[] buildings = m.Buildings ?? Array.Empty<ScenarioBuilding>();
            for (int i = 0; i < buildings.Length; i++)
            {
                ScenarioBuilding b = buildings[i];
                string? e = CheckCoord($"scenario.buildings[{i}].x", b.X, bounds)
                         ?? CheckCoord($"scenario.buildings[{i}].z", b.Z, bounds);
                if (e != null) return ValidationResult.Fail(e);
                if (!declared.Contains(b.Slot))
                    return ValidationResult.Fail(
                        $"scenario.buildings[{i}].slot={b.Slot} references no declared player_slot.");
                if (!IsKnownBuildingType(b.Type))
                    return ValidationResult.Fail(
                        $"scenario.buildings[{i}].type='{b.Type}' is not a known BuildingType.");
            }

            // ── Units: in-bounds position, slot references a declared PlayerSlot ──
            ScenarioUnit[] units = m.Units ?? Array.Empty<ScenarioUnit>();
            for (int i = 0; i < units.Length; i++)
            {
                ScenarioUnit u = units[i];
                string? e = CheckCoord($"scenario.units[{i}].x", u.X, bounds)
                         ?? CheckCoord($"scenario.units[{i}].z", u.Z, bounds);
                if (e != null) return ValidationResult.Fail(e);
                if (!declared.Contains(u.Slot))
                    return ValidationResult.Fail(
                        $"scenario.units[{i}].slot={u.Slot} references no declared player_slot.");
            }

            // AR-13 (forbidden-until-SimRng) — RESERVED, intentionally NOT implemented here. SimRng shipped in
            // Story 1.5 and is unconditionally present (EntityWorld.Rng, non-null, no flag), and no effect/ability
            // schema exists yet (Epic 2). The rule's failing condition ("SimRng absent") can never occur and there
            // is no random-effect model to inspect, so adding a presence check would be unreachable scaffolding.
            // This validator OWNS the rule's home and discharges AR-13 by reservation; the mature form ("a random
            // effect is valid only if it draws from world.Rng") is a static check over the effect graph, enforced
            // by Epic 2's effect-validator (Story 2.3) — the first point an effect schema exists.

            return ValidationResult.Pass(new Validated<ScenarioData>(m, _proof));
        }

        // ── Helpers (return a located error string, or null when the field is OK) ──

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>In the 16.16 representable range [-Range, Range) and finite — mirrors FixedJsonConverter.</summary>
        private static bool InRange(float v) => Finite(v) && v >= -Range && v < Range;

        /// <summary>Coordinate check: finite + in 16.16 range + within ±map_bounds.</summary>
        private static string? CheckCoord(string path, float v, float bounds)
        {
            if (!InRange(v))
                return $"{path}={v} is non-finite or outside the 16.16 range [-{Range}, {Range}).";
            if (v < -bounds || v > bounds)
                return $"{path}={v} is outside map_bounds (±{bounds}).";
            return null;
        }

        /// <summary>Non-negative scalar check: finite + in 16.16 range + &gt;= 0.</summary>
        private static string? CheckNonNeg(string path, float v)
        {
            if (!InRange(v))
                return $"{path}={v} is non-finite or outside the 16.16 range [-{Range}, {Range}).";
            if (v < 0f)
                return $"{path}={v} must be >= 0.";
            return null;
        }

        /// <summary>True only for an EXACT BuildingType enum name (case-sensitive); rejects numeric strings.</summary>
        private static bool IsKnownBuildingType(string? type)
        {
            if (type is null) return false;
            for (int i = 0; i < _buildingTypeNames.Length; i++)
                if (_buildingTypeNames[i] == type) return true;
            return false;
        }
    }
}
