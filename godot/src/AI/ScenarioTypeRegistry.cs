#nullable enable
using System;
using System.Collections.Generic;

namespace ProjectChimera.AI
{
    /// <summary>
    /// DW-371 — the closed set of scenario types (game modes) the Map Generator can author for.
    ///
    /// Story 8.3 parameterized the three RTS map clamps (<see cref="MapGeneratorContext.MinPlayerSlots"/>,
    /// <see cref="MapGeneratorContext.MaxCombatUnitsPerSlot"/>, <see cref="MapGeneratorContext.FactionJsonResolver"/>)
    /// onto the TRUSTED <see cref="MapGeneratorContext"/>, but left NO caller able to relax them — so a non-RTS
    /// scenario was still wrongly rejected. This enum is the missing selector: each member names a scenario type,
    /// <see cref="ScenarioTypeRegistry"/> maps it to a trusted clamp preset, and the Map Generator panel offers the
    /// choice.
    ///
    /// This is an IN-MEMORY, EDITOR-SIDE selector only: it is NEVER written into a scenario file and NEVER read back
    /// out of one (the Story 8.3 spec's "Never" constraint — a clamp sourced from the untrusted file it gates would
    /// be circular validation). Explicit values so a future persisted schema could reuse them without renumbering.
    /// </summary>
    public enum ScenarioType
    {
        /// <summary>Classic base-building skirmish — the historical default. Its preset reproduces the
        /// pre-registry clamp behavior byte-for-byte.</summary>
        Rts = 0,

        /// <summary>Free-for-all melee: every slot is its own player, factions alternate so any slot count is
        /// resolvable.</summary>
        FreeForAll = 1,

        /// <summary>Co-operative: the human team shares the even slots and faces a scripted/AI defence on the odd
        /// ones, which needs a bigger pre-placed force than a 1v1 opening.</summary>
        Coop = 2,

        /// <summary>Arena battle: the fight is decided by pre-placed armies rather than economy, so the per-slot
        /// combat-unit clamp is much wider.</summary>
        Arena = 3,

        /// <summary>Survival / horde defence: a single defending player slot is legal, and the hostile force is
        /// large.</summary>
        Survival = 4,

        /// <summary>Sandbox / showcase: one faction, one slot, everything relaxed — used to stage a faction's
        /// roster on a map without an opponent.</summary>
        Sandbox = 5,
    }

    /// <summary>
    /// DW-371 — how a scenario type maps a 0-based player slot to a TRUSTED faction-JSON path. The policy is
    /// resolved into a <see cref="MapGeneratorContext.FactionJsonResolver"/> by
    /// <see cref="ScenarioTypeRegistry.Apply"/>; the untrusted scenario file never dictates a faction path.
    /// </summary>
    public enum FactionPathPolicy
    {
        /// <summary>The historical RTS mapping: slot 0 → <see cref="MapGeneratorContext.Slot0FactionJson"/>, every
        /// other slot → <see cref="MapGeneratorContext.Slot1FactionJson"/>. Applied by leaving
        /// <see cref="MapGeneratorContext.FactionJsonResolver"/> NULL, so the RTS preset is provably identical to
        /// the pre-registry behavior rather than merely equivalent-looking.</summary>
        RtsDefault = 0,

        /// <summary>Alternating mirror: even slots → <see cref="MapGeneratorContext.Slot0FactionJson"/>, odd slots
        /// → <see cref="MapGeneratorContext.Slot1FactionJson"/>. TOTAL for any slot count — a 4-slot free-for-all
        /// gets mirrored 2v2 factions instead of collapsing slots 2/3 onto slot 1's faction.</summary>
        MirroredPair = 1,

        /// <summary>Every slot → <see cref="MapGeneratorContext.Slot0FactionJson"/>. For single-faction types
        /// (sandbox/showcase) where there is no opposing player faction to resolve.</summary>
        SingleFaction = 2,
    }

