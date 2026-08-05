#nullable enable
using ProjectChimera.Combat;           // DamageType, ArmorType (raw combat stats read only by the def-less restore branch)
using ProjectChimera.Core.Definitions; // UnitDefinition (the def reference restore re-derives authored fields from)

namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 3.17: the Godot-free residue captured by <see cref="EntityWorld.SnapshotUnit"/> and replayed by
    /// <see cref="EntityWorld.RestoreUnit"/> for the editor delete→Ctrl+Z undo path. It carries ONLY the state restore
    /// cannot re-derive from the def:
    ///   • <see cref="Def"/> — the source <see cref="UnitDefinition"/> (null for a def-less spawn). For a def-based
    ///     unit, RestoreUnit routes this back through <see cref="EntityWorld.ApplyUnitDefinition"/>, so every
    ///     def-derived authored field (armor, passives, abilities/Energy, feedback, tags, attack domain, delivery/
    ///     projectile speed, collision radius, separation priority, category, XP bounty, base/effective stats) is
    ///     re-derived — no per-field hand-copy, and any FUTURE def-derived field is auto-restored.
    ///   • the <see cref="EntityWorld.Create"/> ctor-arg fields (<see cref="Position"/>/<see cref="Faction"/>/
    ///     <see cref="MaxHealth"/>/<see cref="Speed"/>);
    ///   • the caller-owned fields the mapper does not write (<see cref="MeshType"/>/<see cref="GatherState"/>/
    ///     <see cref="CarryCapacity"/>/<see cref="SupplyCost"/>), replayed verbatim so worker overrides survive;
    ///   • the raw combat stats read ONLY by the def-less restore branch (<see cref="AttackRange"/>…
    ///     <see cref="SplashRadius"/>);
    ///   • <see cref="Abilities"/> — DW-54: the RESOLVED ability wiring (castable registry indices + the three
    ///     passive slots), pinned by value because it is link-time RESOLUTION, not authored data, and the pinned
    ///     def is a live shared object an editor session can mutate/replace under the snapshot.
    /// Godot-free value type — reachable from a Tier-1 xUnit test.
    /// </summary>
    public struct UnitSnapshot
    {
        // Def reference (null ⇒ restore uses the raw-stat fallback below).
        public UnitDefinition? Def;

        /// <summary>
        /// DW-54: the ability RESOLUTION this entity was actually running with, pinned at capture time so a restore
        /// never re-derives it from a def that has since been edited in place, swapped out of the roster, or
        /// re-resolved against a different/absent registry. <see cref="EntityWorld.RestoreUnit"/> feeds this to
        /// <see cref="EntityWorld.ApplyUnitDefinition"/>, which writes it BEFORE firing the passive-install seam —
        /// so the installed while-alive passive and <c>SelfPassiveAbilityIndex</c> can never disagree.
        /// <para>Null ⇒ pre-DW-54 behavior (re-derive the wiring from the def). <see cref="EntityWorld.SnapshotUnit"/>
        /// always fills it, so null only occurs on a hand-built snapshot.</para>
        /// </summary>
        public PinnedAbilityWiring? Abilities;

        // Create() ctor-arg fields.
        public FixedVec3 Position;
        public Faction   Faction;
        public Fixed     MaxHealth;
        public Fixed     Speed;

        // Caller-owned residue the def mapper does not write — replayed verbatim after ApplyUnitDefinition.
        public byte        MeshType;
        public GatherState GatherState;
        public Fixed       CarryCapacity;
        public byte        SupplyCost;

        // Raw combat stats — read ONLY by the def-less restore branch (a def-based unit re-derives these).
        public Fixed      AttackRange;
        public Fixed      AttackDamage;
        public Fixed      AttackSpeed;
        public DamageType DamageType;
        public ArmorType  ArmorType;
        public Fixed      VisionRange;
        public Fixed      SplashRadius;
    }
}
