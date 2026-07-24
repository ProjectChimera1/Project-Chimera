#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.UGC
{
    /// <summary>The result of a <see cref="PublishGate.Check"/>: whether the package may be published, plus EVERY
    /// failing reason (located, so the UI can list all missing fields at once rather than one-at-a-time).</summary>
    public sealed class PublishGateResult
    {
        public bool Passed { get; }
        public IReadOnlyList<string> Reasons { get; }

        public PublishGateResult(bool passed, IReadOnlyList<string> reasons)
        {
            Passed  = passed;
            Reasons = reasons;
        }
    }

    /// <summary>
    /// Story 9.8 — the single, Godot-free PRE-PUBLISH quality/IP-consent/proof-of-play gate that unifies the two
    /// previously-disconnected surfaces (packaging in <c>WinConditionPhase</c> and upload in <c>ContentBrowserPanel</c>).
    /// Upload is refused unless ALL of:
    /// <list type="bullet">
    ///   <item>a proof-of-play token is present,</item>
    ///   <item>its HMAC signature verifies (untampered),</item>
    ///   <item>its recorded outcome is a win,</item>
    ///   <item>it is NOT stale — its scenario hash still equals the current canonical model hash,</item>
    ///   <item>a thumbnail is present,</item>
    ///   <item>the description is ≥100 chars,</item>
    ///   <item>there is ≥1 screenshot, and</item>
    ///   <item>explicit IP-ownership consent is recorded.</item>
    /// </list>
    /// Every failure is collected so the caller sees all reasons in one pass. Pure logic (no crypto beyond the
    /// deterministic HMAC verify, no Godot, no IO) — fully Tier-1 testable per the I/O-Matrix rows.
    /// </summary>
    public static class PublishGate
    {
        /// <summary>The minimum description length (chars, after trimming) for the min-quality floor.</summary>
        public const int MinDescriptionLength = 100;

        /// <summary>Review P7 — the single source of truth for the win outcome string, shared by the mint side
        /// (<c>ScenarioDelegateBinder</c> → <c>ProofOfPlaySigner.Create</c>) and the gate's outcome compare so the two
        /// can never drift on casing/spelling.</summary>
        public const string WinOutcome = "win";

        // Located refusal reasons — stable strings the UI surfaces and the tests pin.
        public const string ReasonNoToken       = "no proof-of-play";
        public const string ReasonInvalidToken  = "invalid token";
        public const string ReasonStaleToken    = "token stale";
        public const string ReasonNoThumbnail   = "missing thumbnail";
        public const string ReasonShortDesc     = "description must be at least 100 characters";
        public const string ReasonNoScreenshot  = "at least one screenshot required";
        public const string ReasonNoConsent     = "consent required";

        /// <summary>
        /// Evaluate the gate. <paramref name="currentScenarioHash"/> is the CURRENT
        /// <see cref="CanonicalModelHash.Compute"/> value of the model being published (so an edit-after-win is caught
        /// as stale); <paramref name="signingKey"/> is the per-install HMAC key the token was signed with.
        /// </summary>
        public static PublishGateResult Check(ContentPackageManifest manifest, ProofOfPlayToken? token,
                                              ulong currentScenarioHash, byte[] signingKey)
        {
            var reasons = new List<string>();

            // ── Proof-of-play: present → untampered → win → not stale. Each stage gates the next so we never emit a
            //    misleading "stale" for a token whose signature (and thus its hash field) can't be trusted. ──
            if (token is null)
            {
                reasons.Add(ReasonNoToken);
            }
            else if (!ProofOfPlaySigner.Verify(token, signingKey) || token.Outcome != WinOutcome)
            {
                // A failed HMAC OR a non-win outcome both mean the token is not a valid win proof.
                reasons.Add(ReasonInvalidToken);
            }
            else if (!ProofOfPlaySigner.MatchesScenario(token, currentScenarioHash))
            {
                reasons.Add(ReasonStaleToken);
            }

            // ── Min-quality + consent floor (independent of the token; all failures listed). ──
            if (manifest is null || string.IsNullOrEmpty(manifest.ThumbnailFile))
                reasons.Add(ReasonNoThumbnail);

            // Review P6: trim before measuring so an all-whitespace description can't satisfy the floor.
            if (manifest is null || (manifest.Description?.Trim().Length ?? 0) < MinDescriptionLength)
                reasons.Add(ReasonShortDesc);

            if (manifest is null || manifest.Screenshots is null || manifest.Screenshots.Count < 1)
                reasons.Add(ReasonNoScreenshot);

            if (manifest is null || !manifest.IpConsent)
                reasons.Add(ReasonNoConsent);

            return new PublishGateResult(reasons.Count == 0, reasons);
        }
    }
}
