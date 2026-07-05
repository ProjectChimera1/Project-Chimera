#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 2.13 (AC3.1–AC3.3) — BuildingStore free-list recycling and the SoA-recycle trap. Placing/destroying
    /// past the old 64-slot cap keeps working (slots reuse), a recycled slot carries ZERO stale state (mirrors the
    /// EntityWorld <c>RecycledSlot_CarriesNoPrior*</c> guards), and the <c>SupplyBonus</c> default-branch gap is
    /// closed. Godot-free, deterministic. All bookkeeping is UNFOLDED (no SimChecksum change).
    /// </summary>
    public class BuildingStoreRecycleTests
    {
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        // ── AC3.1 — placement past 64 (recycling one-live-at-a-time) never exhausts the store ──

        [Fact]
        public void PlaceAndDestroy_Past64_AlwaysRecycles_NeverReturnsMinusOne()
        {
            var store = new BuildingStore();
            for (int i = 0; i < 200; i++)   // > 3× the old 64 cap
            {
                int id = store.Create(V(i, 0), Faction.Player1, BuildingType.Barracks);
                Assert.True(id >= 0, $"Create #{i} returned -1 — the free-list is not recycling dead slots.");
                store.Destroy(id);
            }
            Assert.Equal(1, store.Count); // only one slot ever live ⇒ the high-water mark never grows past 1
        }

        // ── AC3.1 — the cap trips ONLY when all 64 are SIMULTANEOUSLY live; a Destroy frees a slot again ──

        [Fact]
        public void SixtyFourSimultaneouslyLive_65thFails_ThenRecyclesAfterDestroy()
        {
            var store = new BuildingStore();
            var ids = new int[BuildingStore.MAX_BUILDINGS];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = store.Create(V(i, 0), Faction.Player1, BuildingType.Barracks);
                Assert.True(ids[i] >= 0);
            }
            Assert.Equal(-1, store.Create(V(99, 0), Faction.Player1, BuildingType.Barracks)); // all 64 live → full

            store.Destroy(ids[0]);
            int reused = store.Create(V(100, 0), Faction.Player1, BuildingType.Barracks);
            Assert.True(reused >= 0, "a freed slot must make Create succeed again");
            Assert.Equal(ids[0], reused);  // LIFO reuses the just-freed slot
        }

        // ── AC3.3 — a recycled slot carries NO prior-occupant state (the SoA-recycle trap) ──

        [Fact]
        public void RecycledSlot_CarriesNoPriorState()
        {
            var store = new BuildingStore();
            int cc = store.Create(V(5, 5), Faction.Player1, BuildingType.CommandCenter); // SupplyBonus 10, Health 500
            // Dirty every per-building field a used CommandCenter would hold.
            store.HasRallyPoint[cc] = true;
            store.RallyPoint[cc]    = V(9, 9);
            store.ProductionQueue[cc] = 5;
            store.TrainedCount[cc]  = 7;

            store.Destroy(cc);
            int reused = store.Create(V(1, 1), Faction.Player2, BuildingType.Barracks); // different type + faction
            Assert.Equal(cc, reused); // same slot reused

            Assert.Equal(Faction.Player2, store.FactionOf[reused]);
            Assert.Equal(BuildingType.Barracks, store.Type[reused]);
            Assert.Equal(0, store.SupplyBonus[reused]);                 // NOT the CommandCenter's 10
            Assert.Equal(Fixed.FromFloat(300f).Raw, store.Health[reused].Raw); // Barracks HP, not 500
            Assert.False(store.HasRallyPoint[reused]);                  // rally reset (2.12)
            Assert.Equal(FixedVec3.Zero.X.Raw, store.RallyPoint[reused].X.Raw);
            Assert.Equal((byte)0, store.ProductionQueue[reused]);
            Assert.Equal(0, store.TrainedCount[reused]);
        }

        // ── AC3.2 — the SupplyBonus default-branch gap: a CommandCenter recycled into a default-typed slot must
        //    NOT inherit +10 supply. Teeth: reachable only via a type that hits the switch `default:` (an out-of-enum
        //    value — the data-driven platform's forward path); RED without the unconditional pre-switch zero. ──

        [Fact]
        public void RecycledCommandCenter_IntoDefaultTypedSlot_LeaksNoSupplyBonus()
        {
            var store = new BuildingStore();
            int cc = store.Create(V(0, 0), Faction.Player1, BuildingType.CommandCenter);
            Assert.Equal(10, store.SupplyBonus[cc]); // precondition: a CommandCenter grants +10

            store.Destroy(cc);
            int reused = store.Create(V(0, 0), Faction.Player1, (BuildingType)99); // hits the switch `default:` branch
            Assert.Equal(cc, reused);
            Assert.Equal(0, store.SupplyBonus[reused]); // RED without the pre-switch zero (would inherit the stale 10)
        }
    }
}
