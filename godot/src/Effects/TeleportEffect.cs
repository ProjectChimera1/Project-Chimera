#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// Story 15.13 (DW-248) — the reserved SIM-MUTATING relocation leaf: a deterministic BLINK that moves the CASTER to
    /// a destination through the same placement path <see cref="EntityWorld.Create"/> / a MovementSystem arrival use.
    /// It folds into <c>SimChecksum</c> via <see cref="EntityWorld.Position"/> like any move.
    ///
    /// <para><b>PLACEMENT-CLASS, not a swept step.</b> A blink deliberately bypasses walls between origin and
    /// destination, so it writes <see cref="EntityWorld.Position"/> DIRECTLY and does NOT route through
    /// <c>CheckedStep.Resolve</c> — that swept helper would stop the blink at the first wall. This is the single new
    /// <c>Position</c> writer sanctioned (count 1) in <c>PositionWriterGuardTests</c>. Destination validity is the
    /// ground-cast RaycastGround gate (MVP); an off-map point simply relocates there (no crash).</para>
    ///
    /// <para><b>Destination rule</b> — the only reading the closed vocabulary supports for BOTH the flagship blink
    /// (self→ground) and a charge (self→target):
    /// a GroundPoint cast → the clicked point (X,Z; Y flattened to the ground plane 0); else a live NON-caster primary
    /// target → that target's position (a charge); else (a Self cast with no target point) → a no-op.</para>
    ///
    /// <para>After moving it re-establishes entity consistency EXACTLY like <see cref="EntityWorld.Create"/> / a
    /// movement arrival: <c>PrevPosition = dest</c> (no interpolation smear), <c>Velocity = Zero</c>, a re-sampled
    /// <c>Elevation</c> (a FOLDED SoA — stale = desync), the <see cref="EntityFlags.Moving"/> flag cleared,
    /// <c>MoveTarget = dest</c>, and <c>CommandState = Idle</c> so a stale in-progress move is cancelled and
    /// <c>FlowFieldBridge</c> self-clears the now-stale flow field.</para>
    ///
    /// Pure simulation: <c>Fixed</c> only, ascending-id-safe, no wall-clock/RNG. Displacing a NON-caster target (a
    /// "hook"/"yank") or continuing the unit's prior order is deliberately out of scope — the caster moves and stops.
    /// </summary>
    public sealed class TeleportEffect : LeafEffect
    {
        /// <summary>Construct the blink leaf. <paramref name="requireTag"/> (Story 2.11, default None) gates the
        /// single-target apply on the primary target's tag; omit for the ungated behaviour.</summary>
        public TeleportEffect(UnitTag requireTag = UnitTag.None) : base(requireTag) { }

        /// <inheritdoc />
        internal override void Apply(in EffectContext ctx)
        {
            EntityWorld world = ctx.World;
            int caster = ctx.CasterId;
            if (!world.IsAlive(caster)) return; // dead/recycled caster — guarded no-op

            // Resolve the destination (see the class remarks). No valid destination ⇒ no-op, so a Self cast with no
            // ground point (PrimaryTargetId == caster, !HasTargetPoint) never perturbs the checksum.
            FixedVec3 dest;
            if (ctx.HasTargetPoint)
                dest = new FixedVec3(ctx.TargetPoint.X, Fixed.Zero, ctx.TargetPoint.Z);
            else if (ctx.PrimaryTargetId != caster && world.IsAlive(ctx.PrimaryTargetId))
                // Charge: relocate to the target's XZ, Y FLATTENED to the ground plane 0 — consistent with the ground
                // branch above (Position.Y is invariant-Zero across the sim; copying a target's Y wholesale would smear
                // it in if it ever drifted).
                dest = new FixedVec3(world.Position[ctx.PrimaryTargetId].X, Fixed.Zero, world.Position[ctx.PrimaryTargetId].Z);
            else
                return; // no destination — caster stays put

            // PLACEMENT (not a swept step): the ONE authoritative Position write, then re-establish the same entity
            // consistency Create()/arrival do. Elevation is a folded SoA — re-sample or the blinked unit desyncs.
            world.Position[caster]     = dest;
            world.PrevPosition[caster] = dest;                 // no interpolation smear from the old position
            world.Velocity[caster]     = FixedVec3.Zero;
            world.Elevation[caster]    = world.SampleElevation(dest.X, dest.Z);
            world.Flags[caster]       &= ~EntityFlags.Moving;  // arrived — clear the moving flag
            world.MoveTarget[caster]   = dest;
            world.CommandState[caster] = UnitCommand.Idle;     // cancel any in-progress move; FlowFieldBridge self-clears the stale field
        }
    }
}
