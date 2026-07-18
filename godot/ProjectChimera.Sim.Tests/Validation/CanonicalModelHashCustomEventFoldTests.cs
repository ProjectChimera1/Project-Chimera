#nullable enable
using ProjectChimera.Core;              // Fixed, HeroStore
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;               // DslValueType
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 7.5 (landed via the 7-5 re-land merge) — the <see cref="ScenarioData.CustomEvents"/> registry FOLDS
    /// into <see cref="CanonicalModelHash"/> (v10) and therefore moves the <see cref="StartStateHash"/> seed.
    /// 7.5's original design EXCLUDED the declarations (at v7, on the then-true Triggers/Variables basis), but
    /// that basis dissolved when 7.7's v8 folded the full trigger/DSL sim-semantic model — on the merged tree a
    /// divergent registry must reject at the lobby handshake instead of desyncing in-sim (or failing at the
    /// validator on one peer only). These are the SENSITIVITY teeth: every declaration dimension must move the
    /// hash, null must stay ≡ empty, and the serializer chokepoint must keep event-less scenarios byte-identical
    /// to pre-7.5. The LIVE pending next-tick queue still folds into per-tick SimChecksum (v18), not here.
    /// </summary>
    public class CanonicalModelHashCustomEventFoldTests
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

        private static ScenarioData WithDeclared()
        {
            var m = BaseModel();
            m.CustomEvents = Declared();
            return m;
        }

        [Fact]
        public void AlgoVersions_Unchanged() // 10 canonical (this fold) / 2 start-state (value moves via the seed)
        {
            Assert.Equal(12, CanonicalModelHash.AlgoVersion);
            Assert.Equal(2, StartStateHash.AlgoVersion);
        }

        [Fact]
        public void AddingCustomEvents_ChangesTheCanonicalHash()
        {
            Assert.NotEqual(CanonicalModelHash.Compute(BaseModel()), CanonicalModelHash.Compute(WithDeclared()));
        }

        [Fact]
        public void EachDeclarationDimension_MovesTheCanonicalHash()
        {
            ulong baseline = CanonicalModelHash.Compute(WithDeclared());

            var renamed = WithDeclared();
            renamed.CustomEvents![1].Name = "glut_stack2";
            Assert.NotEqual(baseline, CanonicalModelHash.Compute(renamed));

            var paramRenamed = WithDeclared();
            paramRenamed.CustomEvents![0].Params![0].Name = "count2";
            Assert.NotEqual(baseline, CanonicalModelHash.Compute(paramRenamed));

            var paramRetyped = WithDeclared();
            paramRetyped.CustomEvents![0].Params![1].Type = DslValueType.Bool;
            Assert.NotEqual(baseline, CanonicalModelHash.Compute(paramRetyped));

            var raiserChanged = WithDeclared();
            raiserChanged.CustomEvents![0].AllowedRaisers = new[] { 0, 2 };
            Assert.NotEqual(baseline, CanonicalModelHash.Compute(raiserChanged));

            var reordered = WithDeclared();
            (reordered.CustomEvents![0], reordered.CustomEvents[1]) = (reordered.CustomEvents[1], reordered.CustomEvents[0]);
            Assert.NotEqual(baseline, CanonicalModelHash.Compute(reordered)); // declaration order is canonical
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
        public void AddingCustomEvents_ChangesTheStartStateHash()
        {
            var heroes = new HeroStore(); // empty → no hero rows folded; the canonical seed carries the change
            Assert.NotEqual(StartStateHash.Compute(BaseModel(), heroes), StartStateHash.Compute(WithDeclared(), heroes));
        }

        [Fact]
        public void EmptyCustomEvents_NormalizeToNull_AtTheSerializerChokepoint()
        {
            // The Variables persistence pattern: an event-less scenario serializes byte-identically to pre-7.5
            // (no key emitted), and the caller's model is observably unchanged after Serialize (the 7.7
            // ShallowClone discipline — normalization happens on the copy, never the caller's instance).
            var m = BaseModel();
            string absent = ScenarioSerializer.Serialize(m);
            m.CustomEvents = System.Array.Empty<ScenarioCustomEvent>();
            string emptied = ScenarioSerializer.Serialize(m);
            Assert.Equal(absent, emptied);
            Assert.NotNull(m.CustomEvents); // Serialize never mutates the model
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