    /// <summary>
    /// DW-371 — the trusted per-scenario-type map-clamp preset: the values a selected
    /// <see cref="ScenarioType"/> writes onto a <see cref="MapGeneratorContext"/>, plus the human/model facing
    /// copy that goes with them. Immutable; the registry owns the only instances.
    /// </summary>
    public sealed class ScenarioTypePreset
    {
        internal ScenarioTypePreset(
            ScenarioType type,
            string displayName,
            string summary,
            int minPlayerSlots,
            int maxCombatUnitsPerSlot,
            FactionPathPolicy factionPaths,
            string promptGuidance)
        {
            Type                  = type;
            DisplayName           = displayName;
            Summary               = summary;
            MinPlayerSlots        = minPlayerSlots;
            MaxCombatUnitsPerSlot = maxCombatUnitsPerSlot;
            FactionPaths          = factionPaths;
            PromptGuidance        = promptGuidance;
        }

        /// <summary>The scenario type this preset configures.</summary>
        public ScenarioType Type { get; }

        /// <summary>Dropdown label for the Map Generator's scenario-type picker.</summary>
        public string DisplayName { get; }

        /// <summary>One-line human description shown under the picker.</summary>
        public string Summary { get; }

        /// <summary>Minimum player slots a generated scenario must declare under this type (validation FLOOR, not a
        /// target — a scenario may declare more).</summary>
        public int MinPlayerSlots { get; }

        /// <summary>Maximum pre-placed combat (non-worker) units allowed per slot under this type.</summary>
        public int MaxCombatUnitsPerSlot { get; }

        /// <summary>How this type resolves a slot index to a trusted faction-JSON path.</summary>
        public FactionPathPolicy FactionPaths { get; }

        /// <summary>Model-facing guidance appended to the map system prompt. EMPTY for
        /// <see cref="ScenarioType.Rts"/> so the RTS prompt is byte-for-byte what it was before the registry
        /// landed.</summary>
        public string PromptGuidance { get; }
    }

