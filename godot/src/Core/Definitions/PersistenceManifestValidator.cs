#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The result of a <see cref="PersistenceManifestValidator"/> pass (Story 3.8, AR-39 / D-9) — pure, no logging, no
    /// throw. Carries a LIST of located <c>(FieldPath, Message)</c> errors (mirroring
    /// <see cref="UnitValidationResult"/>) so the Persistence Manifest editor can surface every offending key at once.
    /// <see cref="FieldPath"/> is the offending attribute key namespaced under <c>attributes</c> (e.g.
    /// <c>"attributes.hero.bogus"</c>); Message is the full located sentence. Godot-free so it runs in the Tier-1 test
    /// assembly and the sim layer. NO <see cref="Validated{T}"/> mint — this is a lightweight authoring-time gate, so
    /// the <see cref="ScenarioValidator"/>-only sole-minter allow-list is untouched.
    /// </summary>
    public readonly struct ManifestValidationResult
    {
        /// <summary>True when the manifest passed every check (no errors) — incl. a null manifest.</summary>
        public bool Ok => Errors.Count == 0;

        /// <summary>Every located field error found (NOT just the first — D-9). Empty when valid.</summary>
        public IReadOnlyList<(string FieldPath, string Message)> Errors { get; }

        internal ManifestValidationResult(IReadOnlyList<(string, string)> errors) => Errors = errors;

        /// <summary>The always-valid result (no errors) — a shared empty instance.</summary>
        public static readonly ManifestValidationResult Valid =
            new ManifestValidationResult(System.Array.Empty<(string, string)>());
    }

    /// <summary>
    /// The fail-closed content validator for an authored <see cref="PersistenceManifest"/> (Story 3.8, AR-39 / AR-12).
    /// The editor checklist offers ONLY <see cref="PersistableAttributes.Eligible"/> attributes, so an unknown /
    /// mid-game / duplicate key can enter a manifest only by hand-editing the scenario JSON — this validator is the
    /// backstop that rejects exactly those (D-4). It runs at editor Save (badge + blocked Save) and its rule core is
    /// invoked FIRST-FAIL inside <see cref="ScenarioValidator.Validate"/> so a malformed manifest in a loaded scenario
    /// is rejected at the pre-tick D3 gate — the SAME rule, two gates, no duplication (D-3).
    ///
    /// <para><b>Design.</b> Returns ALL located errors like <see cref="UnitDefinitionValidator"/> (D-9), and does NOT
    /// mint <see cref="Validated{T}"/> (mirrors <c>UnitDefinitionValidator</c>). A NULL manifest ⇒ Valid (persistence
    /// simply not configured); a DISABLED manifest ⇒ Valid (inert — no contract to enforce, and the recovery path out
    /// of a hand-edited invalid manifest). An enabled-with-zero-attributes manifest ⇒ Valid (an empty profile shape is
    /// legal — only unknown / ineligible / duplicate keys are rejected).</para>
    ///
    /// <para><b>Determinism.</b> Pure C#, Godot-free — reads authoring strings, reports strings; touches no sim array
    /// and moves no checksum (the authoring-time posture).</para>
    /// </summary>
    public sealed class PersistenceManifestValidator
    {
        /// <summary>
        /// Validate <paramref name="manifest"/>, returning EVERY located field error (D-9). A null manifest is Valid.
        /// Rules over <see cref="PersistenceManifest.Attributes"/>: eligible ⇒ ok; a KNOWN mid-game key ⇒ located
        /// "mid-game-only state cannot be persisted (&lt;reason&gt;)"; any other key ⇒ located "unknown attribute";
        /// a key selected more than once ⇒ located "selected more than once" (once, on its second occurrence).
        /// Pure — never throws, never logs.
        /// </summary>
        public ManifestValidationResult Validate(PersistenceManifest? manifest)
        {
            // A null manifest ⇒ persistence not configured. A DISABLED manifest ⇒ inert: nothing is ever persisted, so
            // its attribute contents cannot leak mid-game state and must not gate the scenario. Skipping validation when
            // disabled is both correct (no contract to enforce) AND the recovery path out of an otherwise un-saveable
            // hand-edited manifest: the checklist offers only eligible attributes, so a creator who inherits a scenario
            // whose JSON carries an unknown/mid-game key can flip persistence OFF and save. Re-enabling re-asserts the gate.
            if (manifest is null || !manifest.Enabled) return ManifestValidationResult.Valid;

            var errors = new List<(string, string)>();
            List<string> attrs = manifest.Attributes ?? new List<string>();

            // Track which keys we have already seen, so a duplicate reports exactly once (on the repeat), while the
            // first occurrence is still validated for eligibility. Small closed selection → a plain list scan (no
            // Dictionary enumeration — the sim-layer determinism rule).
            var seen = new List<string>();
            for (int i = 0; i < attrs.Count; i++)
            {
                string key = attrs[i] ?? "";

                if (seen.Contains(key))
                {
                    errors.Add(($"attributes.{key}", Located(key, "selected more than once.")));
                    continue;
                }
                seen.Add(key);

                if (PersistableAttributes.IsEligible(key))
                    continue;   // an eligible key is fine

                string? reason = PersistableAttributes.IneligibleReason(key);
                if (reason != null)
                    errors.Add(($"attributes.{key}",
                        Located(key, $"mid-game-only state cannot be persisted ({reason}).")));
                else
                    errors.Add(($"attributes.{key}", Located(key, $"unknown attribute '{key}'.")));
            }

            return errors.Count == 0 ? ManifestValidationResult.Valid : new ManifestValidationResult(errors);
        }

        /// <summary>The located error idiom — names the manifest field path + reason, mirroring
        /// <see cref="UnitDefinitionValidator"/>'s <c>Located</c>.</summary>
        private static string Located(string key, string reason) =>
            $"persistence_manifest.attributes.{key}: {reason}";
    }
}
