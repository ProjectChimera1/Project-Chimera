// Story 9.12 — vitest suite for the server-side validateHeroProfile mirror. Driven off the SAME shared fixture the C#
// HeroProfileValidatorTests reads (fixtures/validation-cases.json), so the two implementations are proven in sync
// against one source of truth without a live Nakama. Also asserts the range predicate + the ceiling constant.
import { readFileSync } from 'node:fs';
import { describe, test, expect } from 'vitest';
import {
  validateHeroProfile,
  isLevelXpInRange,
  XP_CEILING_RAW,
  type HeroProfile,
  type ProfileInvalidReason,
} from '../src/validation.ts';

interface FixtureCase {
  name: string;
  expect_valid: boolean;
  expect_reason: ProfileInvalidReason;
  profile: HeroProfile;
}

const fixture = JSON.parse(
  readFileSync(new URL('./fixtures/validation-cases.json', import.meta.url), 'utf8'),
) as { xp_ceiling_raw: number; cases: FixtureCase[]; ts_only_cases: FixtureCase[] };

describe('validateHeroProfile — shared C#<->TS oracle', () => {
  test.each(fixture.cases.map((c) => [c.name, c] as [string, FixtureCase]))(
    'case %s matches the oracle',
    (_name, c) => {
      const result = validateHeroProfile(c.profile);
      expect(result.valid).toBe(c.expect_valid);
      expect(result.reason).toBe(c.expect_reason);
    },
  );

  test('the oracle covers a valid case AND every invalid reason class', () => {
    const reasons = new Set<string>();
    let sawValid = false;
    for (const c of fixture.cases) {
      if (c.expect_valid) sawValid = true;
      else reasons.add(c.expect_reason);
    }
    expect(sawValid).toBe(true);
    expect(reasons).toContain('identity');
    expect(reasons).toContain('range');
    expect(reasons).toContain('inventory');
    expect(reasons).toContain('attributes');
  });
});

describe('validateHeroProfile — TS-only Int32/structural boundary (P1)', () => {
  // These forged payloads cannot round-trip through the C# PlayerProfile (System.Text.Json throws at the boundary:
  // out-of-Int32 raw, non-array container). C# is fail-closed against them at deserialization; the TS validator (which
  // parses lenient JSON) must reject them EXPLICITLY here so a naive `raw | 0` truncation / `[]` coercion can't smuggle
  // a value that C# would reject — the exact C#<->TS parity break P1 fixes.
  test.each(fixture.ts_only_cases.map((c) => [c.name, c] as [string, FixtureCase]))(
    'ts-only case %s is rejected fail-closed',
    (_name, c) => {
      const result = validateHeroProfile(c.profile);
      expect(result.valid).toBe(false);
      expect(result.reason).toBe(c.expect_reason);
    },
  );

  test('a raw at exactly Int32 max within range bounds is still an integer boundary we accept', () => {
    // sanity: 2^31-1 is a safe int32, but above the xp ceiling, so it is a `range` rejection, NOT a truncation pass.
    expect(validateHeroProfile({
      profile_id: 'g#1', hero_def_id: 'g',
      values: [{ key: 'hero.level', raw: 1 }, { key: 'hero.xp', raw: 2147483647 }], inventory: [],
    }).reason).toBe('range');
  });
});

describe('range predicate + ceiling constant', () => {
  test('isLevelXpInRange matches the C# predicate', () => {
    expect(isLevelXpInRange(0, 0)).toBe(true);
    expect(isLevelXpInRange(3, XP_CEILING_RAW)).toBe(true); // inclusive
    expect(isLevelXpInRange(-1, 0)).toBe(false);
    expect(isLevelXpInRange(1, -1)).toBe(false);
    expect(isLevelXpInRange(1, XP_CEILING_RAW + 1)).toBe(false);
  });

  test('XP_CEILING_RAW equals Fixed(30000) in 16.16 and the fixture constant', () => {
    expect(XP_CEILING_RAW).toBe(1966080000);
    expect(XP_CEILING_RAW).toBe(fixture.xp_ceiling_raw);
  });

  test('a null / non-object profile is rejected as identity', () => {
    expect(validateHeroProfile(null).reason).toBe('identity');
    expect(validateHeroProfile(undefined).reason).toBe('identity');
  });
});
