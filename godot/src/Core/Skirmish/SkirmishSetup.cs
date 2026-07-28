#nullable enable
using System.Collections.Generic;
using ProjectChimera.AI; // AiDifficulty (Godot-free; globbed into the Tier-1 sim assembly by SimSources.props)

namespace ProjectChimera.Core.Skirmish
{
    /// <summary>
    /// Story 11.1 — how a single player slot is configured on the skirmish setup screen. Pure, Godot-free config
    /// data shared by the UI, the transform, and the validator (no <c>using Godot;</c> — this folder is auto-globbed
    /// into the Tier-1 sim compile by <c>SimSources.props</c> and must stay Tier-1-testable).
    /// </summary>
    public enum SlotKind
    {
        /// <summary>An empty, joinable slot — no faction spawns for it (dropped by the transform).</summary>
        Open,
        /// <summary>A locked, unusable slot — no faction spawns for it (dropped by the transform).</summary>
        Closed,
        /// <summary>The local human player. Exactly one is required to launch.</summary>
        Human,
        /// <summary>An AI opponent piloted by the single existing <c>AiOpponentSystem</c> (one AI per match today).</summary>
        Ai,
    }

    /// <summary>
    /// Story 11.1 — one player slot's configuration. <see cref="Ai"/> is only consulted when <see cref="Kind"/> is
    /// <see cref="SlotKind.Ai"/>; <see cref="FactionId"/> is required for Human/Ai slots (validated). <see cref="Team"/>
    /// is the 9.14 team ordinal (0 = FFA / unassigned). Plain data — no behavior.
    /// </summary>
    public sealed class SetupSlot
    {
        /// <summary>0-based slot index (mirrors <c>ScenarioPlayerSlot.Slot</c>): 0 = Player1, 1 = Player2, …</summary>
        public int Slot { get; set; }

        /// <summary>What occupies this slot.</summary>
        public SlotKind Kind { get; set; } = SlotKind.Open;

        /// <summary>The AI difficulty when <see cref="Kind"/> is <see cref="SlotKind.Ai"/> (inert otherwise).</summary>
        public AiDifficulty Ai { get; set; } = AiDifficulty.Normal;

        /// <summary>The chosen faction id (must resolve in the discovered faction catalog for Human/Ai slots). Null/empty
        /// for Open/Closed slots.</summary>
        public string? FactionId { get; set; }

        /// <summary>The 9.14 team ordinal (0 = FFA / unassigned; slots sharing a positive ordinal are allied).</summary>
        public int Team { get; set; }
    }

    /// <summary>
    /// Story 11.1 — the full skirmish configuration the player assembles: a chosen map plus a per-slot configuration.
    /// The transform (<c>SkirmishSetupToScenario</c>) turns this + the base map into an in-memory <c>ScenarioData</c>,
    /// and the validator (<c>SkirmishSetupValidator</c>) gates Launch. Pure data — deterministic in, deterministic out.
    /// </summary>
    public sealed class SkirmishSetup
    {
        /// <summary>The chosen map's id (matches a discovered <c>MapEntry.Id</c>).</summary>
        public string MapId { get; set; } = "";

        /// <summary>The per-slot configuration (one entry per map start position).</summary>
        public List<SetupSlot> Slots { get; set; } = new();
    }
}
