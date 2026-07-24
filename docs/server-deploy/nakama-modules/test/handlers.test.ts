// Story 9.12 — vitest suite for the extracted RPC handler bodies (handleWriteHeroProfile / handleAttestHeroProfile),
// exercised with a MOCKED `nk` runtime — no live Nakama. Proves the server-only write contract (owner-read /
// no-client-write permissions, no write on an invalid payload) and the attestation logic (not_found / valid / invalid /
// id-mismatch) that the client's OnlineHeroLaunchGate depends on.
import { describe, test, expect } from 'vitest';
import { handleWriteHeroProfile, handleAttestHeroProfile, HERO_COLLECTION, HERO_KEY } from '../src/main.ts';

// ── A minimal in-memory mock of the Nakama runtime storage surface ──
interface WriteReq {
  collection: string; key: string; userId?: string;
  value: { [k: string]: any }; permissionRead?: number; permissionWrite?: number;
}
interface StoredObj { collection: string; key: string; userId: string; version: string; value: { [k: string]: any }; }

function mockNk(seed: StoredObj[] = []) {
  const writes: WriteReq[] = [];
  const store = new Map<string, StoredObj>();
  for (const o of seed) store.set(`${o.collection}/${o.key}/${o.userId}`, o);
  return {
    writes,
    store,
    storageWrite(reqs: WriteReq[]) {
      const acks = [];
      for (const r of reqs) {
        writes.push(r);
        const key = `${r.collection}/${r.key}/${r.userId}`;
        store.set(key, { collection: r.collection, key: r.key, userId: r.userId ?? '', version: 'v1', value: r.value });
        acks.push({ collection: r.collection, key: r.key, userId: r.userId ?? '', version: 'v1' });
      }
      return acks;
    },
    storageRead(reqs: { collection: string; key: string; userId?: string }[]) {
      const out: StoredObj[] = [];
      for (const r of reqs) {
        const hit = store.get(`${r.collection}/${r.key}/${r.userId}`);
        if (hit) out.push(hit);
      }
      return out;
    },
  };
}

const USER = 'user-123';

function validProfileJson(profileId = 'grommash#online') {
  return JSON.stringify({
    profile_id: profileId,
    hero_def_id: 'grommash',
    values: [{ key: 'hero.level', raw: 3 }, { key: 'hero.xp', raw: 786432 }],
    inventory: [],
  });
}

