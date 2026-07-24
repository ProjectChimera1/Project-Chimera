#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;   // Fixed
using ProjectChimera.Combat; // HeroXpSystem.XpCeiling (the DW-12 fail-closed ceiling)

namespace ProjectChimera.Core.Definitions
{
    /// <summary>Why a <see cref="PlayerProfile"/> failed validation (Story 9.12). <see cref="None"/> = valid.</summary>
    public enum ProfileInvalidReason
    {
        None,
        Identity,
        Range,
        Inventory,
        Attributes,
    }

    /// <summary>The result of <see cref="HeroProfileValidator.Validate"/>: valid or, if not, the FIRST rule class that
    /// rejected it (<see cref="ProfileInvalidReason"/>). Integer/enum only — Godot-free, float-free.</summary>
    public readonly record struct ProfileValidation(bool IsValid, ProfileInvalidReason Reason)
    {
        /// <summary>The canonical "all rules pass" result.</summary>
        public static readonly ProfileValidation Valid = new(true, ProfileInvalidReason.None);

        /// <summary>A rejection with the given reason class.</summary>
        public static ProfileValidation Invalid(ProfileInvalidReason reason) => new(false, reason);
    }

    /// <summary>
    /// Story 9.12 (FR-7c / AR-12) — the SINGLE canonical profile-validity rule set. Pure C#, Godot-free, float-free (it
    /// auto-globs into the Tier-1 harness + determinism analyzer via <c>SimSources.props src/Core/**</c>). The rules are
    /// obeyed at two authorities that can never drift: (1) on the CLIENT, the init-time apply gate
    /// <see cref="HeroProfileLoader.LoadInto"/> delegates its DW-12 range branch to <see cref="IsLevelXpInRange"/> here
    /// (the client's canonical delegation; the full <see cref="Validate"/> has no separate client pre-flight caller — the
    /// online rail trusts the server as the full-validate authority); and (2) on the SERVER, the TS module
    /// (<c>docs/server-deploy/nakama-modules/src/validation.ts</c>) mirrors the WHOLE rule set — it validates on write and
    /// re-validates on attest, so it is the full-validation authority for the online rail. <b>The TS mirror and this file
    /// MUST stay in sync — edit both together; both test suites are driven off the shared fixture
    /// <c>docs/server-deploy/nakama-modules/test/fixtures/validation-cases.json</c>.</b>
    ///
    /// <para>Rules (checked in this order, so the returned <see cref="ProfileValidation.Reason"/> matches the I/O matrix):
    /// identity (ProfileId + HeroDefId non-empty) → range (<c>level ≥ 0</c>,
    /// <c>0 ≤ xp.Raw ≤ HeroXpSystem.XpCeiling.Raw</c> inclusive — no added upper level ceiling, behaviour-neutral) →
    /// attributes (every persisted raw ≥ 0, no duplicate keys) → inventory (every charge ≥ 0, no duplicate non-negative
    /// slot). Never a silent clamp — reject fail-closed.</para>
    /// </summary>
    public static class HeroProfileValidator
    {
        /// <summary>The DW-12 level/xp range rule — the SINGLE source of truth for the range gate, matching today's
        /// <see cref="HeroProfileLoader.LoadInto"/> predicate EXACTLY (<c>level ≥ 0 &amp;&amp; 0 ≤ xpRaw ≤ ceiling</c>,
        /// inclusive). The loader's range branch calls this so the two never drift (behaviour-neutral by construction).</summary>
        public static bool IsLevelXpInRange(int level, int xpRaw)
            => level >= 0 && xpRaw >= 0 && xpRaw <= HeroXpSystem.XpCeiling.Raw;

        /// <summary>Validate <paramref name="profile"/> against the canonical rule set. A <c>null</c> profile is treated
        /// as an identity failure (there is no valid empty profile).</summary>
        public static ProfileValidation Validate(PlayerProfile? profile)
        {
            if (profile == null) return ProfileValidation.Invalid(ProfileInvalidReason.Identity);

            // 1) Identity — a profile with no stable id / no hero-def id can never mint a deterministic hero.
            if (string.IsNullOrWhiteSpace(profile.ProfileId) || string.IsNullOrWhiteSpace(profile.HeroDefId))
                return ProfileValidation.Invalid(ProfileInvalidReason.Identity);

            // 2) Range — the DW-12 level/xp bounds (delegated to the shared predicate). Checked BEFORE attributes so a
            //    negative level reports `range`, not `attributes` (the I/O matrix requires `range`).
            if (!IsLevelXpInRange(profile.Level, profile.Xp.Raw))
                return ProfileValidation.Invalid(ProfileInvalidReason.Range);

            // 3) Attributes — every persisted raw must be non-negative, and no key may repeat (a duplicate key makes the
            //    by-key accessor ambiguous). Linear scans only (no Dictionary enumeration — the sim-layer rule).
            List<ProfileAttributeValue> values = profile.Values;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i].Raw < 0)
                    return ProfileValidation.Invalid(ProfileInvalidReason.Attributes);
                for (int j = i + 1; j < values.Count; j++)
                    if (values[j].Key == values[i].Key)
                        return ProfileValidation.Invalid(ProfileInvalidReason.Attributes);
            }

            // 4) Inventory — every charge count non-negative, and no two items may claim the same NON-NEGATIVE slot (a
            //    legacy slot of -1 is the "first free" sentinel, so multiple -1s are legal and NOT a duplicate).
            List<ProfileInventoryItem> inv = profile.Inventory;
            for (int i = 0; i < inv.Count; i++)
            {
                if (inv[i].Charges < 0)
                    return ProfileValidation.Invalid(ProfileInvalidReason.Inventory);
                if (inv[i].Slot < 0) continue;
                for (int j = i + 1; j < inv.Count; j++)
                    if (inv[j].Slot == inv[i].Slot)
                        return ProfileValidation.Invalid(ProfileInvalidReason.Inventory);
            }

            return ProfileValidation.Valid;
        }
    }
}