    /// <summary>
    /// DW-371 — the ScenarioType registry: a per-type map-clamp preset table plus the single
    /// <see cref="Apply"/> entry point that populates a TRUSTED <see cref="MapGeneratorContext"/> from a selected
    /// type. Pure C#, Godot-free (the Map Generator panel's picker is a thin shell over this table), so every
    /// preset and every clamp decision is Tier-1 testable.
    ///
    /// Contract:
    ///  • <see cref="ScenarioType.Rts"/> is the default and reproduces the pre-registry clamps exactly (min 2 slots,
    ///    max 6 combat units/slot, NULL resolver ⇒ the historical slot-0/slot-1 mapping).
    ///  • Every enum member has exactly one preset — <see cref="Get"/> throws on an unmapped/undefined value rather
    ///    than silently falling back to RTS clamps (a silent fallback would re-open the "non-RTS scenario wrongly
    ///    rejected" defect the registry exists to close).
    ///  • No preset raises <see cref="ScenarioTypePreset.MinPlayerSlots"/> above 2 while the map prompt's SCHEMA and
    ///    EXAMPLE blocks still hardcode two player slots (DW-372): the model would be told "at least N" but shown a
    ///    2-slot example and emit 2 → a guaranteed reject. Raising a floor is unlocked by DW-372, not by this table.
    ///    Pinned by <c>ScenarioTypeRegistryTests.EveryPreset_KeepsMinSlotsWithinThePromptExample</c>.
    /// </summary>
    public static class ScenarioTypeRegistry
    {
        // ── The preset table ──────────────────────────────────────────────────
        //
        // Declaration order IS the picker order; RTS first so the default is the top item.
        private static readonly ScenarioTypePreset[] PRESETS =
        {
            new ScenarioTypePreset(
                ScenarioType.Rts,
                "RTS skirmish (default)",
                "Classic base-building skirmish — mirrored bases, a small opening force.",
                minPlayerSlots: 2,
                maxCombatUnitsPerSlot: 6,
                FactionPathPolicy.RtsDefault,
                // EMPTY by design: the RTS prompt must stay byte-for-byte identical to the pre-registry prompt.
                promptGuidance: ""),

            new ScenarioTypePreset(
                ScenarioType.FreeForAll,
                "Free-for-all melee",
                "Every slot is its own player; factions alternate so any slot count resolves.",
                minPlayerSlots: 2,
                maxCombatUnitsPerSlot: 6,
                FactionPathPolicy.MirroredPair,
                promptGuidance:
                    "This is a FREE-FOR-ALL melee map: every player slot is a separate, mutually hostile player. " +
                    "Declare one player_slots entry per player with consecutive slot indices starting at 0, place " +
                    "their bases symmetrically around the map (not just left/right), and give every slot equal " +
                    "access to ore nodes."),

            new ScenarioTypePreset(
                ScenarioType.Coop,
                "Co-op vs. AI",
                "Human team on the even slots, a pre-placed AI defence on the odd ones.",
                minPlayerSlots: 2,
                maxCombatUnitsPerSlot: 12,
                FactionPathPolicy.MirroredPair,
                promptGuidance:
                    "This is a CO-OPERATIVE map: the even slots (0, 2, ...) are the human team and share a start " +
                    "corner; the odd slots are the AI defence. Give the AI slots a larger pre-placed garrison than " +
                    "the human slots, and put the contested ore nodes on the AI side of the map."),

            new ScenarioTypePreset(
                ScenarioType.Arena,
                "Arena battle",
                "Pre-placed armies decide the fight; economy is minimal.",
                minPlayerSlots: 2,
                maxCombatUnitsPerSlot: 30,
                FactionPathPolicy.MirroredPair,
                promptGuidance:
                    "This is an ARENA map: the outcome is decided by the PRE-PLACED armies, not by economy. Keep " +
                    "the economy minimal (few ore nodes, low supply), place the two forces facing each other across " +
                    "an open centre, and give each slot a large, mirrored pre-placed army."),

            new ScenarioTypePreset(
                ScenarioType.Survival,
                "Survival / horde defence",
                "One defending player slot; a large hostile force on the opposing slot.",
                minPlayerSlots: 1,
                maxCombatUnitsPerSlot: 40,
                FactionPathPolicy.MirroredPair,
                promptGuidance:
                    "This is a SURVIVAL map: slot 0 is the lone defender and slot 1 (when declared) is the hostile " +
                    "horde. Cluster the defender's base and ore in one defensible pocket, mass the hostile force at " +
                    "the map edge, and leave the approach open so the horde can reach the defender."),

            new ScenarioTypePreset(
                ScenarioType.Sandbox,
                "Sandbox / showcase",
                "A single faction on a single slot — stage a roster with no opponent.",
                minPlayerSlots: 1,
                maxCombatUnitsPerSlot: 40,
                FactionPathPolicy.SingleFaction,
                promptGuidance:
                    "This is a SANDBOX showcase map: ONE player slot, ONE faction, no opponent. Lay the map out as " +
                    "an open display area with plenty of room around the pre-placed units and generous ore."),
        };

        /// <summary>Every preset, in picker order (RTS first). One entry per <see cref="ScenarioType"/> member.</summary>
        public static IReadOnlyList<ScenarioTypePreset> All => PRESETS;

        /// <summary>The type a fresh <see cref="MapGeneratorContext"/> is already configured for.</summary>
        public static ScenarioType DefaultType => ScenarioType.Rts;

        // ── Lookup ────────────────────────────────────────────────────────────

        /// <summary>Look up <paramref name="type"/>'s preset without throwing. Returns false (and a null preset)
        /// for an undefined/unmapped value.</summary>
        public static bool TryGet(ScenarioType type, out ScenarioTypePreset? preset)
        {
            for (int i = 0; i < PRESETS.Length; i++)
                if (PRESETS[i].Type == type)
                {
                    preset = PRESETS[i];
                    return true;
                }

            preset = null;
            return false;
        }

        /// <summary>Look up <paramref name="type"/>'s preset, throwing on an undefined/unmapped value. Deliberately
        /// fail-loud: silently defaulting an unknown type to RTS clamps would re-open the exact "non-RTS scenario
        /// wrongly rejected" defect this registry closes.</summary>
        public static ScenarioTypePreset Get(ScenarioType type)
            => TryGet(type, out var preset) && preset != null
                ? preset
                : throw new ArgumentOutOfRangeException(nameof(type), type,
                    "No ScenarioTypeRegistry preset is registered for this scenario type.");

