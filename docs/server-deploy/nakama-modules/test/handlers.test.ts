// Story 9.12 — vitest suite for the extracted RPC handler bodies (handleWriteHeroProfile / handleAttestHeroProfile),
// exercised with a MOCKED `nk` runtime — no live Nakama. Proves the server-only write contract (owner-read /
// no-client-write permissions, no write on an invalid payload) and the attestation logic (not_found / valid / invalid /
// id-mismatch) that the client's OnlineHeroLaunchGate depends on.
import { describe, test, expect } from 'vitest';
import {
  handleWriteHeroProfile,
  handleAttestHeroProfile,
  HERO_COLLECTION,
  HERO_KEY,
  MAX_RAW_PAYLOAD_BYTES,
  MAX_PROFILE_ELEMENTS,
} from '../src/main.ts';

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

  // ── DW-436: raw-payload / element-count guards run BEFORE parse + the validator's O(n^2) scans ──

  test('DW-436: an oversized RAW payload is rejected even when its sanitized form would be small', () => {
    // Junk keys are DROPPED by sanitizeProfile, so before DW-436 this 40KB payload passed the post-validate
    // sanitized-size cap and was WRITTEN (ok:true). The raw guard must reject it before any parse/validate work.
    const nk = mockNk();
    const payload = JSON.stringify({
      profile_id: 'g#online', hero_def_id: 'grommash',
      values: [{ key: 'hero.level', raw: 1 }], inventory: [],
      junk_blob: 'x'.repeat(MAX_RAW_PAYLOAD_BYTES + 1024),
    });
    expect(payload.length).toBeGreaterThan(MAX_RAW_PAYLOAD_BYTES);
    const reply = JSON.parse(handleWriteHeroProfile(nk as any, USER, payload));
    expect(reply.ok).toBe(false);
    expect(reply.reason).toBe('too_large');
    expect(nk.writes).toHaveLength(0);
  });

  test('DW-436: an over-count values array is rejected too_large BEFORE validation runs', () => {
    // Empty-object entries would be rejected by the validator as `attributes` — the element-count guard must fire
    // FIRST (reason too_large), proving the O(n^2) duplicate scan is never reached for an over-count payload.
    const nk = mockNk();
    const values = [];
    for (let i = 0; i < MAX_PROFILE_ELEMENTS + 88; i++) values.push({});
    const payload = JSON.stringify({ profile_id: 'g#online', hero_def_id: 'grommash', values, inventory: [] });
    const reply = JSON.parse(handleWriteHeroProfile(nk as any, USER, payload));
    expect(reply.ok).toBe(false);
    expect(reply.reason).toBe('too_large');
    expect(nk.writes).toHaveLength(0);
  });

  test('DW-436: an over-count inventory array is rejected too_large BEFORE validation runs', () => {
    const nk = mockNk();
    const inventory = [];
    for (let i = 0; i < MAX_PROFILE_ELEMENTS + 1; i++) inventory.push({});
    const payload = JSON.stringify({
      profile_id: 'g#online', hero_def_id: 'grommash',
      values: [{ key: 'hero.level', raw: 1 }], inventory,
    });
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

  // ── DW-436: the attest path previously re-validated the stored object with NO size cap at all ──

  test('DW-436: an oversized attest REQUEST payload is rejected fail-closed before parsing', () => {
    // Before DW-436 a multi-megabyte request payload was JSON.parsed (goja CPU spent) and only THEN dispositioned
    // (this one: id mismatch → `identity`). Now it is rejected outright (too_large) before any parse.
    const nk = mockNk([seedValid('grommash#online')]);
    const huge = '{"profileId":"' + 'x'.repeat(MAX_RAW_PAYLOAD_BYTES + 1024) + '"}';
    const reply = JSON.parse(handleAttestHeroProfile(nk as any, USER, huge));
    expect(reply.attested).toBe(false);
    expect(reply.reason).toBe('too_large');
  });

  test('DW-436: a VALID but oversized STORED object (raw first-time client write) is NOT attested', () => {
    // permissionWrite=0 only protects an already-stored object, so a first-time raw WriteStorageObjects could plant
    // an object the write RPC would never store. This one is rule-VALID (unique keys, in-range ints) but far over
    // MAX_STORED_PROFILE_BYTES — before DW-436 it re-validated clean and ATTESTED. Now: fail-closed too_large.
    const planted: StoredObj = {
      collection: HERO_COLLECTION, key: HERO_KEY, userId: USER, version: 'v1',
      value: {
        profile_id: 'g#online', hero_def_id: 'grommash',
        values: [
          { key: 'hero.level', raw: 1 },
          { key: 'hero.' + 'x'.repeat(20000), raw: 1 }, // one huge (still unique) key → object >> 8192 bytes
        ],
        inventory: [],
      },
    };
    const nk = mockNk([planted]);
    const reply = JSON.parse(handleAttestHeroProfile(nk as any, USER, JSON.stringify({ profileId: 'g#online' })));
    expect(reply.attested).toBe(false);
    expect(reply.reason).toBe('too_large');
  });

  test('DW-436: an over-count STORED values array is rejected too_large BEFORE re-validation scans', () => {
    // Empty-object entries would report `attributes` from the validator — too_large proves the element-count guard
    // fired first, so the O(n^2) duplicate scans never ran over the planted object.
    const values = [];
    for (let i = 0; i < MAX_PROFILE_ELEMENTS + 1; i++) values.push({});
    const planted: StoredObj = {
      collection: HERO_COLLECTION, key: HERO_KEY, userId: USER, version: 'v1',
      value: { profile_id: 'g#online', hero_def_id: 'grommash', values, inventory: [] },
    };
    const nk = mockNk([planted]);
    const reply = JSON.parse(handleAttestHeroProfile(nk as any, USER, JSON.stringify({ profileId: 'g#online' })));
    expect(reply.attested).toBe(false);
    expect(reply.reason).toBe('too_large');
  });
});
