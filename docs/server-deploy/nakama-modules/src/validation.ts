// Story 9.12 — the canonical hero-profile validity rules, server-side (TypeScript).
//
// This is the server-side MIRROR of godot/src/Core/Definitions/HeroProfileValidator.cs. The two implementations MUST
// stay in sync rule-for-rule — if you change one, change the other. There is no live-Nakama test in CI, so the C# xUnit
// suite and this file's vitest suite are the only proof the rules match; BOTH are driven off the single shared fixture
// docs/server-deploy/nakama-modules/test/fixtures/validation-cases.json, so they cannot silently drift.
//
// The profile JSON shape is exactly what C# System.Text.Json emits for PlayerProfile (snake_case property names):
//   { profile_id, hero_def_id, faction_id, display_name, signature_ability,
//     values:    [ { key, raw } ],           // hero.level as its int, hero.xp as its Fixed 16.16 raw
//     inventory: [ { item_id, charges, slot } ] }
//
// Int32 boundary (P1): the C# side stores every raw/charges/slot as a 32-bit `int`, so System.Text.Json REJECTS (throws)
// any value outside Int32 or any non-array `values`/`inventory` at the deserialization boundary. JS numbers are float64,
// so a forged RPC payload could smuggle 2^32 (which a naive `raw | 0` would wrap to 0) or a non-array container. This
// validator therefore rejects any numeric that is not a safe integer inside Int32 range, and rejects a present-but-not-
// array `values`/`inventory` — matching the C# boundary exactly (fail-closed, never truncate).

/** Fixed(30000) in 16.16 raw = 30000 * 65536. MUST equal HeroXpSystem.XpCeiling.Raw on the C# side. */
export const XP_CEILING_RAW = 30000 * 65536; // 1_966_080_000

const INT32_MIN = -2147483648;
const INT32_MAX = 2147483647;

export type ProfileInvalidReason = 'none' | 'identity' | 'range' | 'inventory' | 'attributes';

export interface ProfileAttributeValue { key: string; raw: number; }
export interface ProfileInventoryItem { item_id: string; charges: number; slot: number; }
export interface HeroProfile {
  profile_id?: string;
  hero_def_id?: string;
  faction_id?: string;
  display_name?: string;
  signature_ability?: string;
  values?: ProfileAttributeValue[];
  inventory?: ProfileInventoryItem[];
}

export interface ProfileValidation { valid: boolean; reason: ProfileInvalidReason; }

function isBlank(s: unknown): boolean {
  return typeof s !== 'string' || s.trim().length === 0;
}

/** A number the C# `int` boundary would accept: a safe integer within signed 32-bit range. A float64 outside this
 * range (e.g. a forged 2^32 xp) would silently wrap under `| 0` — so we reject it instead of truncating (P1). */
function isInt32(v: unknown): v is number {
  return typeof v === 'number' && Number.isSafeInteger(v) && v >= INT32_MIN && v <= INT32_MAX;
}

/** Linear lookup of a persisted raw by key (mirrors PlayerProfile.RawOf); 0 when absent. Returns the value UNCOERCED
 * so the caller can reject a non-Int32 raw rather than truncating it. */
function rawOf(values: ProfileAttributeValue[], key: string): number {
  for (let i = 0; i < values.length; i++) {
    if (values[i] && values[i].key === key) return values[i].raw;
  }
  return 0;
}

/** The DW-12 level/xp range rule — mirrors HeroProfileValidator.IsLevelXpInRange EXACTLY (bounds only; the Int32
 * guard is applied separately by validateHeroProfile before this is consulted). */
export function isLevelXpInRange(level: number, xpRaw: number): boolean {
  return level >= 0 && xpRaw >= 0 && xpRaw <= XP_CEILING_RAW;
}

/**
 * Validate a hero profile against the canonical rule set, in the SAME order as the C# validator so the returned reason
 * matches: identity -> (structural) -> range -> attributes -> inventory. Never a silent clamp or truncation — reject
 * fail-closed.
 */
export function validateHeroProfile(profile: HeroProfile | null | undefined): ProfileValidation {
  if (!profile || typeof profile !== 'object') return { valid: false, reason: 'identity' };

  // 1) Identity.
  if (isBlank(profile.profile_id) || isBlank(profile.hero_def_id)) {
    return { valid: false, reason: 'identity' };
  }

  // 2) Structural — a PRESENT `values`/`inventory` that is not an array is rejected (C# System.Text.Json throws on a
  //    non-array here → rejects at the boundary; we mirror that instead of coercing it to []).
  if (profile.values !== undefined && profile.values !== null && !Array.isArray(profile.values)) {
    return { valid: false, reason: 'attributes' };
  }
  if (profile.inventory !== undefined && profile.inventory !== null && !Array.isArray(profile.inventory)) {
    return { valid: false, reason: 'inventory' };
  }

  const values: ProfileAttributeValue[] = Array.isArray(profile.values) ? profile.values : [];
  const inventory: ProfileInventoryItem[] = Array.isArray(profile.inventory) ? profile.inventory : [];

  // 3) Range (level / xp) — each raw must be a valid Int32 (a forged out-of-range value is rejected, not wrapped) AND
  //    within the DW-12 bounds. Checked BEFORE attributes so a bad level/xp reports `range`.
  const level = rawOf(values, 'hero.level');
  const xpRaw = rawOf(values, 'hero.xp');
  if (!isInt32(level) || !isInt32(xpRaw) || !isLevelXpInRange(level, xpRaw)) {
    return { valid: false, reason: 'range' };
  }

  // 4) Attributes — every raw a valid Int32 >= 0, no duplicate keys.
  for (let i = 0; i < values.length; i++) {
    const v = values[i];
    if (!v || !isInt32(v.raw) || v.raw < 0) return { valid: false, reason: 'attributes' };
    for (let j = i + 1; j < values.length; j++) {
      if (values[j] && values[j].key === v.key) return { valid: false, reason: 'attributes' };
    }
  }

  // 5) Inventory — every charge a valid Int32 >= 0, every slot a valid Int32, no duplicate NON-NEGATIVE slot (a legacy
  //    slot of -1 is the "first free" sentinel).
  for (let i = 0; i < inventory.length; i++) {
    const it = inventory[i];
    if (!it || !isInt32(it.charges) || it.charges < 0) return { valid: false, reason: 'inventory' };
    if (!isInt32(it.slot)) return { valid: false, reason: 'inventory' };
    const slot = it.slot;
    if (slot < 0) continue;
    for (let j = i + 1; j < inventory.length; j++) {
      if (inventory[j] && inventory[j].slot === slot) return { valid: false, reason: 'inventory' };
    }
  }

  return { valid: true, reason: 'none' };
}
