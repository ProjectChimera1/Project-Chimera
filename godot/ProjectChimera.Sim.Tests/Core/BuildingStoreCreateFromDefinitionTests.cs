#nullable enable
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using ProjectChimera.Sim.Tests.Golden;   // ReflectionProbe (DW-218/DW-501: named reflection lookups)
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// DW-172 — the def→<see cref="BuildingStore.Create"/> stat threading (Hp / SupplyBonus / ConstructionTime /
    /// shop / revive) was hand-copied in TWO placement paths: <c>BuildingSystem.PlaceBuildingDirectById</c> (sim) and
    /// <c>EntityPlacer.CreateEditorBuilding</c> (editor). Both were correct, but they had ALREADY drifted
    /// cosmetically (nullable vs <c>Array.Empty</c> shop stock; <c>Fixed.Zero</c> vs <c>default</c> radius) — which is
    /// exactly how the "never hand-copied in a spawn path" defect class starts. The unit side has enforced the
    /// single-mapper rule since Story 1.12 (<c>EntityWorld.ApplyUnitDefinition</c>); this is the building equivalent.
    ///
    /// <para>The tests below pin the mapper's contract, prove the SIM path routes through it by comparing a real
    /// <c>PlaceBuildingDirectById</c> placement field-for-field against a direct
    /// <see cref="BuildingStore.CreateFromDefinition"/> call, and — because <c>EntityPlacer</c> is Godot-coupled and
    /// outside this assembly's compile set — pin the EDITOR path with a source scan (the DW-86/DW-626
    /// <c>CommandApplyParityTests</c> shape). Without that scan, re-inlining the mapping in the editor would leave the
    /// whole suite green while editor and sim placement silently drifted apart again.</para>
    /// </summary>
    public class BuildingStoreCreateFromDefinitionTests
    {
        private static FixedVec3 At(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>A fully-authored custom building: every field the mapper threads carries a DISTINCT value, so a
        /// dropped or swapped argument cannot pass by coincidence.</summary>
        private static BuildingDefinition RichDef() => new BuildingDefinition
        {
            Id = "arcane_bazaar", DisplayName = "Arcane Bazaar", Category = "Structure",
            MeshPath = "res://assets/bazaar.glb",
            Hp = 777f, SupplyBonus = 7, ConstructionTime = 21f,
            RevivesHeroes = true, SellsItems = true,
            ShopStock = new[] { "potion", "boots" }, ShopRadius = 9f,
        };

        private static FactionDefinition FactionWith(BuildingDefinition b)
        {
            var f = new FactionDefinition { Id = "f", DisplayName = "F" };
            f.Buildings.Add(b);
            return f;
        }

        // ── The mapper's own contract ───────────────────────────────────────────────────────────────────

        [Fact]
        public void CreateFromDefinition_ThreadsEveryDefDerivedField()
        {
            var store = new BuildingStore();
            BuildingDefinition def = RichDef();

            int id = store.CreateFromDefinition(def, At(5, -3), Faction.Player2, "arcane_bazaar");

            Assert.True(id >= 0);
            Assert.True(store.Alive[id]);
            Assert.Equal(At(5, -3), store.Position[id]);
            Assert.Equal(Faction.Player2, store.FactionOf[id]);
            Assert.Equal(BuildingType.Custom, store.Type[id]);        // no enum member for this authored id
            Assert.Equal("arcane_bazaar", store.DefinitionId[id]);
            Assert.Equal(Fixed.FromFloat(777f), store.Health[id]);
            Assert.Equal(Fixed.FromFloat(777f), store.MaxHealth[id]);
            Assert.Equal(7, store.SupplyBonus[id]);
            Assert.Equal(Fixed.FromFloat(21f), store.ConstructionDuration[id]);
            Assert.Equal(Fixed.FromFloat(21f), store.ConstructionTimer[id]);   // starts under construction
            Assert.True(store.RevivesHeroes[id]);
            Assert.True(store.SellsItems[id]);
            Assert.Equal(new[] { "potion", "boots" }, store.ShopStock[id]);
            Assert.Equal(Fixed.FromFloat(9f), store.ShopRadius[id]);
        }

        [Fact]
        public void CreateFromDefinition_NullDef_KeepsTheAuthoredId_AndFallsBackToThePerTypeSwitch()
        {
            // An unknown id is only reachable in shadow mode (the validator fails it closed), and it must degrade to
            // the switch defaults rather than throw — while STILL recording the authored id the nav/render buckets key on.
            var store = new BuildingStore();

            int builtIn = store.CreateFromDefinition(null, At(0, 0), Faction.Player1, "barracks");
            Assert.Equal(BuildingType.Barracks, store.Type[builtIn]);
            Assert.Equal(Fixed.FromFloat(300f), store.Health[builtIn]);      // the Barracks switch case
            Assert.Equal(0, store.SupplyBonus[builtIn]);
            Assert.False(store.RevivesHeroes[builtIn]);
            Assert.False(store.SellsItems[builtIn]);
            Assert.Empty(store.ShopStock[builtIn]);                          // never null — Create coalesces
            Assert.Equal(Fixed.Zero, store.ShopRadius[builtIn]);

            int custom = store.CreateFromDefinition(null, At(1, 1), Faction.Player1, "who_knows");
            Assert.Equal(BuildingType.Custom, store.Type[custom]);
            Assert.Equal("who_knows", store.DefinitionId[custom]);           // the authored id survives a null def
        }

        [Fact]
        public void CreateFromDefinition_PrefersTheDefsOwnId_OverTheLookupKey()
        {
            // The mapper records def.Id when one resolved (the pre-DW-172 behaviour of both call sites), so a lookup
            // by an alias can never persist a DefinitionId the def itself does not claim.
            var store = new BuildingStore();
            BuildingDefinition def = RichDef();

            int id = store.CreateFromDefinition(def, At(0, 0), Faction.Player1, "lookup_alias");

            Assert.Equal("arcane_bazaar", store.DefinitionId[id]);
        }

        // ── The SIM path really routes through it ───────────────────────────────────────────────────────

        [Theory]
        [InlineData("arcane_bazaar")]   // authored custom building — the resolved-stats path
        [InlineData("barracks")]        // built-in with no authored def — the switch-fallback path
        public void PlaceBuildingDirectById_AndTheMapper_ProduceIdenticalSlots(string buildingId)
        {
            BuildingDefinition def = RichDef();
            var sys = new BuildingSystem(new BuildingStore(), new ResourceStore(Fixed.Zero), FactionWith(def), null);
            int viaSystem = sys.PlaceBuildingDirectById(buildingId, Faction.Player1, At(-4, 8), preBuilt: false);

            var direct = new BuildingStore();
            int viaMapper = direct.CreateFromDefinition(
                buildingId == "arcane_bazaar" ? def : null, At(-4, 8), Faction.Player1, buildingId);

            BuildingStore viaSys = SystemStoreOf(sys);
            Assert.True(viaSystem >= 0 && viaMapper >= 0);
            AssertSameSlot(viaSys, viaSystem, direct, viaMapper);
        }

        /// <summary>Every SoA field <see cref="BuildingStore.CreateFromDefinition"/> is responsible for, compared
        /// across two stores. Hand-enumerated on purpose: a NEW def-derived field must be added here, which is the
        /// point of the guard.</summary>
        private static void AssertSameSlot(BuildingStore a, int ia, BuildingStore b, int ib)
        {
            Assert.Equal(a.Alive[ia], b.Alive[ib]);
            Assert.Equal(a.Position[ia], b.Position[ib]);
            Assert.Equal(a.FactionOf[ia], b.FactionOf[ib]);
            Assert.Equal(a.Type[ia], b.Type[ib]);
            Assert.Equal(a.DefinitionId[ia], b.DefinitionId[ib]);
            Assert.Equal(a.Health[ia], b.Health[ib]);
            Assert.Equal(a.MaxHealth[ia], b.MaxHealth[ib]);
            Assert.Equal(a.SupplyBonus[ia], b.SupplyBonus[ib]);
            Assert.Equal(a.ConstructionDuration[ia], b.ConstructionDuration[ib]);
            Assert.Equal(a.ConstructionTimer[ia], b.ConstructionTimer[ib]);
            Assert.Equal(a.RevivesHeroes[ia], b.RevivesHeroes[ib]);
            Assert.Equal(a.SellsItems[ia], b.SellsItems[ib]);
            Assert.Equal(a.ShopStock[ia], b.ShopStock[ib]);
            Assert.Equal(a.ShopRadius[ia], b.ShopRadius[ib]);
            Assert.Equal(a.RequiresBuilder[ia], b.RequiresBuilder[ib]);
        }

        /// <summary>The store a <see cref="BuildingSystem"/> was constructed over (it exposes no accessor). Routed
        /// through <see cref="ReflectionProbe"/> so a rename fails loud and NAMED (DW-218/DW-501) rather than as an
        /// opaque NullReferenceException at the use site.</summary>
        private static BuildingStore SystemStoreOf(BuildingSystem sys)
            => ReflectionProbe.Read<BuildingStore>(
                ReflectionProbe.Field(typeof(BuildingSystem), "_buildings"), sys);

        // ── The EDITOR path (Godot-coupled ⇒ source-pinned) ─────────────────────────────────────────────

        [Fact]
        public void EntityPlacer_EditorPlacement_DelegatesToTheSharedMapper()
        {
            string path = SrcFile("UI", "EntityPlacer.cs");
            Assert.True(File.Exists(path), $"source file not found at '{path}' (via [CallerFilePath]).");

            string blob = StripCommentsAndNormalize(File.ReadAllText(path));

            // Vacuous-pass guard: the editor placement helper must still exist under the name this pin scans for.
            Assert.Matches(@"\bstatic int CreateEditorBuilding\(", blob);

            Assert.Matches(@"\bstore\.CreateFromDefinition\(", blob);
            Assert.False(Regex.IsMatch(blob, @"\bstore\.Create\("),
                "EntityPlacer re-inlines the def→BuildingStore.Create mapping. DW-172: editor placement must delegate " +
                "to BuildingStore.CreateFromDefinition, the same mapper BuildingSystem.PlaceBuildingDirectById uses, " +
                "so the two placement paths cannot drift apart in the stats a placed building carries.");
        }

        // ── Source-scan plumbing (mirrors CommandApplyParityTests) ──────────────────────────────────────

        private static string StripCommentsAndNormalize(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"//[^\n]*", " ");
            return Regex.Replace(text, @"\s+", " ");
        }

        /// <summary>This file lives in godot/ProjectChimera.Sim.Tests/Core/ → ../../src/&lt;a&gt;/&lt;b&gt;.</summary>
        private static string SrcFile(string a, string b, [CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source dir via [CallerFilePath].");
            return Path.GetFullPath(Path.Combine(dir, "..", "..", "src", a, b));
        }
    }
}
