// DW-438 — C#->TS WIRE round-trip: feed the TS validator + BOTH RPC handlers the EXACT bytes the C# client puts on
// the wire (`JsonSerializer.Serialize(profile)` in NakamaService.WriteHeroProfileViaRpcAsync), not a hand-authored
// JSON literal. The fixture fixtures/csharp-serialized-profile.json is PINNED byte-for-byte by the C# test
// PlayerProfileWireParityTests (godot/ProjectChimera.Sim.Tests, embedded copy), so any wire-format drift — a
// JsonPropertyName rename, a Fixed raw-encoding change, a converter change — breaks the C# byte assertion, forces a
// fixture regeneration, and lands HERE where the TS side must still accept it. Together the two suites prove the live
// client->server boundary, with all rule-parity tests left untouched.
import { readFileSync } from 'node:fs';
import { describe, test, expect } from 'vitest';
import { validateHeroProfile } from '../src/validation.ts';
import {
  handleWriteHeroProfile,
  handleAttestHeroProfile,
  HERO_COLLECTION,
  HERO_KEY,
  MAX_RAW_PAYLOAD_BYTES,
} from '../src/main.ts';

// The raw wire bytes exactly as C# serialized them (single compact line; trim() only neutralizes a trailing newline
// an editor or git could append — the JSON text itself is untouched).
const wire = readFileSync(new URL('./fixtures/csharp-serialized-profile.json', import.meta.url), 'utf8').trim();

// Minimal in-memory nk mock (same surface as handlers.test.ts — kept local so the suites stay independent).
interface StoredObj { collection: string; key: string; userId: string; version: string; value: { [k: string]: any } }
function mockNk() {
  const writes: any[] = [];
  const store = new Map<string, StoredObj>();
  return {
    writes,
    storageWrite(reqs: any[]) {
      const acks = [];
      for (const r of reqs) {
        writes.push(r);
        store.set(`${r.collection}/${r.key}/${r.userId}`, {
          collection: r.collection, key: r.key, userId: r.userId ?? '', version: 'v1', value: r.value,
        });
        acks.push({ collection: r.collection, key: r.key, userId: r.userId ?? '', version: 'v1' });
      }
      return acks;
    },
    storageRead(reqs: any[]) {
      const out: StoredObj[] = [];
      for (const r of reqs) {
        const hit = store.get(`${r.collection}/${r.key}/${r.userId}`);
        if (hit) out.push(hit);
      }
      return out;
    },
  };
}

const USER = 'user-wire-1';

describe('DW-438: genuinely C#-serialized PlayerProfile bytes on the TS boundary', () => {
  test('the fixture is the raw one-line wire payload (sanity: parseable, plausible size)', () => {
    expect(wire.length).toBeGreaterThan(0);
    expect(wire.includes('\n')).toBe(false); // compact System.Text.Json output — a single line
    expect(wire.length).toBeLessThanOrEqual(MAX_RAW_PAYLOAD_BYTES); // a real profile never trips the DW-436 guard
    expect(() => JSON.parse(wire)).not.toThrow();
  });

  test('validateHeroProfile accepts the exact C# wire bytes', () => {
    const result = validateHeroProfile(JSON.parse(wire));
    expect(result.valid).toBe(true);
    expect(result.reason).toBe('none');
  });

  test('the parsed wire carries the full snake_case shape the TS contract expects', () => {
    // Field-level pin: if C# renames a JsonPropertyName, the C# byte test forces a fixture regen and THIS fails.
    const p = JSON.parse(wire);
    expect(Object.keys(p).sort()).toEqual(
      ['display_name', 'faction_id', 'hero_def_id', 'inventory', 'profile_id', 'signature_ability', 'values'],
    );
    expect(p.profile_id).toBe('grommash#wire-1');
    expect(p.hero_def_id).toBe('grommash');
    // hero.xp rides as its Fixed 16.16 raw — 12.5 in 16.16 = 819200 (the Fixed-encoding pin).
    expect(p.values).toContainEqual({ key: 'hero.xp', raw: 819200 });
    expect(p.values).toContainEqual({ key: 'hero.level', raw: 7 });
    // inventory entries carry the converter's exact key set, slot-faithful.
    expect(Object.keys(p.inventory[0]).sort()).toEqual(['charges', 'item_id', 'slot']);
    expect(p.inventory).toContainEqual({ item_id: 'ring-of-haste', charges: 0, slot: 2 });
  });

  test('rpc_write_hero_profile accepts the exact C# wire bytes and stores them LOSSLESSLY', () => {
    const nk = mockNk();
    const reply = JSON.parse(handleWriteHeroProfile(nk as any, USER, wire));
    expect(reply.ok).toBe(true);
    expect(nk.writes).toHaveLength(1);
    const w = nk.writes[0];
    expect(w.collection).toBe(HERO_COLLECTION);
    expect(w.key).toBe(HERO_KEY);
    // sanitizeProfile must preserve EVERY field the C# serializer actually emits — a field the whitelist forgot
    // would be silently dropped from storage and this deep-equality would fail.
    expect(w.value).toEqual(JSON.parse(wire));
  });

  test('full wire round trip: C# bytes -> write RPC -> stored object -> attest RPC -> attested:true', () => {
    const nk = mockNk();
    expect(JSON.parse(handleWriteHeroProfile(nk as any, USER, wire)).ok).toBe(true);
    const attest = JSON.parse(
      handleAttestHeroProfile(nk as any, USER, JSON.stringify({ profileId: 'grommash#wire-1' })),
    );
    expect(attest.attested).toBe(true);
    expect(attest.reason).toBe('none');
  });
});
