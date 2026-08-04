#nullable enable
using ProjectChimera.Combat;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 11.4 (FR-74) — the single reason→text map for the reactive denial cue. The rejecting guard authors the
    /// <see cref="DenialReason"/> (single-truth); this renders it to player-facing text. MatchAlertBridge calls
    /// <see cref="For"/>.
    ///
    /// Godot-free (Combat only) so the "every reason maps" totality guard is Tier-1 testable. The default arm returns
    /// an EMPTY string on purpose: a newly-added <see cref="DenialReason"/> with no case here yields "" and the
    /// totality test fails loudly, rather than silently falling back to a generic string.
    /// </summary>
    public static class DenialReasonText
    {
        public static string For(DenialReason reason) => reason switch
        {
            DenialReason.None            => "Order denied",
            DenialReason.NeedOre         => "Not enough Ore",
            DenialReason.NeedCrystal     => "Not enough Crystal",
            DenialReason.SupplyCapped    => "Supply capped",
            DenialReason.PrereqMissing   => "Requirements not met",
            DenialReason.OnCooldown      => "On cooldown",
            DenialReason.NoEnergy        => "Not enough energy",
            DenialReason.InvalidLocation => "Invalid location",
            DenialReason.InvalidTarget   => "Invalid target",
            DenialReason.OutOfRange      => "Out of range",
            DenialReason.InventoryFull   => "Inventory full",
            DenialReason.QueueFull       => "Can't queue that right now",
            DenialReason.InsufficientResources => "Not enough resources",
            DenialReason.Silenced        => "Silenced",
            DenialReason.Stunned         => "Stunned",
            _                            => string.Empty, // an unmapped reason is a located test failure, not a silent fallback
        };
    }
}
