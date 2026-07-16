#nullable enable
using ProjectChimera.Core;              // Fixed, HeroStore
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;               // DslValueType
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 7.5 — the new <see cref="ScenarioData.CustomEvents"/> registry is EXCLUDED from
    /// <see cref="CanonicalModelHash"/> / <see cref="StartStateHash"/> on the SAME basis as the 7.3
    /// Variables/Timers/TriggerGraphJson declarations (the authoritative handshake fold is 7.7/later; only the
    /// LIVE pending next-tick queue folds, into SimChecksum v17). Cloned from
    /// <see cref="CanonicalModelHashDeclarationExclusionTests"/>: adding/changing/removing custom events must move
    /// NEITHER hash — an accidental early fold (ahead of the planned versioned 7.7 fold) turns these RED instead
    /// of silently changing the MP handshake for declaring scenarios (the story's Block-If).
    /// </summary>
    public class CanonicalModelHashCustomEventExclusionTests
    {
        private static ScenarioData BaseModel() => new ScenarioData
        {
            Id = "m", DisplayName = "M", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json" } },
        };

        private static ScenarioCustomEvent[] Declared() => new[]
        {
            new ScenarioCustomEvent
            {
                Name = "wave_start",
                Params = new[]
                {
                    new ScenarioEventParam { Name = "count", Type = DslValueType.Int },
                    new ScenarioEventParam { Name = "rate",  Type = DslValueType.Fixed },
                },
                AllowedRaisers = new[] { 0, 1 },
            },
            new ScenarioCustomEvent { Name = "glut_stack" },
        };

        [Fact]
        public void AlgoVersions_Unchanged() // 7 canonical / 2 start-state — 7.5 folds NOTHING into either hash
        {
            Assert.Equal(7, CanonicalModelHash.AlgoVersion);
            Assert.Equal(2, StartStateHash.AlgoVersion);
        }

        [Fact]
        public void AddingCustomEvents_DoesNotChangeCanonicalHash()
        {
            var without = BaseModel();
            var with = BaseModel();
            with.CustomEvents = Declared();
            Assert.Equal(CanonicalModelHash.Compute(without), CanonicalModelHash.Compute(with));
        }

        [Fact]
        public void ChangingADeclaredEvent_DoesNotChangeCanonicalHash()
        {
            // Divergent declarations that MATTER are caught by the SimChecksum v17 queue/value folds the first
            // tick they matter — the handshake stays declaration-blind until 7.7's versioned fold.
            var a = BaseModel();
            a.CustomEvents = new[] { new ScenarioCustomEvent { Name = "e", AllowedRaisers = new[] { 0 } } };
            var b = BaseModel();
            b.CustomEvents = new[]
            {
                new ScenarioCustomEvent
                {
                    Name = "e2",
                    Params = new[] { new ScenarioEventParam { Name = "p", Type = DslValueType.Bool } },
                    AllowedRaisers = new[] { 1 },
                },
            };
            Assert.Equal(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void NullAndEmptyCustomEvents_HashIdenticallyToOneAnother()
        {
            var nulls = BaseModel();   // CustomEvents null
            var empties = BaseModel();
            empties.CustomEvents = System.Array.Empty<ScenarioCustomEvent>();
            Assert.Equal(CanonicalModelHash.Compute(nulls), CanonicalModelHash.Compute(empties));
        }

        [Fact]
        public void AddingCustomEvents_DoesNotChangeStartStateHash()
        {
            var without = BaseModel();
            var with = BaseModel();
            with.CustomEvents = Declared();
            var heroes = new HeroStore(); // empty → no hero rows folded
            Assert.Equal(StartStateHash.Compute(without, heroes), StartStateHash.Compute(with, heroes));
        }

        [Fact]
        public void EmptyCustomEvents_NormalizeToNull_AtTheSerializerChokepoint()
        {
            // The Variables persistence pattern: an event-less scenario serializes byte-identically to pre-7.5
            // (no key emitted), and the caller's model is observably unchanged after Serialize.
            var m = BaseModel();
            string absent = ScenarioSerializer.Serialize(m);
            m.CustomEvents = System.Array.Empty<ScenarioCustomEvent>();
            string emptied = ScenarioSerializer.Serialize(m);
            Assert.Equal(absent, emptied);
            Assert.NotNull(m.CustomEvents); // restore-after discipline — Serialize never mutates the model
            Assert.DoesNotContain("custom_events", absent);

            m.CustomEvents = Declared();
            string declared = ScenarioSerializer.Serialize(m);
            Assert.Contains("custom_events", declared);
            // And a declared registry round-trips through the serializer (LoadFromFile — the disk entry point).
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"chimera-7-5-{System.Guid.NewGuid():N}.json");
            try
            {
                System.IO.File.WriteAllText(path, declared);
                ScenarioData? back = ScenarioSerializer.LoadFromFile(path);
                Assert.NotNull(back?.CustomEvents);
                Assert.Equal(2, back!.CustomEvents!.Length);
                Assert.Equal("wave_start", back.CustomEvents[0].Name);
                Assert.Equal(DslValueType.Fixed, back.CustomEvents[0].Params![1].Type);
                Assert.Equal(new[] { 0, 1 }, back.CustomEvents[0].AllowedRaisers);
                Assert.Equal(declared, ScenarioSerializer.Serialize(back)); // byte-stable re-serialization
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }
    }
}
