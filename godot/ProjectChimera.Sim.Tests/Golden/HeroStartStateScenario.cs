#nullable enable
using ProjectChimera.Core;              // HeroStore, HeroId, Fixed
using ProjectChimera.Core.Definitions;  // ScenarioData, StartStateHash

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 3.2 (AC2 / AC3 / Task 5) — the fixed HERO START-STATE fixture whose <see cref="StartStateHash"/> the
    /// hero-start-state golden pins (the "recorded model-layout pin" — the SECOND of AC3's two pins; the first is the
    /// independent-FNV pin in <c>StartStateHashTests</c>). A Godot-free builder (mirrors <c>ShiftQueueScenario</c>'s
    /// builder role) producing a deterministic (applied scenario model + <see cref="HeroStore"/> with minted heroes).
    ///
    /// The model leaves Player2's slot content minimal and depends on NOTHING float-AI, so the pinned hash is
    /// CROSS-PLATFORM SAFE (every folded field is <c>int</c> / <c>Fixed.Raw</c>) — compared on both CI legs, NOT
    /// Windows-gated. The two heroes carry distinct identities, levels, and XP so the golden has teeth against a
    /// roster / level / XP change.
    /// </summary>
    public static class HeroStartStateScenario
    {
        /// <summary>The applied content model (2 slots, 1 node, 1 pre-built building) — the CanonicalModelHash seed.</summary>
        public static ScenarioData BuildModel() => new ScenarioData
        {
            Id = "hero-start-state", DisplayName = "Hero Start State Fixture",
            TerrainRef = "res://terrain.tres", MapBounds = 120f,
            WinCondition = WinCondition.EliminateAllUnits,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, StartCrystal = 50f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 150f, StartCrystal = 30f, BaseX =  45f, BaseZ = 0f },
            },
            ResourceNodes = new[]
            {
                new ScenarioResourceNode { X = -20f, Z = -10f, Supply = 400f, Rate = 5f, MaxGatherers = 4 },
            },
            Buildings = new[]
            {
                new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -45f, Z = 0f, PreBuilt = true },
            },
        };

        /// <summary>The persistent hero init state: two heroes at fixed identity / level / XP (a stable fixture).</summary>
        public static HeroStore BuildHeroes()
        {
            var s = new HeroStore();
            s.Mint(new HeroId(1_000_000_007UL), entityId: 3, level: 4, xp: Fixed.FromInt(250));
            s.Mint(new HeroId(2_000_000_011UL), entityId: 8, level: 1, xp: Fixed.FromInt(0));
            return s;
        }

        /// <summary>Compute the fixture's start-state hash (the value the golden pins).</summary>
        public static ulong Compute() => StartStateHash.Compute(BuildModel(), BuildHeroes());
    }
}
