#nullable enable
using System;
using System.Security.Cryptography;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.UGC
{
    /// <summary>The outcome of resolving the per-install proof-of-play signing key (Story 9.8, review P3).</summary>
    public enum SigningKeyStatus
    {
        /// <summary>No key existed; a fresh one was generated and stored.</summary>
        Provisioned,
        /// <summary>A valid key already existed and was returned.</summary>
        Existing,
        /// <summary>A key entry existed but was unreadable (non-hex) — LEFT UNTOUCHED (never rotated), no key returned.</summary>
        CorruptExisting,
    }

    /// <summary>
    /// Story 9.8 (review P5/P3/P7) — the SINGLE Godot-free home for the mint-side decisions shared by the mint hook
    /// (<c>ScenarioDelegateBinder</c>) and the pack/load side (<c>WinConditionPhase</c>): whether to mint, the scenario
    /// identity key, and per-install signing-key provisioning. Extracted so the two surfaces can never diverge (a
    /// diverged <see cref="ResolveScenarioId"/> would silently package a token the export can't find) and so the pure
    /// rules are Tier-1 testable.
    /// </summary>
    public static class ProofOfPlayMint
    {
        /// <summary>The Godot <c>user://</c> directory the per-scenario proof-of-play token JSON files live under. A
        /// single shared constant so the mint side (<c>ScenarioDelegateBinder</c> Save) and the export/load side
        /// (<c>WinConditionPhase</c> TryLoad) can never point at divergent directories — a drift between two raw
        /// literals would silently package a token the export can't find and refuse every publish with
        /// "no proof-of-play". Godot-agnostic string (no <c>using Godot</c>); the callers <c>GlobalizePath</c> it.</summary>
        public const string TokenDirGodotPath = "user://tokens";

        /// <summary>Mint iff the winning slot maps to the LOCAL faction. Slots are 0-based; the faction enum is 1-based
        /// (Neutral=0, Player1=1…), matching the <c>(Faction)(slot+1)</c> convention used across the presentation
        /// layer. A loss / another faction's win returns false (mints nothing).</summary>
        public static bool ShouldMint(int winnerSlot, Faction localFaction)
            => (Faction)(winnerSlot + 1) == localFaction;

        /// <summary>The token's scenario-identity key: the authored <see cref="ScenarioData.Id"/>, else a slug of the
        /// display name, else a fixed safe fallback so a token always has a stable id. The single derivation used by
        /// BOTH the mint side and the export/load side (the store sanitizes it further to a file-safe name).</summary>
        public static string ResolveScenarioId(ScenarioData? scenario)
        {
            if (scenario == null) return "scenario";
            if (!string.IsNullOrEmpty(scenario.Id)) return scenario.Id;
            string slug = ContentPackager.Slugify(scenario.DisplayName ?? "");
            return string.IsNullOrEmpty(slug) ? "scenario" : slug;
        }

        /// <summary>
        /// Read the per-install HMAC signing key from <paramref name="store"/>, generating 32 random bytes (hex-encoded)
        /// ONLY when none exists. Review P3: an existing-but-corrupt key is NEVER overwritten — that would silently
        /// rotate the install key and invalidate every previously-minted token. In that case the stored value is left
        /// intact, <see cref="SigningKeyStatus.CorruptExisting"/> is returned, and <paramref name="key"/> is empty (the
        /// caller skips minting rather than rotating).
        /// </summary>
        public static SigningKeyStatus GetOrProvisionSigningKey(ISecretStore store, out byte[] key)
        {
            key = Array.Empty<byte>();
            if (store == null) return SigningKeyStatus.CorruptExisting; // no store ⇒ cannot sign, do nothing

            string existing = store.Get(SecretIds.ProofOfPlay);
            if (!string.IsNullOrEmpty(existing))
            {
                try { key = Convert.FromHexString(existing); return SigningKeyStatus.Existing; }
                catch { return SigningKeyStatus.CorruptExisting; } // do NOT overwrite corrupt key material
            }

            key = RandomNumberGenerator.GetBytes(32);
            store.Set(SecretIds.ProofOfPlay, Convert.ToHexString(key));
            return SigningKeyStatus.Provisioned;
        }
    }
}