        // ── Application ───────────────────────────────────────────────────────

        /// <summary>
        /// Populate <paramref name="context"/>'s three TRUSTED map clamps (min player slots, max pre-placed combat
        /// units per slot, per-slot faction-path resolver) from <paramref name="type"/>'s preset, and record the
        /// selected type on the context so the map prompt can carry the type's guidance. Returns the same context
        /// for chaining.
        ///
        /// Only the clamp fields are touched — <see cref="MapGeneratorContext.UnitIds"/>,
        /// <see cref="MapGeneratorContext.MapBounds"/> and the two faction-JSON paths are the caller's (the boot
        /// phase seeds them from the loaded scenario) and are left exactly as they were. Re-applying is idempotent,
        /// and applying <see cref="ScenarioType.Rts"/> restores the historical defaults exactly (including NULLING a
        /// resolver a previous selection installed), so a user toggling the picker can never leave a stale clamp
        /// behind.
        /// </summary>
        public static MapGeneratorContext Apply(MapGeneratorContext context, ScenarioType type)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var preset = Get(type);
            context.ScenarioType          = type;
            context.MinPlayerSlots        = preset.MinPlayerSlots;
            context.MaxCombatUnitsPerSlot = preset.MaxCombatUnitsPerSlot;
            context.FactionJsonResolver   = BuildResolver(context, preset.FactionPaths);
            return context;
        }

        /// <summary>Build the per-slot faction-JSON resolver for <paramref name="policy"/>, closing over
        /// <paramref name="context"/> so a later edit of its faction paths is honored. Returns NULL for
        /// <see cref="FactionPathPolicy.RtsDefault"/> — the historical mapping IS
        /// <see cref="MapGeneratorContext.ResolveFactionJson"/>'s null branch, so RTS re-uses it rather than
        /// re-implementing (and possibly drifting from) it.</summary>
        private static Func<int, string>? BuildResolver(MapGeneratorContext context, FactionPathPolicy policy)
            => policy switch
            {
                FactionPathPolicy.RtsDefault    => null,
                FactionPathPolicy.MirroredPair  => slot => slot % 2 == 0
                                                       ? context.Slot0FactionJson
                                                       : context.Slot1FactionJson,
                FactionPathPolicy.SingleFaction => _ => context.Slot0FactionJson,
                _ => throw new ArgumentOutOfRangeException(nameof(policy), policy,
                        "Unknown faction-path policy."),
            };

        // ── Copy ──────────────────────────────────────────────────────────────

        /// <summary>The model-facing guidance for <paramref name="type"/> — EMPTY for
        /// <see cref="ScenarioType.Rts"/> (the RTS prompt is unchanged by the registry).</summary>
        public static string PromptGuidance(ScenarioType type) => Get(type).PromptGuidance;

        /// <summary>One-line "what this type does to the gate" hint for the Map Generator picker — the same three
        /// clamp values <see cref="LLMService.ValidateScenario"/> will enforce, in plain language.</summary>
        public static string DescribeClamps(ScenarioType type)
        {
            var p = Get(type);
            string slots = p.MinPlayerSlots == 1 ? "1 player slot" : $"{p.MinPlayerSlots} player slots";
            return $"{p.Summary}  (min {slots}, max {p.MaxCombatUnitsPerSlot} pre-placed combat units/slot, " +
                   $"factions: {DescribeFactionPaths(p.FactionPaths)})";
        }

        /// <summary>Plain-language label for a <see cref="FactionPathPolicy"/>.</summary>
        public static string DescribeFactionPaths(FactionPathPolicy policy)
            => policy switch
            {
                FactionPathPolicy.RtsDefault    => "slot 0 vs. every other slot",
                FactionPathPolicy.MirroredPair  => "alternating (even vs. odd slots)",
                FactionPathPolicy.SingleFaction => "one faction on every slot",
                _ => throw new ArgumentOutOfRangeException(nameof(policy), policy,
                        "Unknown faction-path policy."),
            };
    }
}
