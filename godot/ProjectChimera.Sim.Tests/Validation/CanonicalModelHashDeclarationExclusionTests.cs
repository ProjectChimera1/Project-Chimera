#nullable enable
using ProjectChimera.Core;              // Fixed, HeroStore
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;               // DslValueType / VarScope, TriggerGraph
using ProjectChimera.Effects;           // DirectHpDeltaEffect (a representative trigger_graph payload)
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 7.3 (review follow-up) — the new <see cref="ScenarioData.Variables"/> / <see cref="ScenarioData.Timers"/> /
    /// <see cref="ScenarioData.TriggerGraphJson"/> declarations are EXCLUDED from <see cref="CanonicalModelHash"/> /
    /// <see cref="StartStateHash"/> on the SAME basis as Triggers/Regions (the authoritative handshake fold is
    /// 7.7/later; only the LIVE per-tick DslVarTable values fold, into SimChecksum v16). The Regions precedent
    /// (<see cref="CanonicalModelHashRegionExclusionTests"/>) has explicit with/without-equality teeth; this gives the
    /// 7.3 exclusion contract the same teeth — an accidental early fold of any declaration field (ahead of the planned
    /// versioned 7.7 fold) turns these RED instead of silently changing the MP handshake for declaring scenarios.
    /// </summary>
    public class CanonicalModelHashDeclarationExclusionTests
    {
        private static ScenarioData BaseModel() => new ScenarioData
        {
            Id = "m", DisplayName = "M", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json" } },
        };

        private static void AddDeclarations(ScenarioData m)
        {
            m.Variables = new[]
            {
                new ScenarioVariable { Name = "score", Type = DslValueType.Int,   Scope = VarScope.PerPlayer, Initial = Fixed.FromInt(3) },
                new ScenarioVariable { Name = "rate",  Type = DslValueType.Fixed, Scope = VarScope.Global,    Initial = Fixed.FromFloat(2.5f) },
            };
            m.Timers = new[] { new ScenarioTimer { Name = "clock", Seconds = Fixed.FromInt(30) } };
            m.TriggerGraphJson = TriggerGraph
                .BuildRunEffectTrigger("t", "match_start", new DirectHpDeltaEffect(Fixed.FromInt(-1)))
                .ToCanonicalJson();
        }

        [Fact]
        public void AlgoVersions_Unchanged() // 7 canonical / 2 start-state — 7.3 folds NOTHING into either hash
        {
            Assert.Equal(7, CanonicalModelHash.AlgoVersion);
            Assert.Equal(2, StartStateHash.AlgoVersion);
        }

        [Fact]
        public void AddingDeclarations_DoesNotChangeCanonicalHash()
        {
            var without = BaseModel();
            var with = BaseModel();
            AddDeclarations(with);
            Assert.Equal(CanonicalModelHash.Compute(without), CanonicalModelHash.Compute(with));
        }

        [Fact]
        public void ChangingADeclaredInitial_DoesNotChangeCanonicalHash()
        {
            // Divergent declared INITIALS between peers are caught by the SimChecksum v16 value fold at tick 1 —
            // the handshake stays declaration-blind until 7.7's versioned fold.
            var a = BaseModel();
            a.Variables = new[] { new ScenarioVariable { Name = "v", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.FromInt(1) } };
            var b = BaseModel();
            b.Variables = new[] { new ScenarioVariable { Name = "v", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.FromInt(2) } };
            Assert.Equal(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void NullAndEmptyDeclarations_HashIdenticallyToOneAnother()
        {
            var nulls = BaseModel(); // Variables/Timers/TriggerGraphJson all null
            var empties = BaseModel();
            empties.Variables = System.Array.Empty<ScenarioVariable>();
            empties.Timers = System.Array.Empty<ScenarioTimer>();
            empties.TriggerGraphJson = "";
            Assert.Equal(CanonicalModelHash.Compute(nulls), CanonicalModelHash.Compute(empties));
        }

        [Fact]
        public void AddingDeclarations_DoesNotChangeStartStateHash()
        {
            var without = BaseModel();
            var with = BaseModel();
            AddDeclarations(with);
            var heroes = new HeroStore(); // empty → no hero rows folded
            Assert.Equal(StartStateHash.Compute(without, heroes), StartStateHash.Compute(with, heroes));
        }
    }
}
