#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.AI
{
    /// <summary>
    /// Story 8.5 — the Godot-free apply-and-gate core for an AI balance suggestion. It is the SINGLE source of truth
    /// for the closed set of tunable fields (<see cref="TunableFields"/>), shared by three consumers so they cannot
    /// drift: the prompt builder enumerates it, <see cref="LLMService.ValidateBalanceReport"/> rejects any field outside
    /// it, and <see cref="TryApply"/>'s snake_case→setter switch handles exactly it.
    ///
    /// <para><b>The load-bearing decision (apply = clone → set → EXISTING gate).</b> <see cref="TryApply"/> never
    /// mutates the live target: it clones it (serialize via <see cref="FactionWriter.SerializeUnitClean"/> →
    /// deserialize with <see cref="FactionDefinition.JsonOptions"/> — a deterministic plain-float round-trip, NO second
    /// quantize path), sets the proposed value on the CLONE, and re-gates it through the SAME
    /// <see cref="UnitDefinitionValidator"/> hand-authored unit edits pass. A rejected value leaves the original
    /// untouched and returns a located error. Quantization still happens ONLY later at the existing load-time
    /// float→Fixed boundary — <c>EntityWorld.ApplyUnitDefinition</c> for the def-mapped stats, the
    /// <c>EntityWorld.Create</c> ctor args for <c>hp</c>/<c>speed</c> (every spawn site quantizes
    /// <c>Fixed.FromFloat(def.Speed)</c> there) — so an applied stat hashes identically to a hand-authored one by
    /// construction; this applier introduces no quantize path of its own.</para>
    /// </summary>
    public static class BalanceSuggestionApplier
    {
        /// <summary>
        /// The closed set of numeric fields a balance suggestion may target — the SINGLE definition shared by the prompt
        /// builder, the validate router, and the apply mapper. Unit numeric stats plus hero growth fields (the schema
        /// exercised by Story 8.4). A member added here MUST also be handled by <see cref="SetField"/>/<see cref="TryReadField"/>
        /// and enumerated by <see cref="LLMService.BuildBalanceAnalysisPrompt"/> — the prompt staleness-guard test
        /// (BalanceAnalysisPromptTests) and the SetField coverage-guard test (BalanceAnalysisApplyTests) fail otherwise.
        /// DW-382 (recorded decision 2026-07-30, "add speed only, drop mesh_scale"): movement <c>speed</c> IS tunable —
        /// it quantizes at the <c>EntityWorld.Create</c> ctor arg (the same single load-time float→Fixed boundary the
        /// spawn sites already use), so an applied speed hashes identically to a hand-authored one; cosmetic
        /// <c>mesh_scale</c> is NOT a balance lever and was dropped; nullable/derived <c>xp_bounty</c> stays out.
        /// </summary>
        public static readonly IReadOnlyList<string> TunableFields = new[]
        {
            // ── unit numeric stats ──
            "attack_damage", "hp", "speed", "armor", "attack_range", "attack_speed", "splash_radius", "vision_range",
            "cost_ore", "cost_crystal", "supply", "train_time", "max_energy", "collision_radius",
            "projectile_speed",
            // ── hero growth stats ──
            "hero.max_level", "hero.base_xp", "hero.xp_growth", "hero.xp_per_kill", "hero.xp_share_radius",
            "hero.health_per_level", "hero.damage_per_level", "hero.armor_per_level",
        };

        private static readonly HashSet<string> _tunableSet = new(TunableFields, StringComparer.Ordinal);

        /// <summary>True when <paramref name="field"/> is a member of the closed tunable set (Ordinal).</summary>
        public static bool IsTunable(string? field) => field != null && _tunableSet.Contains(field);

        /// <summary>
        /// Gate <paramref name="proposed"/> for <paramref name="field"/> on a CLONE of <paramref name="target"/> through
        /// the same <see cref="UnitDefinitionValidator"/> hand-authored edits use, WITHOUT touching the original. On
        /// success returns <c>(candidate, null)</c> — a validated clone with the one field set, ready to commit through
        /// the panel's undo/Save seam and quantize at <c>EntityWorld.ApplyUnitDefinition</c>. On any failure (unknown
        /// field, non-hero target for a <c>hero.*</c> field, or an out-of-Fixed-range / non-finite / invalid value)
        /// returns <c>(null, located error)</c> and the target is unchanged.
        /// </summary>
        /// <param name="siblings">The faction roster (for the duplicate-id rule). The <paramref name="target"/>'s own
        /// entry is filtered out before validation so keeping the target's id is never flagged as a new duplicate —
        /// mirroring the raw-JSON-pane save path.</param>
        public static (UnitDefinition? candidate, string? error) TryApply(
            UnitDefinition target, string field, double proposed, IReadOnlyList<UnitDefinition>? siblings)
        {
            if (target == null)
                return (null, "no target unit to apply the suggestion to.");
            if (string.IsNullOrEmpty(field) || !_tunableSet.Contains(field))
                return (null, Located(target.Id, field ?? "", "is not a tunable balance field."));

            // Clone via the deterministic authorable-fields round-trip (no Parsed getters, no ballooning, plain float —
            // the same bytes the raw-JSON hatch authors). A rejected apply thus never reaches the original object.
            UnitDefinition candidate;
            try
            {
                string json = FactionWriter.SerializeUnitClean(target);
                candidate = JsonSerializer.Deserialize<UnitDefinition>(json, FactionDefinition.JsonOptions)
                    ?? throw new InvalidOperationException("clone deserialized to null.");
            }
            catch (Exception ex)
            {
                return (null, Located(target.Id, field, $"could not be cloned for apply ({ex.Message})."));
            }

            string? setError = SetField(candidate, field, proposed);
            if (setError != null)
                return (null, setError);

            // Filter the target's own entry from the sibling list (the clone shares its id — keeping an existing id is
            // not a "new duplicate"). A genuine second unit sharing the id is still caught.
            IReadOnlyList<UnitDefinition>? effectiveSiblings = siblings;
            if (siblings != null)
            {
                var filtered = new List<UnitDefinition>(siblings.Count);
                for (int i = 0; i < siblings.Count; i++)
                    if (!ReferenceEquals(siblings[i], target)) filtered.Add(siblings[i]);
                effectiveSiblings = filtered;
            }

            UnitValidationResult result =
                new UnitDefinitionValidator().Validate(candidate, null, null, null, effectiveSiblings, "unit");
            return result.Ok ? (candidate, null) : (null, JoinErrors(result.Errors));
        }

        // ── The snake_case → setter switch (handles exactly TunableFields) ─────────

        /// <summary>Set the tunable <paramref name="field"/> on <paramref name="u"/> from the proposed value. A
        /// <c>hero.*</c> field on a non-hero unit (no <c>hero</c> block) is a located reject; the range/finite gate is the
        /// validator's job (this only writes the value so the validator can locate the offending field).</summary>
        private static string? SetField(UnitDefinition u, string field, double proposed)
        {
            switch (field)
            {
                // ── unit float stats ──
                case "attack_damage":    u.AttackDamage    = F(proposed); return null;
                case "hp":               u.Hp              = F(proposed); return null;
                case "speed":            u.Speed           = F(proposed); return null;
                case "armor":            u.Armor           = F(proposed); return null;
                case "attack_range":     u.AttackRange     = F(proposed); return null;
                case "attack_speed":     u.AttackSpeed     = F(proposed); return null;
                case "splash_radius":    u.SplashRadius    = F(proposed); return null;
                case "vision_range":     u.VisionRange     = F(proposed); return null;
                case "train_time":       u.TrainTime       = F(proposed); return null;
                case "max_energy":       u.MaxEnergy       = F(proposed); return null;
                case "collision_radius": u.CollisionRadius = F(proposed); return null;
                case "projectile_speed": u.ProjectileSpeed = F(proposed); return null;

                // ── unit int stats ──
                case "cost_ore":     u.CostOre     = I(proposed); return null;
                case "cost_crystal": u.CostCrystal = I(proposed); return null;
                case "supply":       u.Supply      = I(proposed); return null;

                // ── hero growth stats (require a hero block) ──
                case "hero.max_level":        return SetHero(u, field, h => h.MaxLevel       = I(proposed));
                case "hero.base_xp":          return SetHero(u, field, h => h.BaseXp         = F(proposed));
                case "hero.xp_growth":        return SetHero(u, field, h => h.XpGrowth       = F(proposed));
                case "hero.xp_per_kill":      return SetHero(u, field, h => h.XpPerKill      = F(proposed));
                case "hero.xp_share_radius":  return SetHero(u, field, h => h.XpShareRadius  = F(proposed));
                case "hero.health_per_level": return SetHero(u, field, h => h.HealthPerLevel = F(proposed));
                case "hero.damage_per_level": return SetHero(u, field, h => h.DamagePerLevel = F(proposed));
                case "hero.armor_per_level":  return SetHero(u, field, h => h.ArmorPerLevel  = F(proposed));

                default:
                    return Located(u.Id, field, "is not a tunable balance field.");
            }
        }

        private static string? SetHero(UnitDefinition u, string field, Action<HeroDefinition> set)
        {
            if (u.Hero == null)
                return Located(u.Id, field, "targets a hero growth stat but this unit has no 'hero' block.");
            set(u.Hero);
            return null;
        }

        // ── The snake_case → reader (the counterpart of SetField; handles exactly TunableFields) ──

        /// <summary>
        /// Read the current value of a tunable <paramref name="field"/> from <paramref name="u"/> — the counterpart of
        /// <see cref="SetField"/>, so a caller can show the unit's REAL current value (not the model's unverified
        /// <c>current</c> claim) and echo the value actually applied (int fields round). Returns <c>false</c> for an
        /// unknown field or a <c>hero.*</c> field on a unit with no <c>hero</c> block.
        /// </summary>
        public static bool TryReadField(UnitDefinition u, string field, out double value)
        {
            value = 0;
            if (u == null || string.IsNullOrEmpty(field)) return false;
            switch (field)
            {
                case "attack_damage":    value = u.AttackDamage;    return true;
                case "hp":               value = u.Hp;              return true;
                case "speed":            value = u.Speed;           return true;
                case "armor":            value = u.Armor;           return true;
                case "attack_range":     value = u.AttackRange;     return true;
                case "attack_speed":     value = u.AttackSpeed;     return true;
                case "splash_radius":    value = u.SplashRadius;    return true;
                case "vision_range":     value = u.VisionRange;     return true;
                case "train_time":       value = u.TrainTime;       return true;
                case "max_energy":       value = u.MaxEnergy;       return true;
                case "collision_radius": value = u.CollisionRadius; return true;
                case "projectile_speed": value = u.ProjectileSpeed; return true;

                case "cost_ore":     value = u.CostOre;     return true;
                case "cost_crystal": value = u.CostCrystal; return true;
                case "supply":       value = u.Supply;      return true;

                case "hero.max_level":        return ReadHero(u, h => h.MaxLevel,       out value);
                case "hero.base_xp":          return ReadHero(u, h => h.BaseXp,         out value);
                case "hero.xp_growth":        return ReadHero(u, h => h.XpGrowth,       out value);
                case "hero.xp_per_kill":      return ReadHero(u, h => h.XpPerKill,      out value);
                case "hero.xp_share_radius":  return ReadHero(u, h => h.XpShareRadius,  out value);
                case "hero.health_per_level": return ReadHero(u, h => h.HealthPerLevel, out value);
                case "hero.damage_per_level": return ReadHero(u, h => h.DamagePerLevel, out value);
                case "hero.armor_per_level":  return ReadHero(u, h => h.ArmorPerLevel,  out value);

                default: return false;
            }
        }

        private static bool ReadHero(UnitDefinition u, Func<HeroDefinition, double> get, out double value)
        {
            value = 0;
            if (u.Hero == null) return false;
            value = get(u.Hero);
            return true;
        }

        // ── Value conversion (the validator, not this, is the finite/range gate) ───

        /// <summary>double→float verbatim: NaN/Inf/out-of-range pass through so the validator can locate + reject them.</summary>
        private static float F(double v) => (float)v;

        /// <summary>double→int for the count fields. A non-finite or out-of-int-range proposal is coerced to a value the
        /// validator rejects (NaN → -1 so the negative-cost / [0,32768) gate fires with a located error, never a silent
        /// NaN→0 pass). A finite in-range value is rounded to the nearest int.</summary>
        private static int I(double v)
        {
            if (double.IsNaN(v)) return -1;
            if (v >= int.MaxValue) return int.MaxValue;
            if (v <= int.MinValue) return int.MinValue;
            return (int)Math.Round(v);
        }

        // ── Located-error helpers (mirror UnitDefinitionValidator's message shape) ──

        private static string Located(string? id, string field, string reason) =>
            $"unit '{id}'.{field}: {reason}";

        private static string JoinErrors(IReadOnlyList<(string FieldPath, string Message)> errors)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < errors.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append(errors[i].Message);
            }
            return sb.ToString();
        }
    }
}
