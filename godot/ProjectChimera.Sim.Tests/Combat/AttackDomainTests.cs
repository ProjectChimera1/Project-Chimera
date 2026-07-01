#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 2.9a (AC1) — the air/ground attack-domain filter in auto-acquire, plus the <see cref="DomainClassifier"/>
    /// single-source classifier and the <see cref="UnitDefinition"/> <c>attack_domains</c> parser. Godot-free,
    /// <see cref="Fixed"/>-only, ascending-id — runs on every OS leg including the WSL cross-platform gate.
    /// </summary>
    public class AttackDomainTests
    {
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        // ── The classifier: the ONE place "what is a flyer" is decided (shared by AC1 combat + AC6 ability) ──

        [Fact]
        public void Classifier_MapsCategoryToDomain()
        {
            Assert.Equal(Domain.Air,       DomainClassifier.Of(UnitCategory.Air));
            Assert.Equal(Domain.Structure, DomainClassifier.Of(UnitCategory.Structure));
            foreach (UnitCategory c in new[] { UnitCategory.Worker, UnitCategory.Melee, UnitCategory.Ranged, UnitCategory.Siege })
                Assert.Equal(Domain.Ground, DomainClassifier.Of(c));
        }

        [Fact]
        public void CanAttack_HonorsMask()
        {
            // Anti-air only: hits fliers, not ground or structures.
            Assert.True(DomainClassifier.CanAttack(AttackDomain.Air, UnitCategory.Air));
            Assert.False(DomainClassifier.CanAttack(AttackDomain.Air, UnitCategory.Melee));
            Assert.False(DomainClassifier.CanAttack(AttackDomain.Air, UnitCategory.Structure));

            // Ground only: hits ground, not fliers.
            Assert.True(DomainClassifier.CanAttack(AttackDomain.Ground, UnitCategory.Siege));
            Assert.False(DomainClassifier.CanAttack(AttackDomain.Ground, UnitCategory.Air));

            // Default All hits every domain (AC1.3 / AC1.4).
            foreach (UnitCategory c in new[] { UnitCategory.Worker, UnitCategory.Melee, UnitCategory.Ranged,
                                               UnitCategory.Siege, UnitCategory.Air, UnitCategory.Structure })
                Assert.True(DomainClassifier.CanAttack(AttackDomain.All, c));
        }

        // ── The parser: attack_domains → AttackDomain, default All (AC1.3 — no shipping JSON edited) ──

        [Fact]
        public void ParsedAttackDomains_UnauthoredOrEmptyOrAllThree_IsAll()
        {
            Assert.Equal(AttackDomain.All, new UnitDefinition().ParsedAttackDomains);                                       // null
            Assert.Equal(AttackDomain.All, new UnitDefinition { AttackDomains = new string[0] }.ParsedAttackDomains);       // empty
            Assert.Equal(AttackDomain.All, new UnitDefinition { AttackDomains = new[] { "Nonsense" } }.ParsedAttackDomains);// unknown-only
            Assert.Equal(AttackDomain.All, new UnitDefinition { AttackDomains = new[] { "Ground", "Air", "Structure" } }.ParsedAttackDomains);
        }

        [Fact]
        public void ParsedAttackDomains_Restriction_IsExactSubset()
        {
            Assert.Equal(AttackDomain.Air,    new UnitDefinition { AttackDomains = new[] { "Air" } }.ParsedAttackDomains);
            Assert.Equal(AttackDomain.Ground, new UnitDefinition { AttackDomains = new[] { "Ground" } }.ParsedAttackDomains);
            Assert.Equal(AttackDomain.Air | AttackDomain.Structure,
                         new UnitDefinition { AttackDomains = new[] { "Air", "Structure" } }.ParsedAttackDomains);
        }

        // ── AC1 / AC1.1 / AC1.2 — the filter prunes candidates in BOTH acquire paths ──

        private static int Enemy(EntityWorld w, FixedVec3 pos, UnitCategory cat)
        {
            int id = w.Create(pos, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            w.CategoryOf[id] = cat;   // AC1.2: "is the candidate flying?" is read from CategoryOf, never armor_type
            return id;
        }

        [Fact]
        public void FindNearestEnemy_AntiAirUnit_PicksAirOverNearerGround()
        {
            var w  = new EntityWorld();
            int aa = w.Create(V(0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.AttackRange[aa]    = Fixed.FromInt(20);
            w.AttackDomainOf[aa] = AttackDomain.Air;                 // authored anti-air only
            /* nearer ground decoy */ Enemy(w, V(2, 0), UnitCategory.Melee);
            int air = Enemy(w, V(8, 0), UnitCategory.Air);          // farther flier — the only valid target

            var hash = new SpatialHash();
            hash.Rebuild(w);

            // In-range acquire and chase-to-contact BOTH honor the domain filter (AC1 + AC1.1).
            // Teeth: without the guard each returns the NEARER ground decoy — this asserts the far air unit instead.
            Assert.Equal(air, hash.FindNearestEnemy(w, aa));
            Assert.Equal(air, hash.FindNearestEnemyGlobal(w, aa));
        }

        [Fact]
        public void FindNearestEnemy_GroundOnlyUnit_PicksGroundOverNearerAir()
        {
            var w = new EntityWorld();
            int g = w.Create(V(0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.AttackRange[g]    = Fixed.FromInt(20);
            w.AttackDomainOf[g] = AttackDomain.Ground;
            /* nearer flier decoy */ Enemy(w, V(2, 0), UnitCategory.Air);
            int ground = Enemy(w, V(8, 0), UnitCategory.Melee);

            var hash = new SpatialHash();
            hash.Rebuild(w);
            Assert.Equal(ground, hash.FindNearestEnemy(w, g));
            Assert.Equal(ground, hash.FindNearestEnemyGlobal(w, g));
        }

        [Fact]
        public void FindNearestEnemy_DefaultAll_PicksNearestRegardlessOfDomain()
        {
            var w = new EntityWorld();
            int u = w.Create(V(0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.AttackRange[u] = Fixed.FromInt(20);                    // AttackDomainOf defaults to All
            int air = Enemy(w, V(2, 0), UnitCategory.Air);          // nearest — with default All it wins
            /* farther ground */ Enemy(w, V(8, 0), UnitCategory.Melee);

            var hash = new SpatialHash();
            hash.Rebuild(w);
            Assert.Equal(air, hash.FindNearestEnemy(w, u));         // nearest wins, domain ignored (unchanged behaviour)
        }

        // AC1.1 end-to-end: an anti-air unit with only a ground enemy in the world does NOT march at it.

        [Fact]
        public void IdleCombat_AntiAirUnit_DoesNotChaseOrDamageGroundEnemy()
        {
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());
            int aa = w.Create(V(0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[aa] = Fixed.FromInt(10);
            w.AttackRange[aa]    = Fixed.FromInt(2);
            w.AttackSpeed[aa]    = Fixed.FromInt(1);
            w.AttackDomainOf[aa] = AttackDomain.Air;
            int ground = Enemy(w, V(1, 0), UnitCategory.Melee);     // in melee range, but the AA unit can't hit it
            Fixed before = w.Health[ground];

            w.CommandState[aa] = UnitCommand.Idle;
            combat.Tick(w, Dt);

            Assert.Equal(before.Raw, w.Health[ground].Raw);         // untouched
            Assert.True((w.Flags[aa] & EntityFlags.Moving) == 0);   // does not chase a target it can never damage
        }
    }
}