describe('handleWriteHeroProfile', () => {
  test('valid payload writes an owner-read / no-client-write object and returns ok', () => {
    const nk = mockNk();
    const reply = JSON.parse(handleWriteHeroProfile(nk as any, USER, validProfileJson()));

    expect(reply.ok).toBe(true);
    expect(reply.version).toBe('v1');
    expect(nk.writes).toHaveLength(1);
    const w = nk.writes[0];
    expect(w.collection).toBe(HERO_COLLECTION);
    expect(w.key).toBe(HERO_KEY);
    expect(w.userId).toBe(USER);          // written for the authenticated caller
    expect(w.permissionRead).toBe(1);     // owner-read
    expect(w.permissionWrite).toBe(0);    // NO client write
  });

  test('invalid payload writes NOTHING and returns ok:false with the reason', () => {
    const nk = mockNk();
    const bad = JSON.stringify({ profile_id: '', hero_def_id: 'grommash' }); // blank id → identity
    const reply = JSON.parse(handleWriteHeroProfile(nk as any, USER, bad));

    expect(reply.ok).toBe(false);
    expect(reply.reason).toBe('identity');
    expect(nk.writes).toHaveLength(0);    // nothing stored server-side
  });

  test('an out-of-range xp payload is rejected, nothing written', () => {
    const nk = mockNk();
    const bad = JSON.stringify({
      profile_id: 'g#online', hero_def_id: 'grommash',
      values: [{ key: 'hero.level', raw: 1 }, { key: 'hero.xp', raw: 1966080001 }],
    });
    const reply = JSON.parse(handleWriteHeroProfile(nk as any, USER, bad));
    expect(reply.ok).toBe(false);
    expect(reply.reason).toBe('range');
    expect(nk.writes).toHaveLength(0);
  });

  test('malformed JSON is rejected as bad_json, nothing written', () => {
    const nk = mockNk();
    const reply = JSON.parse(handleWriteHeroProfile(nk as any, USER, '{not json'));
    expect(reply.ok).toBe(false);
    expect(reply.reason).toBe('bad_json');
    expect(nk.writes).toHaveLength(0);
  });

  test('P6: extra/junk keys (top-level AND nested) are DROPPED from the stored object', () => {
    const nk = mockNk();
    const payload = JSON.stringify({
      profile_id: 'grommash#online', hero_def_id: 'grommash', faction_id: 'rebels', display_name: 'G',
      values: [{ key: 'hero.level', raw: 3, junk_nested: 'x' }],
      inventory: [{ item_id: 'ring', charges: 0, slot: 0, junk_nested: 1 }],
      evil_top_level: { a: 1 }, __proto__hack: 'no',
    });
    const reply = JSON.parse(handleWriteHeroProfile(nk as any, USER, payload));
    expect(reply.ok).toBe(true);
    const stored = nk.writes[0].value;
    // whitelist kept
    expect(Object.keys(stored).sort()).toEqual(['display_name', 'faction_id', 'hero_def_id', 'inventory', 'profile_id', 'values']);
    expect(stored.evil_top_level).toBeUndefined();
    // nested junk dropped
    expect(Object.keys(stored.values[0]).sort()).toEqual(['key', 'raw']);
    expect(Object.keys(stored.inventory[0]).sort()).toEqual(['charges', 'item_id', 'slot']);
  });

  test('P6: an oversized profile is rejected (too_large), nothing written', () => {
    const nk = mockNk();
    const bigValues = [];
    for (let i = 0; i < 2000; i++) bigValues.push({ key: `hero.attr_${i}`, raw: 1 });
    const payload = JSON.stringify({ profile_id: 'g#online', hero_def_id: 'grommash', values: [{ key: 'hero.level', raw: 1 }, ...bigValues], inventory: [] });
    const reply = JSON.parse(handleWriteHeroProfile(nk as any, USER, payload));
    expect(reply.ok).toBe(false);
    expect(reply.reason).toBe('too_large');
    expect(nk.writes).toHaveLength(0);
  });
});

describe('handleAttestHeroProfile', () => {
  function seedValid(profileId = 'grommash#online'): StoredObj {
    return {
      collection: HERO_COLLECTION, key: HERO_KEY, userId: USER, version: 'v1',
      value: {
        profile_id: profileId, hero_def_id: 'grommash',
        values: [{ key: 'hero.level', raw: 3 }, { key: 'hero.xp', raw: 786432 }], inventory: [],
      },
    };
  }

  test('no stored object → attested:false, reason not_found', () => {
    const nk = mockNk();
    const reply = JSON.parse(handleAttestHeroProfile(nk as any, USER, JSON.stringify({ profileId: 'x' })));
    expect(reply.attested).toBe(false);
    expect(reply.reason).toBe('not_found');
  });

  test('present, valid, matching id → attested:true', () => {
    const nk = mockNk([seedValid('grommash#online')]);
    const reply = JSON.parse(handleAttestHeroProfile(nk as any, USER, JSON.stringify({ profileId: 'grommash#online' })));
    expect(reply.attested).toBe(true);
  });

  test('valid stored object but requested id disagrees → attested:false, reason identity', () => {
    const nk = mockNk([seedValid('grommash#online')]);
    const reply = JSON.parse(handleAttestHeroProfile(nk as any, USER, JSON.stringify({ profileId: 'someone-else' })));
    expect(reply.attested).toBe(false);
    expect(reply.reason).toBe('identity');
  });

  test('stored object that fails validation → attested:false with the validation reason', () => {
    const bad: StoredObj = {
      collection: HERO_COLLECTION, key: HERO_KEY, userId: USER, version: 'v1',
      value: {
        profile_id: 'g#online', hero_def_id: 'grommash',
        values: [{ key: 'hero.level', raw: -1 }], inventory: [], // negative level → range
      },
    };
    const nk = mockNk([bad]);
    const reply = JSON.parse(handleAttestHeroProfile(nk as any, USER, JSON.stringify({ profileId: 'g#online' })));
    expect(reply.attested).toBe(false);
    expect(reply.reason).toBe('range');
  });

  test('empty request payload still attests a present valid object (id check skipped)', () => {
    const nk = mockNk([seedValid('grommash#online')]);
    const reply = JSON.parse(handleAttestHeroProfile(nk as any, USER, ''));
    expect(reply.attested).toBe(true);
  });
});
