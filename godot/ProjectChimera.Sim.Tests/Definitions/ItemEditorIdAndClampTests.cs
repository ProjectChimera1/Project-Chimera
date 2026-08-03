#nullable enable
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-452 / DW-453 / DW-456 — the item editor's Godot-free extraction seams. <c>ItemCardPanel</c> is a Godot
    /// <c>Node</c> outside the Tier-1 boundary, so its three safety decisions were previously verified only by
    /// reading; each is now a static on <see cref="ItemDefinitionValidator"/> that the panel delegates to and these
    /// tests pin:
    /// <list type="bullet">
    /// <item>DW-452 — <see cref="ItemDefinitionValidator.MoveSpeedSpinnerRange"/>: the "Speed" spinner clamp is
    /// exactly ±<see cref="ItemDefinitionValidator.MAX_MOVE_SPEED_DELTA"/> (Story 3.16 AC4), and the window matches
    /// the fail-closed gate's acceptance window at both boundaries.</item>
    /// <item>DW-453 — <see cref="ItemDefinitionValidator.MakeUniqueItemId"/>: Create/Duplicate id-minting rides the
    /// SHARED <c>UnitDefinitionValidator.SanitizeId</c> convention (the old panel-local Unicode sanitizer kept
    /// letters like <c>é</c> that the DW-47 gate rejects — an un-saveable item).</item>
    /// <item>DW-456 — <see cref="ItemDefinitionValidator.IsFilenameSafeId"/>: THE "may this id touch the on-disk
    /// file?" decision guarding <c>DoDelete</c>'s <c>File.Delete</c>, kept in lockstep with both id gates.</item>
    /// </list>
    /// </summary>
    public class ItemEditorIdAndClampTests
    {
        private static readonly ItemDefinitionValidator V = new();

        private static ItemDefinition Item(string id) => new ItemDefinition { Id = id, Charges = 0 };

        private static bool HasIdBadge(string id) =>
            V.ValidateFields(Item(id)).Errors.Any(e => e.FieldPath == "id");

        // ── DW-452: the Speed spinner clamp ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void MoveSpeedSpinnerRange_IsExactlyPlusMinusTheValidatorCap()
        {
            // AC4: the editor's Speed spinner min/max == ±MAX_MOVE_SPEED_DELTA. RED before DW-452: no Godot-free
            // surface existed to pin — decoupling the spinner from the constant broke nothing.
            int cap = ItemDefinitionValidator.MAX_MOVE_SPEED_DELTA.ToInt();
            Assert.Equal((-cap, cap), ItemDefinitionValidator.MoveSpeedSpinnerRange());
        }

        [Fact]
        public void MoveSpeedSpinnerRange_IsTheDecidedPlusMinus50()
        {
            // DW-42 decided ±50 explicitly; pin the literal so a silent MAX_MOVE_SPEED_DELTA drift (which would move
            // the spinner AND the gate together, keeping the relative test green) still trips a deliberate review.
            Assert.Equal((-50, 50), ItemDefinitionValidator.MoveSpeedSpinnerRange());
        }

        [Fact]
        public void MoveSpeedSpinnerWindow_MatchesTheGateAcceptanceWindow()
        {
            // The point of the clamp: every value the spinner can dial passes the fail-closed gate, and the first
            // value beyond the spinner in each direction is one the gate rejects — the windows are the SAME window.
            (int min, int max) = ItemDefinitionValidator.MoveSpeedSpinnerRange();

            Assert.True(V.Validate(new ItemDefinition { Id = "boots", Charges = 0, MoveSpeedDelta = Fixed.FromInt(max) }).Ok);
            Assert.True(V.Validate(new ItemDefinition { Id = "boots", Charges = 0, MoveSpeedDelta = Fixed.FromInt(min) }).Ok);
            Assert.False(V.Validate(new ItemDefinition { Id = "boots", Charges = 0, MoveSpeedDelta = Fixed.FromInt(max + 1) }).Ok);
            Assert.False(V.Validate(new ItemDefinition { Id = "boots", Charges = 0, MoveSpeedDelta = Fixed.FromInt(min - 1) }).Ok);
        }

        // ── DW-456: the single "may this id touch the on-disk file?" decision ─────────────────────────────────────

        [Theory]
        [InlineData("ring_of_vigor")]
        [InlineData("a")]
        [InlineData("x2")]
        [InlineData("42")]
        [InlineData("_")]
        [InlineData("item_2")]
        [InlineData("con_2")]     // near-reserved: suffixed mints stay legal
        [InlineData("console")]   // merely CONTAINS "con"
        [InlineData("com0")]      // not a reserved device
        public void FilenameSafeId_IsAccepted_ByPredicateAndBothGates(string id)
        {
            // The predicate and the two id gates are ONE convention — a safe id passes all three surfaces.
            Assert.True(ItemDefinitionValidator.IsFilenameSafeId(id), id);
            Assert.True(V.Validate(Item(id)).Ok, id);
            Assert.False(HasIdBadge(id), id);
        }

        [Theory]
        [InlineData("../../foo")]   // the DW-47 traversal class File.Delete/Persist() must never see
        [InlineData("..")]
        [InlineData(@"..\..\foo")]
        [InlineData("a/b")]
        [InlineData(@"a\b")]
        [InlineData("a.b")]
        [InlineData("c:")]
        [InlineData("Ring")]        // uppercase — out of the [a-z0-9_] charset
        [InlineData(" ring")]       // leading whitespace
        [InlineData("café")]        // Unicode letter — the DW-453 divergence class
        [InlineData("con")]         // DW-454 reserved device basenames
        [InlineData("CON")]
        [InlineData("nul")]
        [InlineData("com1")]
        [InlineData("lpt9")]
        public void UnsafeId_IsRefused_ByPredicateAndBothGates(string id)
        {
            // RED before DW-456 for the sink half: DoDelete's guard was an inline lambda check in a Godot Node —
            // deleting it reopened the filesystem sink with every test still green. The guard is now THIS predicate.
            Assert.False(ItemDefinitionValidator.IsFilenameSafeId(id), id);
            Assert.False(V.Validate(Item(id)).Ok, id);
            Assert.True(HasIdBadge(id), id);
        }

        [Fact]
        public void NullOrEmptyId_IsNeverFilenameSafe()
        {
            // Fail-closed: an empty id can never have produced a legit on-disk file, so it must never reach
            // File.Delete (the old inline guard PASSED "" through, relying on File.Exists to shrug).
            Assert.False(ItemDefinitionValidator.IsFilenameSafeId(null));
            Assert.False(ItemDefinitionValidator.IsFilenameSafeId(""));
            Assert.False(V.Validate(Item("")).Ok);
            Assert.True(HasIdBadge(""));
        }

        [Fact]
        public void UnsafeId_StillBadgesTheIdFieldExactlyOnce()
        {
            // The DW-456 single-decision refactor must preserve the D-9 one-badge-per-field contract for an id that
            // fails BOTH sub-rules ("CON" is out-of-charset AND reserved) — and keep the charset-first message order.
            var uppercaseReserved = V.ValidateFields(Item("CON")).Errors.Where(e => e.FieldPath == "id").ToList();
            Assert.Single(uppercaseReserved);
            Assert.Contains("outside [a-z0-9_]", uppercaseReserved[0].Message);

            var reservedOnly = V.ValidateFields(Item("con")).Errors.Where(e => e.FieldPath == "id").ToList();
            Assert.Single(reservedOnly);
            Assert.Contains("reserved device name", reservedOnly[0].Message);
        }

        // ── DW-453: Create/Duplicate id-minting rides the shared convention ───────────────────────────────────────

        [Fact]
        public void Mint_UnicodeBase_ProducesAGateSatisfyingId()
        {
            // THE DW-453 defect: the panel's old local sanitizer (char.IsLetterOrDigit) KEPT 'é', minting "café" —
            // which the DW-47 charset gate rejects, an un-saveable item needing a manual rename. The shared mint
            // collapses it to '_' so the minted id always satisfies the gate.
            string minted = ItemDefinitionValidator.MakeUniqueItemId(System.Array.Empty<string>(), "café");
            Assert.Equal("caf_", minted);
            Assert.True(ItemDefinitionValidator.IsFilenameSafeId(minted));

            // And what the OLD sanitizer would have minted is exactly what the gate refuses.
            Assert.False(ItemDefinitionValidator.IsFilenameSafeId("café"));
        }

        [Fact]
        public void Mint_OrdinaryCreateAndDuplicateBases_KeepTheirPriorBehavior()
        {
            // Regression net for the delegation: DoCreate ("new_item") and DoDuplicate ("<id>_copy") bases mint
            // byte-identically to the old local loop, including the _2 dedup suffix.
            Assert.Equal("new_item", ItemDefinitionValidator.MakeUniqueItemId(new[] { "ring" }, "new_item"));
            Assert.Equal("new_item_2", ItemDefinitionValidator.MakeUniqueItemId(new[] { "new_item" }, "new_item"));
            Assert.Equal("ring_copy", ItemDefinitionValidator.MakeUniqueItemId(new[] { "ring" }, "ring_copy"));
            Assert.Equal("ring_copy_2", ItemDefinitionValidator.MakeUniqueItemId(new[] { "ring", "ring_copy" }, "ring_copy"));
        }

        [Fact]
        public void Mint_EmptyBase_FallsBackToTheItemNoun_NotTheUnitNoun()
        {
            // The shared MakeUniqueId's empty fallback is "new_unit" — the item mint substitutes the item noun first
            // (the panel's old "item" fallback), so an all-symbol/whitespace base still mints an item-flavored id.
            Assert.Equal("item", ItemDefinitionValidator.MakeUniqueItemId(System.Array.Empty<string>(), "   "));
            Assert.Equal("item", ItemDefinitionValidator.MakeUniqueItemId(System.Array.Empty<string>(), null));
            Assert.Equal("item_2", ItemDefinitionValidator.MakeUniqueItemId(new[] { "item" }, ""));
        }

        [Fact]
        public void Mint_ReservedBase_SuffixesInsteadOfMintingAnInvalidId()
        {
            // DW-454 coherence, now on the ITEM mint too: a reserved basename is treated like a taken id, so the
            // minter can never hand back an id its own Save gate refuses.
            Assert.Equal("con_2", ItemDefinitionValidator.MakeUniqueItemId(System.Array.Empty<string>(), "con"));
            Assert.Equal("nul_2", ItemDefinitionValidator.MakeUniqueItemId(System.Array.Empty<string>(), "NUL"));
        }

        [Theory]
        [InlineData("café")]
        [InlineData("CafÉ 12")]
        [InlineData("../../foo")]
        [InlineData("CON")]
        [InlineData("  spaced base  ")]
        [InlineData("☃☃☃")]
        [InlineData("")]
        [InlineData("Ring Of Vigor_copy")]
        public void Mint_AdversarialBase_AlwaysSatisfiesTheSaveGate(string baseId)
        {
            // The property the panel now inherits by delegation: whatever the creator's base string, the minted id is
            // filename-safe and draws no "id" badge — New/Duplicate can never produce an un-saveable item.
            string minted = ItemDefinitionValidator.MakeUniqueItemId(new[] { "ring", "item" }, baseId);
            Assert.True(ItemDefinitionValidator.IsFilenameSafeId(minted), $"'{baseId}' minted '{minted}'");
            Assert.False(HasIdBadge(minted), $"'{baseId}' minted '{minted}'");
            Assert.True(V.Validate(Item(minted)).Ok, $"'{baseId}' minted '{minted}'");
        }
    }
}
